using System.Globalization;
using System.Text.Json;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Core.Updates;

/// <summary>Откуда узнали о релизе.</summary>
public enum ReleaseSource { Api, ReleasesPage }

/// <summary>Свежий релиз на GitHub.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, string? InstallerName,
    long InstallerSize, string? ChecksumsUrl, ReleaseSource Source);

/// <summary>Чем закончилась проверка. Разные причины отказа различаются — интерфейс пишет честную причину.</summary>
public enum UpdateCheckStatus
{
    /// <summary>Новее текущей версии ничего нет.</summary>
    UpToDate,
    /// <summary>Есть новая версия (<see cref="UpdateCheckResult.Release"/>).</summary>
    Available,
    /// <summary>GitHub ограничил число запросов (403 с исчерпанным лимитом или 429). До <see cref="UpdateCheckResult.RetryAfterUtc"/> не спрашиваем.</summary>
    RateLimited,
    /// <summary>Обычный отказ в доступе (403 без признаков лимита).</summary>
    Forbidden,
    /// <summary>Репозиторий или релизы не найдены (404).</summary>
    NotFound,
    /// <summary>Нет сети, таймаут, обрыв соединения, ошибка сервера.</summary>
    NetworkError,
    /// <summary>Ответ пришёл, но его не удалось разобрать.</summary>
    Invalid
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release, DateTime CheckedUtc,
    DateTime? RetryAfterUtc = null, string? Message = null, bool FromCache = false)
{
    public bool Succeeded => Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.Available;
}

/// <summary>
/// Один запрос к источнику релизов без кэша и повторов — их делает <see cref="UpdateCoordinator"/>.
/// Основной источник — api.github.com; резервный — страница github.com/{repo}/releases/latest
/// (тег берётся из перенаправления, ссылки на установщик и SHA256SUMS.txt строятся по шаблону релиза).
/// </summary>
public sealed class ReleaseChecker
{
    public const string InstallerPrefix = "Roshiq-Setup-";
    public const string ChecksumsName = "SHA256SUMS.txt";
    /// <summary>Пауза после лимита, если GitHub не прислал время сброса.</summary>
    public static readonly TimeSpan DefaultLimitPause = TimeSpan.FromMinutes(15);

    private readonly HttpClient _api;
    private readonly HttpClient _page;
    private readonly Func<DateTime> _now;

    /// <param name="api">Клиент для api.github.com.</param>
    /// <param name="page">Клиент для страницы релизов — желательно без автоперехода по перенаправлениям.</param>
    public ReleaseChecker(HttpClient api, HttpClient page, Func<DateTime>? now = null)
    {
        _api = api;
        _page = page;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Клиенты для боевой работы: User-Agent, таймаут, страница без автоперехода.</summary>
    public static ReleaseChecker CreateDefault(Version current)
    {
        HttpClient Make(bool redirects)
        {
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Roshiq/" + current);
            return http;
        }
        var api = Make(true);
        api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return new ReleaseChecker(api, Make(false));
    }

    /// <summary>«owner/repo» из строки или ссылки github.com/owner/repo.</summary>
    public static string? NormalizeRepo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimEnd('/');
        const string marker = "github.com/";
        int i = t.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) t = t[(i + marker.Length)..];
        var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        static bool Ok(string s) => s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
        return Ok(parts[0]) && Ok(parts[1]) ? $"{parts[0]}/{parts[1]}" : null;
    }

    /// <summary>«v0.3.2» / «0.3.2-beta» → 0.3.2.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) t = t[..cut];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    // ------------------------------------------------------------------ основной источник

    public async Task<UpdateCheckResult> CheckApiAsync(string repo, Version current, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        try
        {
            using var response = await _api.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeBody(response, ct);
                var failed = Classify(response, body);
                NetworkLog.Record("GET", url, $"{(int)response.StatusCode} → {failed.Status}");
                return failed;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var tag = Str(root, "tag_name");
            var version = ParseTag(tag);
            NetworkLog.Record("GET", url, "OK " + (tag ?? "?"));
            if (version == null) return new UpdateCheckResult(UpdateCheckStatus.Invalid, null, _now(), Message: "нет тега релиза");
            if (version <= current) return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, _now());

            string? installer = null, name = null, sums = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var n = Str(a, "name");
                    if (n == null) continue;
                    if (n.Equals(ChecksumsName, StringComparison.OrdinalIgnoreCase)) { sums = Str(a, "browser_download_url"); continue; }
                    if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!n.Contains("setup", StringComparison.OrdinalIgnoreCase) && installer != null) continue;
                    installer = Str(a, "browser_download_url");
                    name = n;
                    size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var l) ? l : 0;
                }
            }
            var page = Str(root, "html_url") ?? $"https://github.com/{repo}/releases/tag/{tag}";
            return new UpdateCheckResult(UpdateCheckStatus.Available,
                new ReleaseInfo(version, tag!, page, installer, name, size, sums, ReleaseSource.Api), _now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (JsonException ex)
        {
            NetworkLog.Record("GET", url, "ошибка разбора: " + ex.Message);
            return new UpdateCheckResult(UpdateCheckStatus.Invalid, null, _now(), Message: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            NetworkLog.Record("GET", url, "ошибка сети: " + ex.Message);
            return new UpdateCheckResult(UpdateCheckStatus.NetworkError, null, _now(), Message: ex.Message);
        }
    }

    /// <summary>Разобрать неуспешный ответ: лимит (403 с исчерпанным лимитом / 429), отказ, 404, ошибка сервера.</summary>
    public UpdateCheckResult Classify(HttpResponseMessage response, string? body)
    {
        var now = _now();
        int code = (int)response.StatusCode;
        bool exhausted = Header(response, "X-RateLimit-Remaining") == "0";
        bool limitText = body != null && body.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        if (code == 429 || (code == 403 && (exhausted || limitText || response.Headers.RetryAfter != null)))
            return new UpdateCheckResult(UpdateCheckStatus.RateLimited, null, now, RetryAfter(response, now), "rate limit");
        if (code == 403) return new UpdateCheckResult(UpdateCheckStatus.Forbidden, null, now, Message: "403");
        if (code == 404) return new UpdateCheckResult(UpdateCheckStatus.NotFound, null, now, Message: "404");
        return new UpdateCheckResult(UpdateCheckStatus.NetworkError, null, now, Message: code.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Когда можно спросить снова: Retry-After, затем X-RateLimit-Reset (секунды Unix), иначе пауза по умолчанию.</summary>
    public static DateTime RetryAfter(HttpResponseMessage response, DateTime now)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is TimeSpan delta && delta > TimeSpan.Zero) return now + delta;
        if (ra?.Date is DateTimeOffset date && date.UtcDateTime > now) return date.UtcDateTime;
        if (long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            if (reset > now) return reset;
        }
        return now + DefaultLimitPause;
    }

    // ------------------------------------------------------------------ резервный источник

    /// <summary>
    /// github.com/{repo}/releases/latest перенаправляет на /releases/tag/{tag}. Ссылки на установщик и суммы
    /// строятся по шаблону нашего релиза; размер неизвестен. Контрольная сумма при установке обязательна.
    /// </summary>
    public async Task<UpdateCheckResult> CheckReleasesPageAsync(string repo, Version current, CancellationToken ct)
    {
        var url = $"https://github.com/{repo}/releases/latest";
        try
        {
            using var response = await _page.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)response.StatusCode;
            Uri? target = code is >= 300 and < 400 ? response.Headers.Location
                : response.IsSuccessStatusCode ? response.RequestMessage?.RequestUri : null;
            if (target != null && !target.IsAbsoluteUri) target = new Uri(new Uri("https://github.com"), target);
            if (target == null)
            {
                var failed = code is >= 300 and < 400 ? new UpdateCheckResult(UpdateCheckStatus.Invalid, null, _now(), Message: "нет адреса перенаправления")
                    : Classify(response, null);
                NetworkLog.Record("GET", url, $"{code} → {failed.Status}");
                return failed;
            }
            const string marker = "/releases/tag/";
            var path = Uri.UnescapeDataString(target.AbsolutePath);
            int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var tag = i >= 0 ? path[(i + marker.Length)..].Trim('/') : null;
            var version = ParseTag(tag);
            NetworkLog.Record("GET", url, $"{code} → {tag ?? "?"}");
            if (version == null) return new UpdateCheckResult(UpdateCheckStatus.Invalid, null, _now(), Message: "тег не найден в адресе");
            if (version <= current) return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, _now());
            var baseUrl = $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag!)}/";
            var name = $"{InstallerPrefix}{version.ToString(3)}.exe";
            return new UpdateCheckResult(UpdateCheckStatus.Available,
                new ReleaseInfo(version, tag!, $"https://github.com/{repo}/releases/tag/{Uri.EscapeDataString(tag!)}",
                    baseUrl + name, name, 0, baseUrl + ChecksumsName, ReleaseSource.ReleasesPage), _now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            NetworkLog.Record("GET", url, "ошибка сети: " + ex.Message);
            return new UpdateCheckResult(UpdateCheckStatus.NetworkError, null, _now(), Message: ex.Message);
        }
    }

    // ------------------------------------------------------------------ мелочи

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static async Task<string?> SafeBody(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            var s = await r.Content.ReadAsStringAsync(ct);
            return s.Length > 2000 ? s[..2000] : s;
        }
        catch { return null; }
    }
}
