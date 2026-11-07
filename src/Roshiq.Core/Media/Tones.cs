namespace Roshiq.Core.Media;

/// <summary>
/// Короткие звуки уведомлений, синтезированные в коде (WAV, 16 бит, моно) — без сторонних файлов.
/// soft — мягкий щелчок, chime — два тона вверх, ping — один высокий, alert — два коротких сигнала.
/// </summary>
public static class Tones
{
    public const int SampleRate = 22050;

    public static readonly IReadOnlyList<string> Names = new[] { "soft", "chime", "ping", "alert" };

    /// <summary>WAV-файл для звука name; null — "none" или неизвестное имя.</summary>
    public static byte[]? Wav(string name) => name switch
    {
        "soft" => Build((520, 90)),
        "chime" => Build((660, 110), (990, 170)),
        "ping" => Build((1320, 140)),
        "alert" => Build((980, 90), (0, 60), (980, 90)),
        _ => null
    };

    /// <summary>Последовательность тонов: (частота Гц, длительность мс). Частота 0 — пауза.</summary>
    public static byte[] Build(params (double Freq, int Ms)[] parts)
    {
        var samples = new List<short>();
        foreach (var (freq, ms) in parts)
        {
            int n = SampleRate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SampleRate;
                // плавные атака и затухание — без щелчков
                double env = Math.Min(1, i / (SampleRate * 0.008)) * Math.Exp(-3.0 * i / Math.Max(1, n));
                double v = freq <= 0 ? 0 : Math.Sin(2 * Math.PI * freq * t) * env * 0.55
                                           + Math.Sin(4 * Math.PI * freq * t) * env * 0.12;
                samples.Add((short)Math.Round(Math.Clamp(v, -1, 1) * short.MaxValue));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int dataBytes = samples.Count * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);              // размер блока fmt
        w.Write((short)1);        // PCM
        w.Write((short)1);        // моно
        w.Write(SampleRate);
        w.Write(SampleRate * 2);  // байт в секунду
        w.Write((short)2);        // выравнивание блока
        w.Write((short)16);       // бит на сэмпл
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }
}
