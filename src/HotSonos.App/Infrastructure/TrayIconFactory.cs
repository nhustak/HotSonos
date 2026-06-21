using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HotSonos.App.Infrastructure;

/// <summary>Draws the HotSonos tray icon: a green play triangle on a dark disc.</summary>
public static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        using var outerBrush = new SolidBrush(Color.FromArgb(28, 37, 54));
        using var accentBrush = new SolidBrush(Color.FromArgb(46, 204, 113));

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        graphics.FillEllipse(outerBrush, 1, 1, 30, 30);

        // Play triangle, optically centred.
        var triangle = new[]
        {
            new PointF(12f, 9f),
            new PointF(12f, 23f),
            new PointF(24f, 16f),
        };
        graphics.FillPolygon(accentBrush, triangle);

        var handle = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(handle);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
