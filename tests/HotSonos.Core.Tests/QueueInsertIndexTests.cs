using HotSonos.Core;

namespace HotSonos.Core.Tests;

public class QueueInsertIndexTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 2)]
    [InlineData(35, 36)]
    [InlineData(66, 67)]
    public void QueueInsertIndexAfterCurrent_never_returns_zero(int current, int expected)
    {
        Assert.Equal(expected, SonosController.QueueInsertIndexAfterCurrent(current));
    }

    [Theory]
    [InlineData(SonosController.LibraryQueuePlace.AfterCurrent, 0, 1)]
    [InlineData(SonosController.LibraryQueuePlace.AfterCurrent, 1, 2)]
    [InlineData(SonosController.LibraryQueuePlace.AfterCurrent, 35, 36)]
    [InlineData(SonosController.LibraryQueuePlace.Append, 0, 0)]
    [InlineData(SonosController.LibraryQueuePlace.Append, 1, 0)]
    [InlineData(SonosController.LibraryQueuePlace.Append, 66, 0)]
    public void QueueInsertIndex_play_now_and_play_next_go_after_current_append_goes_to_end(
        SonosController.LibraryQueuePlace place, int current, int expected)
    {
        Assert.Equal(expected, SonosController.QueueInsertIndex(place, current));
    }

    [Fact]
    public void PlayNow_and_PlayNext_share_the_same_insert_index()
    {
        Assert.Equal(
            SonosController.QueueInsertIndex(SonosController.LibraryQueuePlace.AfterCurrent, 8),
            SonosController.QueueInsertIndexAfterCurrent(8));
    }
}
