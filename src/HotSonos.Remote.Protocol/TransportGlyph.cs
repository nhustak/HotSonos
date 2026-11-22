namespace HotSonos.Remote.Protocol;

/// <summary>
/// Skip-previous and skip-next marks in a unit square (origin top-left).
/// Previous is the horizontal mirror of next: a bar on the left and a triangle pointing left.
/// </summary>
public static class TransportGlyph
{
    public readonly record struct Point(float X, float Y);

    public static Point MirrorX(Point point) => new(1f - point.X, point.Y);

    /// <summary>Right-pointing triangle. The lone x is the tip.</summary>
    public static IReadOnlyList<Point> NextTriangle { get; } =
    [
        new(0.12f, 0.16f),
        new(0.66f, 0.50f),
        new(0.12f, 0.84f),
    ];

    /// <summary>Vertical bar to the right of <see cref="NextTriangle"/>.</summary>
    public static (float X, float Y, float Width, float Height) NextBar { get; } = (0.74f, 0.18f, 0.14f, 0.64f);

    public static IReadOnlyList<Point> PreviousTriangle { get; } =
        NextTriangle.Select(MirrorX).ToArray();

    public static (float X, float Y, float Width, float Height) PreviousBar
    {
        get
        {
            var (x, y, width, height) = NextBar;
            return (1f - x - width, y, width, height);
        }
    }

    /// <summary>The triangle tip: the x coordinate that appears once.</summary>
    public static float TipX(IReadOnlyList<Point> triangle) =>
        triangle.GroupBy(p => p.X).Single(g => g.Count() == 1).Key;
}
