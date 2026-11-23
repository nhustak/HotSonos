using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Protocol.Tests;

public class VolumeSliderCommitTests
{
    [Fact]
    public void FromSlider_rounds_and_keeps_zero()
    {
        Assert.Equal(0, PhoneVolume.FromSlider(0));
        Assert.Equal(0, PhoneVolume.FromSlider(0.4));
        Assert.Equal(1, PhoneVolume.FromSlider(0.5));
        Assert.Equal(15, PhoneVolume.FromSlider(14.6));
        Assert.Equal(100, PhoneVolume.FromSlider(100.4));
        Assert.Equal(0, PhoneVolume.FromSlider(-3));
    }

    [Fact]
    public void Drag_to_zero_flushes_even_when_completed_is_skipped()
    {
        var seek = new VolumeSliderCommit(15);
        seek.BeginDrag();
        Assert.Equal(VolumeSliderCommit.DragFlushMs, seek.PointerValue(12));
        Assert.Equal(VolumeSliderCommit.DragFlushMs, seek.PointerValue(0));
        Assert.Equal(0, seek.Target);
        Assert.Equal(0, seek.Shown);
        Assert.Equal(15, seek.Applied);
        Assert.True(seek.NeedsFlush);
        Assert.True(seek.Dragging);

        // Lift never arrived. Aborting (page hide, or the trailing timer) still keeps 0.
        Assert.Equal(0, seek.AbortDrag());
        Assert.False(seek.Dragging);
        Assert.Equal(0, seek.Target);
        Assert.True(seek.NeedsFlush);

        seek.AppliedOk(0);
        Assert.False(seek.NeedsFlush);
    }

    [Fact]
    public void EndDrag_uses_round_and_flushes_now()
    {
        var seek = new VolumeSliderCommit(15);
        seek.BeginDrag();
        Assert.Equal(VolumeSliderCommit.DragFlushMs, seek.PointerValue(0.6));
        Assert.Equal(1, seek.Shown);
        Assert.Equal(0, seek.EndDrag(0.6));
        Assert.Equal(1, seek.Target);
        Assert.False(seek.Dragging);
    }

    [Fact]
    public void Truncating_the_thumb_would_have_sent_the_wrong_percent()
    {
        // The old DragCompleted path used (int)slider.Value, which turns 0.6 into 0.
        Assert.Equal(0, (int)0.6);
        Assert.Equal(1, PhoneVolume.FromSlider(0.6));
        Assert.Equal(0, PhoneVolume.FromSlider(0.4));
        Assert.Equal(0, PhoneVolume.FromSlider(0.0));
    }

    [Fact]
    public void Tap_without_a_drag_commits_immediately()
    {
        var seek = new VolumeSliderCommit(15);
        Assert.False(seek.Dragging);
        Assert.Equal(0, seek.PointerValue(0));
        Assert.Equal(0, seek.Target);
        Assert.True(seek.NeedsFlush);
    }

    [Fact]
    public void Failed_write_reverts_to_the_applied_percent()
    {
        var seek = new VolumeSliderCommit(15);
        seek.Aim(0);
        Assert.True(seek.NeedsFlush);
        seek.Revert();
        Assert.Equal(15, seek.Target);
        Assert.Equal(15, seek.Shown);
        Assert.False(seek.NeedsFlush);
    }

    [Fact]
    public void Player_page_flushes_from_value_changes_not_only_drag_completed()
    {
        var source = Source("src", "HotSonos.Remote", "Views", "PlayerPage.cs");
        Assert.Contains("PointerValue(e.NewValue)", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleFlush(delay)", source, StringComparison.Ordinal);
        Assert.Contains("AbortDrag()", source, StringComparison.Ordinal);
        Assert.Contains("_seek.NeedsFlush", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(int)_slider.Value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!_drag)", source, StringComparison.Ordinal);
    }

    static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new InvalidOperationException("Could not find " + Path.Combine(parts));
    }
}
