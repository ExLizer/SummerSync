using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

public class LinkBacklogTests
{
    private const double Frame = 1.0 / 60.0;

    [Fact]
    public void Sends_that_fit_everything_say_nothing()
    {
        var backlog = new LinkBacklog();
        for (double time = 0; time < 30; time += Frame)
        {
            Assert.False(backlog.Note(time, 0, 3, out string said));
            Assert.Null(said);
        }
    }

    [Fact]
    public void A_window_with_sends_that_left_unreliable_messages_over_says_how_many_and_the_most()
    {
        var backlog = new LinkBacklog();
        var said = new List<string>();
        int frame = 0;
        for (double time = 0; time < 21; time += Frame, frame++)
        {
            // A burst a second into the second window: three crowded sends.
            int left = frame >= 660 && frame < 663 ? 5 + (frame - 660) : 0;
            if (backlog.Note(time, left, frame == 661 ? 140 : 2, out string line))
            {
                said.Add(line);
            }
        }

        Assert.Single(said);
        Assert.Matches(@"3 of 60\d send\(s\) left", said[0]);
        Assert.Contains("the most waiting: 7", said[0]);
        Assert.Contains("the reliable queue reached 140", said[0]);
    }
}
