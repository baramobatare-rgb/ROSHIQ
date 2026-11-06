using System.Net;
using System.Net.Sockets;
using System.Text;
using Roshiq.Core.Diagnostics;

namespace Roshiq.GSI;

/// <summary>
/// Минимальный HTTP-приёмник на 127.0.0.1 для POST-запросов Game State Integration.
/// Слушает только loopback, не требует прав администратора и URL ACL.
/// Любая ошибка обработки запроса логируется и не влияет на игру.
/// </summary>
public sealed class GsiServer : IDisposable
{
    private const int MaxBody = 1024 * 1024;
    private readonly int _port;
    private readonly string? _token;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public GsiServer(int port, string? token)
    {
        _port = port;
        _token = token;
    }

    public int Port => _port;
    public bool IsRunning => _listener != null;
    public DateTime? LastPacketUtc { get; private set; }
    public long PacketsReceived => Interlocked.Read(ref _packets);
    public long PacketsRejected => Interlocked.Read(ref _rejected);
    public GameStateSnapshot? Last { get; private set; }
    public string? LastError { get; private set; }

    private long _packets;
    private long _rejected;

    public event EventHandler<GameStateSnapshot>? SnapshotReceived;

    public void Start()
    {
        if (_listener != null) return;
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _loop = Task.Run(() => AcceptLoop(_cts.Token));
            Log.Info($"GSI: слушаю http://127.0.0.1:{_port}/");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error($"GSI: не удалось открыть порт {_port}", ex);
            _listener = null;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { LastError = ex.Message; break; }
            _ = Task.Run(() => HandleClient(client, ct), ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                var body = await ReadRequestBody(stream, ct).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct).ConfigureAwait(false);
                if (body == null) return;
                ProcessBody(body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex.Message;
                Log.Warn("GSI: ошибка обработки запроса: " + ex.Message);
            }
        }
    }

    /// <summary>Разбор и публикация пакета (публично для тестов и ручной подачи данных).</summary>
    public GameStateSnapshot? ProcessBody(string body)
    {
        GameStateSnapshot snap;
        try { snap = GsiParser.Parse(body, _token); }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _rejected);
            LastError = "JSON: " + ex.Message;
            return null;
        }
        if (!snap.AuthValid)
        {
            Interlocked.Increment(ref _rejected);
            LastError = "неверный токен GSI";
            return null;
        }
        Interlocked.Increment(ref _packets);
        LastPacketUtc = snap.ReceivedUtc;
        Last = snap;
        try { SnapshotReceived?.Invoke(this, snap); }
        catch (Exception ex) { Log.Error("GSI: ошибка в обработчике", ex); }
        return snap;
    }

    internal static async Task<string?> ReadRequestBody(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) return null;
            ms.Write(buffer, 0, n);
            if (ms.Length > 64 * 1024) return null; // слишком длинные заголовки
            headerEnd = IndexOfHeaderEnd(ms.GetBuffer(), (int)ms.Length);
        }

        var all = ms.GetBuffer();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        if (!headerText.StartsWith("POST", StringComparison.OrdinalIgnoreCase)) return null;

        int contentLength = 0;
        foreach (var line in headerText.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx > 0 && line[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(idx + 1)..].Trim(), out contentLength);
        }
        if (contentLength <= 0 || contentLength > MaxBody) return null;

        int bodyStart = headerEnd + 4;
        var body = new byte[contentLength];
        int have = Math.Min(contentLength, (int)ms.Length - bodyStart);
        Array.Copy(all, bodyStart, body, 0, have);
        while (have < contentLength)
        {
            int n = await stream.ReadAsync(body.AsMemory(have, contentLength - have), ct).ConfigureAwait(false);
            if (n <= 0) return null;
            have += n;
        }
        return Encoding.UTF8.GetString(body);
    }

    private static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        return -1;
    }
}
