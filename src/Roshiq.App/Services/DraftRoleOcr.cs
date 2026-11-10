using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Roshiq.Core.Models;
using Roshiq.Vision;
using Tesseract;

namespace Roshiq.App.Services;

/// <summary>Reads the role shown under the local player's name on the ranked draft screen.</summary>
public static class DraftRoleOcr
{
    public static Position? Read(RgbImage image, string playerName, IReadOnlyList<SlotRegion> slots)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(dataDir, "rus.traineddata")) || !File.Exists(Path.Combine(dataDir, "eng.traineddata")))
            return null;
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Rgb24,
            null, image.Data, image.Width * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        using var engine = new TesseractEngine(dataDir, "rus+eng", EngineMode.LstmOnly);
        using var pix = Pix.LoadFromMemory(stream.ToArray());
        using var page = engine.Process(pix, PageSegMode.SparseText);
        using var iterator = page.GetIterator();
        var words = new List<DraftOcrWord>();
        iterator.Begin();
        do
        {
            var word = iterator.GetText(PageIteratorLevel.Word);
            if (!string.IsNullOrWhiteSpace(word) && iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box))
                words.Add(new DraftOcrWord(word, box.X1, box.Y1, box.X2, box.Y2));
        } while (iterator.Next(PageIteratorLevel.Word));
        return DraftRoleResolver.Resolve(words, playerName, slots, image.Height);
    }
}
