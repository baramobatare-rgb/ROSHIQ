using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;
using Roshiq.Core.Diagnostics;
using Roshiq.Data;

namespace Roshiq.App.Services;

/// <summary>
/// Привязка Steam-аккаунта. Три способа, все без пароля:
/// 1) из игры — Dota передаёт Steam ID в Game State Integration (делает AppHost);
/// 2) из установленного Steam — список аккаунтов в config\loginusers.vdf;
/// 3) «Войти через Steam» — официальная страница входа steamcommunity.com (OpenID), ответ принимается на 127.0.0.1.
/// </summary>
public static class SteamLink
{
    /// <summary>Папка Steam из реестра (HKCU\Software\Valve\Steam\SteamPath) или null.</summary>
    public static string? SteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return p.Replace('/', '\\');
        }
        catch (Exception ex) { Log.Warn("Steam не найден в реестре: " + ex.Message); }
        foreach (var p in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            if (Directory.Exists(p)) return p;
        return null;
    }

    /// <summary>Аккаунты, входившие в Steam на этом компьютере (последний — первым).</summary>
    public static IReadOnlyList<LocalSteamUser> LocalUsers()
    {
        try
        {
            var root = SteamPath();
            if (root == null) return Array.Empty<LocalSteamUser>();
            var file = Path.Combine(root, "config", "loginusers.vdf");
            return File.Exists(file) ? SteamLoginUsers.Parse(File.ReadAllText(file)) : Array.Empty<LocalSteamUser>();
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось прочитать аккаунты Steam: " + ex.Message);
            return Array.Empty<LocalSteamUser>();
        }
    }

    /// <summary>
    /// Вход через Steam: открывает страницу входа в браузере и ждёт ответа (до 3 минут).
    /// Возвращает проверенный у Steam Steam ID64 или null (отмена, тайм-аут, отказ проверки).
    /// </summary>
    public static async Task<string?> SignInAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string realm = $"http://127.0.0.1:{port}/";
            string returnTo = realm + "steam";
            Process.Start(new ProcessStartInfo(SteamOpenId.LoginUrl(returnTo, realm)) { UseShellExecute = true });
            NetworkLog.Record("OPEN", SteamOpenId.Endpoint, "страница входа Steam в браузере");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, timeout.Token);
                if (target == null || !target.StartsWith("/steam", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "", timeout.Token);
                    continue; // favicon и прочее
                }
                var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
                var response = SteamOpenId.ParseQuery(query);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                string? id = null;
                try { id = await SteamOpenId.VerifyAsync(response, returnTo, http, timeout.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Проверка входа Steam: " + ex.Message); }
                await RespondAsync(stream, 200, id != null
                    ? Page("Steam привязан к ROSHIQ", "Можно закрыть эту вкладку и вернуться в программу.")
                    : Page("Не получилось", "Steam не подтвердил вход. Вернитесь в ROSHIQ и попробуйте ещё раз."), timeout.Token);
                return id;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn("Вход через Steam: " + ex.Message);
            return null;
        }
        finally { listener.Stop(); }
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n")) break;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, total);
        var line = head.Split("\r\n")[0].Split(' ');
        return line.Length >= 2 && line[0] == "GET" ? line[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int code, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                           $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    private static string Page(string title, string text) =>
        "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>ROSHIQ</title>" +
        "<body style=\"margin:0;height:100vh;display:grid;place-items:center;background:#0b0f0d;color:#e8efe9;font-family:Segoe UI,sans-serif\">" +
        $"<div style=\"text-align:center\"><div style=\"font-size:13px;letter-spacing:.2em;color:#7ee081\">ROSHIQ</div>" +
        $"<h1 style=\"font-weight:600\">{WebUtility.HtmlEncode(title)}</h1><p style=\"color:#9aa59d\">{WebUtility.HtmlEncode(text)}</p></div></body></html>";
}
