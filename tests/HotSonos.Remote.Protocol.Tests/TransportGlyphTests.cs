using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class TransportGlyphTests
{
    [Fact]
    public void Next_points_right_and_previous_is_its_left_mirror()
    {
        Assert.True(TransportGlyph.PointsRight(TransportGlyph.Next));
        Assert.False(TransportGlyph.PointsRight(TransportGlyph.Previous));

        var next = TransportGlyph.Next;
        var previous = TransportGlyph.Previous;
        Assert.Equal(next.Triangle.Length, previous.Triangle.Length);
        for (var i = 0; i < next.Triangle.Length; i++)
        {
            Assert.Equal(1f - next.Triangle[i].X, previous.Triangle[i].X, 3);
            Assert.Equal(next.Triangle[i].Y, previous.Triangle[i].Y, 3);
        }

        Assert.Equal(1f - (next.Bar.X + next.Bar.Width), previous.Bar.X, 3);
        Assert.Equal(next.Bar.Y, previous.Bar.Y, 3);
        Assert.Equal(next.Bar.Width, previous.Bar.Width, 3);
        Assert.Equal(next.Bar.Height, previous.Bar.Height, 3);

        Assert.True(previous.Bar.X < previous.Triangle[1].X);
        Assert.True(next.Bar.X > next.Triangle[1].X);
    }
}
