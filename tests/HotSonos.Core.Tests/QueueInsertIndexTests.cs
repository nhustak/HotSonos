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
}
