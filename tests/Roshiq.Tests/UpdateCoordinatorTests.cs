using System.Net;
using System.Text;
using Roshiq.Core.Updates;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.17.4: проверка обновлений — один запрос на всех, кэш, пауза после лимита, резервный источник.</summary>
public class UpdateCoordinatorTests
{
    private const string Repo = "baramobatare-rgb/ROSHIQ";
    private const string ApiUrl = "https://api.github.com/repos/baramobatare-rgb/ROSHIQ/releases/latest";
    private const string PageUrl = "https://github.com/baramobatare-rgb/ROSHIQ/releases/latest";
    private static readonly Version Current = new(0, 17, 3);

    /// <summary>Поддельный сервер: ответы по адресу, счётчик запросов, необязательная задержка.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        public readonly Dictionary<string, int> Calls = new();
        public TaskCompletionSource? Gate;

        public int CallsTo(string url) { lock (Calls) return Calls.TryGetValue(url, out var n) ? n : 0; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Calls) Calls[url] = CallsTo(url) + 1;
            if (Gate != null) await Gate.Task;
            if (!Routes.TryGetValue(url, out var make)) throw new HttpRequestException("нет сети");
            var r = make();
            r.RequestMessage = request;
            return r;
        }
    }

    private static HttpResponseMessage Json(string tag) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""
            {"tag_name":"{{tag}}","html_url":"https://github.com/{{Repo}}/releases/tag/{{tag}}",
             "assets":[{"name":"Roshiq-Setup-{{tag.TrimStart('v')}}.exe","browser_download_url":"https://github.com/{{Repo}}/releases/download/{{tag}}/Roshiq-Setup-{{tag.TrimStart('v')}}.exe","size":123},
                       {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/{{Repo}}/releases/download/{{tag}}/SHA256SUMS.txt","size":10}]}
            """, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage RateLimited(DateTime resetUtc)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") };
        r.Headers.Add("X-RateLimit-Remaining", "0");
        r.Headers.Add("X-RateLimit-Reset", new DateTimeOffset(resetUtc).ToUnixTimeSeconds().ToString());
        return r;
    }

    private static HttpResponseMessage Redirect(string tag)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri($"https://github.com/{Repo}/releases/tag/{tag}");
        return r;
    }

    private sealed class Clock { public DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc); }

    private static (UpdateCoordinator coord, FakeHandler fake, Clock clock) Make()
    {
        var fake = new FakeHandler();
        var clock = new Clock();
        var http = new HttpClient(fake);
        var checker = new ReleaseChecker(http, http, () => clock.Now);
        return (new UpdateCoordinator(checker, () => Current, () => clock.Now), fake, clock);
    }

    [Fact]
    public async Task ConcurrentChecks_ShareOneRequest()
    {
        var (coord, fake, _) = Make();
        fake.Routes[ApiUrl] = () => Json("v0.17.4");
        fake.Gate = new TaskCompletionSource();
        var a = coord.CheckAsync(Repo, manual: false);
        var b = coord.CheckAsync(Repo, manual: true);
        var c = coord.CheckAsync("https://github.com/" + Repo, manual: false);
        fake.Gate.SetResult();
        var results = await Task.WhenAll(a, b, c);
        Assert.Equal(1, fake.CallsTo(ApiUrl));
        Assert.All(results, r => Assert.Equal(UpdateCheckStatus.Available, r.Status));
        Assert.Equal(new Version(0, 17, 4), results[0].Release!.Version);
        Assert.Equal(ReleaseSource.Api, results[0].Release!.Source);
    }

    [Fact]
    public async Task SuccessIsCached_ManualRefreshesAfterOneMinute()
    {
        var (coord, fake, clock) = Make();
        fake.Routes[ApiUrl] = () => Json("v0.17.3");
        Assert.Equal(UpdateCheckStatus.UpToDate, (await coord.CheckAsync(Repo, false)).Status);
        clock.Now += TimeSpan.FromSeconds(30);
        var cached = await coord.CheckAsync(Repo, true);
        Assert.True(cached.FromCache);
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.True((await coord.CheckAsync(Repo, false)).FromCache);        // фоновая — кэш 10 минут
        Assert.False((await coord.CheckAsync(Repo, true)).FromCache);        // ручная — прошла минута
        Assert.Equal(2, fake.CallsTo(ApiUrl));
    }

    [Fact]
    public async Task RateLimit_PausesApi_EvenForManual_AndUsesReleasesPage()
    {
        var (coord, fake, clock) = Make();
        var reset = clock.Now.AddMinutes(40);
        fake.Routes[ApiUrl] = () => RateLimited(reset);
        fake.Routes[PageUrl] = () => Redirect("v0.17.4");

        var r = await coord.CheckAsync(Repo, manual: true);
        Assert.Equal(UpdateCheckStatus.Available, r.Status);
        Assert.Equal(ReleaseSource.ReleasesPage, r.Release!.Source);
        Assert.Equal($"https://github.com/{Repo}/releases/download/v0.17.4/Roshiq-Setup-0.17.4.exe", r.Release.InstallerUrl);
        Assert.Equal($"https://github.com/{Repo}/releases/download/v0.17.4/SHA256SUMS.txt", r.Release.ChecksumsUrl);
        Assert.Equal(reset, coord.ApiBlockedUntilUtc!.Value, TimeSpan.FromSeconds(1));

        // через 2 минуты ручная проверка: API в паузе не трогаем, спрашиваем только страницу
        clock.Now += TimeSpan.FromMinutes(2);
        await coord.CheckAsync(Repo, manual: true);
        Assert.Equal(1, fake.CallsTo(ApiUrl));
        Assert.Equal(2, fake.CallsTo(PageUrl));

        // после сброса лимита API снова спрашивается
        clock.Now = reset.AddMinutes(1);
        fake.Routes[ApiUrl] = () => Json("v0.17.4");
        var after = await coord.CheckAsync(Repo, manual: true);
        Assert.Equal(2, fake.CallsTo(ApiUrl));
        Assert.Equal(ReleaseSource.Api, after.Release!.Source);
    }

    [Fact]
    public async Task BothSourcesDown_ReportsLimitWithTime_AndDoesNotHammer()
    {
        var (coord, fake, clock) = Make();
        var reset = clock.Now.AddMinutes(30);
        fake.Routes[ApiUrl] = () => RateLimited(reset);
        // страницы в маршрутах нет — «нет сети»

        var r = await coord.CheckAsync(Repo, manual: true);
        Assert.Equal(UpdateCheckStatus.RateLimited, r.Status);
        Assert.Equal(reset, r.RetryAfterUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Null(r.Release);

        // десять нажатий подряд — ни одного нового запроса
        for (int i = 0; i < 10; i++)
        {
            var again = await coord.CheckAsync(Repo, manual: true);
            Assert.True(again.FromCache);
            Assert.Equal(UpdateCheckStatus.RateLimited, again.Status);
        }
        Assert.Equal(1, fake.CallsTo(ApiUrl));
        Assert.Equal(1, fake.CallsTo(PageUrl));
    }

    [Fact]
    public async Task Status429_IsRateLimit_WithRetryAfter()
    {
        var (coord, fake, clock) = Make();
        fake.Routes[ApiUrl] = () =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return r;
        };
        var result = await coord.CheckAsync(Repo, false);
        Assert.Equal(UpdateCheckStatus.RateLimited, result.Status);
        Assert.Equal(clock.Now.AddSeconds(90), result.RetryAfterUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Plain403_IsForbidden_NotRateLimit()
    {
        var (coord, fake, _) = Make();
        fake.Routes[ApiUrl] = () => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"Forbidden\"}") };
        fake.Routes[PageUrl] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var r = await coord.CheckAsync(Repo, false);
        Assert.Equal(UpdateCheckStatus.Forbidden, r.Status);
        Assert.Null(coord.ApiBlockedUntilUtc);
    }

    [Fact]
    public async Task NetworkDown_IsNetworkError_RetriedAfterTwoMinutes()
    {
        var (coord, fake, clock) = Make();
        var r = await coord.CheckAsync(Repo, true);
        Assert.Equal(UpdateCheckStatus.NetworkError, r.Status);
        Assert.True((await coord.CheckAsync(Repo, true)).FromCache);
        clock.Now += UpdateCoordinator.FailureRetry + TimeSpan.FromSeconds(1);
        fake.Routes[ApiUrl] = () => Json("v0.17.3");
        Assert.Equal(UpdateCheckStatus.UpToDate, (await coord.CheckAsync(Repo, true)).Status);
    }

    [Fact]
    public async Task ReleasesPage_SameVersion_IsUpToDate()
    {
        var (coord, fake, _) = Make();
        fake.Routes[ApiUrl] = () => RateLimited(DateTime.UtcNow.AddHours(1));
        fake.Routes[PageUrl] = () => Redirect("v0.17.3");
        Assert.Equal(UpdateCheckStatus.UpToDate, (await coord.CheckAsync(Repo, false)).Status);
    }

    [Theory]
    [InlineData("v0.17.4", 0, 17, 4)]
    [InlineData("0.18", 0, 18, 0)]
    [InlineData("v1.2.3-beta", 1, 2, 3)]
    public void ParseTag(string tag, int a, int b, int c) => Assert.Equal(new Version(a, b, c), ReleaseChecker.ParseTag(tag));

    // ------------------------------------------------------------------ контрольная сумма обязательна

    private static ReleaseInfo Info(string? sumsUrl) => new(new Version(0, 17, 4), "v0.17.4", "https://github.com/x/y",
        "https://github.com/x/y/releases/download/v0.17.4/Roshiq-Setup-0.17.4.exe", "Roshiq-Setup-0.17.4.exe", 0, sumsUrl, ReleaseSource.ReleasesPage);

    private static async Task<(string path, string hash)> TempFile(string content)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, content);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return (path, hash);
    }

    [Fact]
    public async Task Checksum_MissingSums_Refuses()
    {
        var (path, _) = await TempFile("installer");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InstallerChecksum.VerifyAsync(Info(null), path, new HttpClient(new FakeHandler()), CancellationToken.None));
            Assert.Contains("SHA256SUMS", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Checksum_SumsUnavailable_Refuses()
    {
        var (path, _) = await TempFile("installer");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InstallerChecksum.VerifyAsync(Info("https://github.com/x/y/releases/download/v0.17.4/SHA256SUMS.txt"), path,
                    new HttpClient(new FakeHandler()), CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Checksum_MatchPasses_MismatchRefuses()
    {
        var (path, hash) = await TempFile("installer");
        const string url = "https://github.com/x/y/releases/download/v0.17.4/SHA256SUMS.txt";
        try
        {
            var ok = new FakeHandler();
            ok.Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  Roshiq-Setup-0.17.4.exe\n") };
            await InstallerChecksum.VerifyAsync(Info(url), path, new HttpClient(ok), CancellationToken.None);

            var bad = new FakeHandler();
            bad.Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{new string('0', 64)}  Roshiq-Setup-0.17.4.exe\n") };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InstallerChecksum.VerifyAsync(Info(url), path, new HttpClient(bad), CancellationToken.None));
        }
        finally { File.Delete(path); }
    }
}
