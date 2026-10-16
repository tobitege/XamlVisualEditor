using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class BackgroundActivityTrackerTests
{
    [Fact]
    public void CurrentDescription_Follows_The_Most_Recent_Running_Activity()
    {
        BackgroundActivityTracker tracker = new();
        Assert.Null(tracker.CurrentDescription);

        IDisposable first = tracker.Begin("first");
        IDisposable second = tracker.Begin("second");
        Assert.Equal("second", tracker.CurrentDescription);

        second.Dispose();
        Assert.Equal("first", tracker.CurrentDescription);

        first.Dispose();
        Assert.Null(tracker.CurrentDescription);
    }

    [Fact]
    public void Ending_An_Older_Activity_Keeps_The_Newer_One_Current()
    {
        BackgroundActivityTracker tracker = new();
        IDisposable older = tracker.Begin("older");
        using IDisposable newer = tracker.Begin("newer");

        older.Dispose();

        Assert.Equal("newer", tracker.CurrentDescription);
    }

    [Fact]
    public void Changed_Is_Raised_Once_Per_Begin_And_End()
    {
        BackgroundActivityTracker tracker = new();
        int changes = 0;
        tracker.Changed += (_, _) => changes++;

        IDisposable activity = tracker.Begin("work");
        activity.Dispose();
        activity.Dispose();

        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Begin_Rejects_An_Empty_Description(string description)
    {
        BackgroundActivityTracker tracker = new();

        Assert.Throws<ArgumentException>(() => tracker.Begin(description));
        Assert.Null(tracker.CurrentDescription);
    }
}
