using Roshiq.Core.Layout;
using Roshiq.Vision;

namespace Roshiq.Overlay;

/// <summary>
/// Разовый захват области экрана через GDI BitBlt (то, что видит игрок на мониторе).
/// Не обращается к процессу игры. Окна Roshiq исключены из захвата (WDA_EXCLUDEFROMCAPTURE).
/// В эксклюзивном полноэкранном режиме кадр может быть чёрным — используйте оконный/безрамочный режим.
/// </summary>
public static class ScreenCapture
{
    public static RgbImage? Capture(PixelRect area)
    {
        if (area.IsEmpty) return null;
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = Native.GetDC(IntPtr.Zero);
            memDc = Native.CreateCompatibleDC(screenDc);
            bmp = Native.CreateCompatibleBitmap(screenDc, area.Width, area.Height);
            old = Native.SelectObject(memDc, bmp);
            if (!Native.BitBlt(memDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, Native.SRCCOPY | Native.CAPTUREBLT))
                return null;
            Native.SelectObject(memDc, old);
            old = IntPtr.Zero;

            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = area.Width,
                biHeight = -area.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };
            var buffer = new byte[area.Width * area.Height * 4];
            int lines = Native.GetDIBits(memDc, bmp, 0, (uint)area.Height, buffer, ref bmi, 0);
            if (lines == 0) return null;
            return RgbImage.FromBgra(buffer, area.Width, area.Height, area.Width * 4);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero && memDc != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
            if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
