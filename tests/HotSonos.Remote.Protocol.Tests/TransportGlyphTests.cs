using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class TransportGlyphTests
{
    [Fact]
    public void Previous_is_the_horizontal_mirror_of_next()
    {
        Assert.Equal(
            TransportGlyph.NextTriangle.Select(TransportGlyph.MirrorX),
            TransportGlyph.PreviousTriangle);

        var (x, y, width, height) = TransportGlyph.NextBar;
        Assert.Equal((1f - x - width, y, width, height), TransportGlyph.PreviousBar);
    }

    [Fact]
    public void Previous_tip_points_left_and_next_tip_points_right()
    {
        var previousTip = TransportGlyph.TipX(TransportGlyph.PreviousTriangle);
        var previousBase = TransportGlyph.PreviousTriangle.Where(p => p.X != previousTip).Select(p => p.X).Distinct().Single();
        Assert.True(previousTip < previousBase, "previous triangle must point left");

        var nextTip = TransportGlyph.TipX(TransportGlyph.NextTriangle);
        var nextBase = TransportGlyph.NextTriangle.Where(p => p.X != nextTip).Select(p => p.X).Distinct().Single();
        Assert.True(nextTip > nextBase, "next triangle must point right");

        var (previousBarX, _, previousBarWidth, _) = TransportGlyph.PreviousBar;
        Assert.True(previousBarX + previousBarWidth <= previousTip, "previous bar sits to the left of the triangle");

        var (nextBarX, _, _, _) = TransportGlyph.NextBar;
        Assert.True(nextBarX >= nextTip, "next bar sits to the right of the triangle");
    }
}
