namespace HotSonos.Remote.Protocol;

/// <summary>
/// Skip-previous and skip-next in a unit square (origin top-left, y downward).
/// Previous is the mirror of next across the vertical center: the triangle points
/// left and the bar sits on the left. Next points right with the bar on the right.
/// </summary>
public static class TransportGlyph
{
    public readonly record struct Point(float X, float Y);

    public readonly record struct Bar(float X, float Y, float Width, float Height);

    public readonly record struct Shape(Point[] Triangle, Bar Bar);

    /// <summary>Right-pointing skip-next: triangle, then the bar.</summary>
    public static Shape Next { get; } = new(
        [new Point(0.12f, 0.16f), new Point(0.66f, 0.50f), new Point(0.12f, 0.84f)],
        new Bar(0.74f, 0.18f, 0.14f, 0.64f));

    /// <summary>Left-pointing skip-previous: the bar, then the mirrored triangle.</summary>
    public static Shape Previous { get; } = Mirror(Next);

    public static Shape Mirror(Shape shape)
    {
        var triangle = new Point[shape.Triangle.Length];
        for (var i = 0; i < shape.Triangle.Length; i++)
        {
            var point = shape.Triangle[i];
            triangle[i] = new Point(1f - point.X, point.Y);
        }

        var bar = shape.Bar;
        return new Shape(triangle, new Bar(1f - (bar.X + bar.Width), bar.Y, bar.Width, bar.Height));
    }

    /// <summary>True when the triangle's middle point is to the right of its base.</summary>
    public static bool PointsRight(Shape shape)
    {
        var apex = shape.Triangle[1];
        var baseX = (shape.Triangle[0].X + shape.Triangle[2].X) / 2f;
        return apex.X > baseX;
    }
}
