namespace Roshiq.Core.Updates;

/// <summary>
/// Единая точка проверки обновлений для всей программы (фоновая проверка, кнопка в шапке, раздел «Обновления»).
/// <list type="bullet">
/// <item>Одновременные вызовы получают одну и ту же задачу — к GitHub уходит один запрос.</item>
/// <item>Удачный ответ кэшируется: 10 минут для фоновой проверки, 1 минута для ручной.</item>
/// <item>После лимита API (403 с исчерпанным лимитом / 429) API не спрашиваем до времени сброса — и ручная кнопка
/// эту паузу не обходит; в это время пробуем только страницу релизов.</item>
/// <item>Неудача (оба источника недоступны) повторяется не чаще раза в 2 минуты.</item>
/// </list>
/// </summary>
public sealed class UpdateCoordinator
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ManualMinInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(2);

    private readonly ReleaseChecker _checker;
    private readonly Func<Version> _current;
    private readonly Func<DateTime> _now;
    private readonly object _sync = new();
    private Task<UpdateCheckResult>? _inflight;
    private string? _inflightRepo;
    private UpdateCheckResult? _last;
    private string? _lastRepo;
    private DateTime? _apiBlockedUntil;

    public UpdateCoordinator(ReleaseChecker checker, Func<Version> current, Func<DateTime>? now = null)
    {
        _checker = checker;
        _current = current;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Последний результат (в том числе неудачный).</summary>
    public UpdateCheckResult? Last { get { lock (_sync) return _last; } }

    /// <summary>До какого времени API GitHub не спрашиваем (после лимита).</summary>
    public DateTime? ApiBlockedUntilUtc
    {
        get { lock (_sync) return _apiBlockedUntil is DateTime t && t > _now() ? t : null; }
    }

    /// <summary>Новый результат получен (не из кэша).</summary>
    public event EventHandler<UpdateCheckResult>? Checked;

    public Task<UpdateCheckResult> CheckAsync(string repoText, bool manual, CancellationToken ct = default)
    {
        var repo = ReleaseChecker.NormalizeRepo(repoText) ?? throw new FormatException("Укажите репозиторий в виде owner/repo");
        lock (_sync)
        {
            if (_inflight is { IsCompleted: false } running && _inflightRepo == repo) return running;
            var now = _now();
            if (_last != null && _lastRepo == repo)
            {
                var age = now - _last.CheckedUtc;
                if (_last.Succeeded && age < (manual ? ManualMinInterval : CacheTtl))
                    return Task.FromResult(_last with { FromCache = true });
                if (!_last.Succeeded && age < FailureRetry)
                    return Task.FromResult(_last with { FromCache = true });
            }
            _inflightRepo = repo;
            _inflight = RunAsync(repo, ct);
            return _inflight;
        }
    }

    private async Task<UpdateCheckResult> RunAsync(string repo, CancellationToken ct)
    {
        await Task.Yield();   // не держать вызывающий поток и не завершаться синхронно внутри lock
        var current = _current();
        UpdateCheckResult? api = null;
        DateTime? blocked;
        lock (_sync) blocked = _apiBlockedUntil is DateTime t && t > _now() ? t : null;

        if (blocked == null)
        {
            api = await _checker.CheckApiAsync(repo, current, ct);
            if (api.Succeeded || api.Status is UpdateCheckStatus.NotFound or UpdateCheckStatus.Invalid) return Finish(repo, api);
            if (api.Status == UpdateCheckStatus.RateLimited)
            {
                blocked = api.RetryAfterUtc ?? _now() + ReleaseChecker.DefaultLimitPause;
                lock (_sync) _apiBlockedUntil = blocked;
            }
        }

        // API недоступен или в паузе — резервный источник
        var page = await _checker.CheckReleasesPageAsync(repo, current, ct);
        if (page.Succeeded) return Finish(repo, page);

        // оба не ответили: честно сообщаем главную причину
        var result = blocked != null
            ? new UpdateCheckResult(UpdateCheckStatus.RateLimited, null, _now(), blocked, $"API: лимит; страница релизов: {page.Status}")
            : api! with { Message = $"API: {api.Status}; страница релизов: {page.Status}" };
        return Finish(repo, result);
    }

    private UpdateCheckResult Finish(string repo, UpdateCheckResult result)
    {
        lock (_sync)
        {
            _last = result;
            _lastRepo = repo;
        }
        Checked?.Invoke(this, result);
        return result;
    }
}
