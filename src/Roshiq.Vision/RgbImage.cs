namespace Roshiq.Vision;

/// <summary>Простое RGB24-изображение в памяти (без зависимостей от GDI/WPF — тестируется на любой ОС).</summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height, byte[]? data = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 3];
        if (Data.Length != width * height * 3) throw new ArgumentException("Неверный размер буфера", nameof(data));
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Пиксели построчно: R,G,B.</summary>
    public byte[] Data { get; }

    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 3;
        return (Data[i], Data[i + 1], Data[i + 2]);
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        int i = (y * Width + x) * 3;
        Data[i] = r; Data[i + 1] = g; Data[i + 2] = b;
    }

    /// <summary>Из буфера BGRA (формат GDI/WPF), stride в байтах.</summary>
    public static RgbImage FromBgra(byte[] bgra, int width, int height, int stride)
    {
        var img = new RgbImage(width, height);
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            int dst = y * width * 3;
            for (int x = 0; x < width; x++, src += 4, dst += 3)
            {
                img.Data[dst] = bgra[src + 2];
                img.Data[dst + 1] = bgra[src + 1];
                img.Data[dst + 2] = bgra[src];
            }
        }
        return img;
    }

    public RgbImage Crop(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        w = Math.Clamp(w, 1, Width - x);
        h = Math.Clamp(h, 1, Height - y);
        var img = new RgbImage(w, h);
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Data, ((y + row) * Width + x) * 3, img.Data, row * w * 3, w * 3);
        return img;
    }

    /// <summary>Уменьшение усреднением по областям (устойчиво к шуму и масштабу 1080p/1440p).</summary>
    public RgbImage ResizeArea(int newW, int newH)
    {
        var img = new RgbImage(newW, newH);
        double sx = (double)Width / newW, sy = (double)Height / newH;
        for (int ty = 0; ty < newH; ty++)
        {
            int y0 = (int)(ty * sy), y1 = Math.Max(y0 + 1, (int)((ty + 1) * sy));
            for (int tx = 0; tx < newW; tx++)
            {
                int x0 = (int)(tx * sx), x1 = Math.Max(x0 + 1, (int)((tx + 1) * sx));
                long r = 0, g = 0, b = 0; int n = 0;
                for (int y = y0; y < y1 && y < Height; y++)
                    for (int x = x0; x < x1 && x < Width; x++)
                    {
                        int i = (y * Width + x) * 3;
                        r += Data[i]; g += Data[i + 1]; b += Data[i + 2]; n++;
                    }
                if (n == 0) n = 1;
                img.SetPixel(tx, ty, (byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }
        return img;
    }

    /// <summary>Стандартное отклонение яркости — признак "пустой" ячейки.</summary>
    public double LumaStdDev()
    {
        double sum = 0, sum2 = 0; int n = Width * Height;
        for (int i = 0; i < Data.Length; i += 3)
        {
            double l = 0.299 * Data[i] + 0.587 * Data[i + 1] + 0.114 * Data[i + 2];
            sum += l; sum2 += l * l;
        }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
    }

    // ---- простой бинарный формат шаблонов (.dpt): "DPT1", w, h, RGB ----

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("DPT1"u8.ToArray());
        bw.Write(Width);
        bw.Write(Height);
        bw.Write(Data);
    }

    public static RgbImage Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Load(fs);
    }

    public static RgbImage Load(Stream stream)
    {
        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var magic = br.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != 'D' || magic[1] != 'P' || magic[2] != 'T' || magic[3] != '1')
            throw new InvalidDataException("Не шаблон Roshiq");
        int w = br.ReadInt32(), h = br.ReadInt32();
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) throw new InvalidDataException("Неверный размер шаблона");
        var data = br.ReadBytes(w * h * 3);
        return new RgbImage(w, h, data);
    }
}
