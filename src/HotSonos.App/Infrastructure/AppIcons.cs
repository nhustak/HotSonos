using System.Windows.Controls;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using Brush = System.Windows.Media.Brush;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Thickness = System.Windows.Thickness;
using WpfApplication = System.Windows.Application;

namespace HotSonos.App.Infrastructure;

/// <summary>
/// Loads SVG icons from <c>Assets/Icons</c> (copied from the Dev Graphics pack)
/// into WPF <see cref="ImageSource"/>s for use across the app.
/// </summary>
public static class AppIcons
{
    public const string Wifi = "wifi";
    public const string NoWifi = "no-wifi";
    public const string Ethernet = "ethernet";
    public const string Mesh = "mesh";
    public const string Speaker = "speaker";
    public const string Sub = "sub";
    public const string Amp = "amp";
    public const string Port = "port";
    public const string Star = "star";
    public const string Link = "link";
    public const string House = "house";
    public const string Settings = "settings";
    public const string Refresh = "refresh";
    public const string History = "history";
    public const string Search = "search";
    public const string Trash = "trash";
    public const string Info = "info";
    public const string Caution = "caution";
    public const string Warning = "warning";
    public const string Lightning = "lightning";
    public const string Tools = "tools";
    public const string Clear = "clear";
    public const string Discovery = "discovery";
    public const string Minus = "minus";
    public const string ArrowUp = "arrow-up";
    public const string ArrowDown = "arrow-down";
    public const string Reset = "reset";
    public const string Source = "source";

    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Load (and cache) an icon by file stem, e.g. <c>wifi</c> → <c>wifi.svg</c>.</summary>
    public static ImageSource? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var hit))
                return hit;
        }

        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Icons/{name}.svg", UriKind.Absolute);
            var streamInfo = WpfApplication.GetResourceStream(uri);
            if (streamInfo?.Stream is null)
                return null;

            using var stream = streamInfo.Stream;
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = false,
                TextAsGeometry = true,
                OptimizePath = true,
            };
            using var reader = new FileSvgReader(settings);
            var drawing = reader.Read(stream);
            if (drawing is null)
                return null;

            // Freeze for cross-thread / multi-use safety.
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();

            lock (Gate)
                Cache[name] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>WPF <see cref="Image"/> control for inline use (chips, buttons, legends).</summary>
    public static Image CreateImage(string name, double size = 14, string? toolTip = null)
    {
        var img = new Image
        {
            Source = Get(name),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        if (!string.IsNullOrWhiteSpace(toolTip))
            img.ToolTip = toolTip;
        // If SVG failed, keep a tiny placeholder so layout does not collapse.
        if (img.Source is null)
        {
            img.Width = size;
            img.Height = size;
            img.Opacity = 0.35;
        }

        return img;
    }

    /// <summary>Horizontal stack: icon(s) + label text (topology chips / legend rows).</summary>
    public static System.Windows.UIElement Row(
        string label,
        double fontSize,
        Brush foreground,
        System.Windows.FontWeight? weight = null,
        params string[] iconNames)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        foreach (var icon in iconNames)
        {
            if (string.IsNullOrWhiteSpace(icon))
                continue;
            panel.Children.Add(CreateImage(icon, size: Math.Max(12, fontSize + 2)));
        }

        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = fontSize,
            FontWeight = weight ?? System.Windows.FontWeights.Normal,
            Foreground = foreground,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextWrapping = System.Windows.TextWrapping.Wrap,
        });
        return panel;
    }

    public static string? ProductKey(string productKind) => productKind switch
    {
        "Sub" => Sub,
        "Port" => Port,
        "Amp" => Amp,
        "Bonded" => Link,
        "Speaker" => Speaker,
        _ => Speaker,
    };

    public static string? ConnectionKey(string? connectionLabel) => connectionLabel switch
    {
        "ETH" => Ethernet,
        "Wi‑Fi" => Wifi,
        _ => null,
    };
}
