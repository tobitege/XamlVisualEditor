using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class StatusBarIntegrationTests
{
    [Fact]
    public void UpsertStatusBarItem_AddsAndMovesBetweenAlignments()
    {
        using MainWindowViewModel viewModel = new();

        viewModel.UpsertStatusBarItem(
            "status.collab",
            "Collab: Offline",
            "No active collaboration session",
            "collaboration.toggleView",
            StatusBarAlignment.Left,
            95,
            isVisible: true);

        Assert.Single(viewModel.LeftStatusBarItems);
        Assert.Empty(viewModel.RightStatusBarItems);
        Assert.Equal("Collab: Offline", viewModel.LeftStatusBarItems[0].Text);
        Assert.NotNull(viewModel.LeftStatusBarItems[0].Command);

        viewModel.UpsertStatusBarItem(
            "status.collab",
            "Collab: 2",
            "Session with two participants",
            null,
            StatusBarAlignment.Right,
            10,
            isVisible: true);

        Assert.Empty(viewModel.LeftStatusBarItems);
        Assert.Single(viewModel.RightStatusBarItems);
        Assert.Equal("Collab: 2", viewModel.RightStatusBarItems[0].Text);
        Assert.Null(viewModel.RightStatusBarItems[0].Command);
    }

    [Fact]
    public void UpsertStatusBarItem_HideAndRemove_Work()
    {
        using MainWindowViewModel viewModel = new();

        viewModel.UpsertStatusBarItem(
            "status.sample",
            "Sample",
            null,
            null,
            StatusBarAlignment.Left,
            1,
            isVisible: true);

        Assert.Single(viewModel.LeftStatusBarItems);

        viewModel.UpsertStatusBarItem(
            "status.sample",
            "Sample",
            null,
            null,
            StatusBarAlignment.Left,
            1,
            isVisible: false);

        Assert.Empty(viewModel.LeftStatusBarItems);

        viewModel.RemoveStatusBarItem("status.sample");
        Assert.Empty(viewModel.LeftStatusBarItems);
        Assert.Empty(viewModel.RightStatusBarItems);
    }

    [Fact]
    public void StatusText_Changes_Notify_The_StatusText_Property()
    {
        BackgroundActivityTracker activities = new();
        IDisposable startup = activities.Begin(UiStrings.StartingStatus);
        using MainWindowViewModel viewModel = new(backgroundActivities: activities);
        List<string?> changed = new();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        startup.Dispose();
        viewModel.StatusText = "Saved";

        Assert.Equal(2, changed.Count(name => name == nameof(MainWindowViewModel.StatusText)));
    }

    [Fact]
    public void StatusText_Is_Ready_When_No_Activity_Runs_At_Creation()
    {
        BackgroundActivityTracker activities = new();
        using MainWindowViewModel viewModel = new(backgroundActivities: activities);

        Assert.False(viewModel.IsStarting);
        Assert.Equal(UiStrings.Ready, viewModel.StatusText);
    }

    [Fact]
    public void StatusText_Shows_Running_Activities_Until_Startup_Ends()
    {
        BackgroundActivityTracker activities = new();
        IDisposable startup = activities.Begin(UiStrings.StartingStatus);
        using MainWindowViewModel viewModel = new(backgroundActivities: activities);

        Assert.True(viewModel.IsStarting);
        Assert.Equal(UiStrings.StartingStatus, viewModel.StatusText);

        IDisposable extension = activities.Begin("Activating sample...");
        Assert.Equal("Activating sample...", viewModel.StatusText);

        extension.Dispose();
        Assert.Equal(UiStrings.StartingStatus, viewModel.StatusText);
        Assert.True(viewModel.IsStarting);

        startup.Dispose();
        Assert.False(viewModel.IsStarting);
        Assert.Equal(UiStrings.Ready, viewModel.StatusText);
    }

    [Fact]
    public void Message_Set_During_Startup_Is_Not_Replaced_When_Startup_Ends()
    {
        BackgroundActivityTracker activities = new();
        IDisposable startup = activities.Begin(UiStrings.StartingStatus);
        using MainWindowViewModel viewModel = new(backgroundActivities: activities);

        viewModel.StatusText = "Workspace loaded";
        startup.Dispose();

        Assert.False(viewModel.IsStarting);
        Assert.Equal("Workspace loaded", viewModel.StatusText);
    }

    [Fact]
    public void Activity_Changes_Do_Not_Replace_Another_Message()
    {
        BackgroundActivityTracker activities = new();
        IDisposable startup = activities.Begin(UiStrings.StartingStatus);
        using MainWindowViewModel viewModel = new(backgroundActivities: activities);

        viewModel.StatusText = "Loading workspace Sample.csproj";
        IDisposable extension = activities.Begin("Activating sample...");
        extension.Dispose();

        Assert.Equal("Loading workspace Sample.csproj", viewModel.StatusText);
        startup.Dispose();
        Assert.Equal("Loading workspace Sample.csproj", viewModel.StatusText);
    }

    [Fact]
    public void Activity_After_Startup_Returns_To_Ready_When_It_Ends()
    {
        BackgroundActivityTracker activities = new();
        using MainWindowViewModel viewModel = new(backgroundActivities: activities);

        IDisposable warmup = activities.Begin("Preparing language services...");
        Assert.Equal("Preparing language services...", viewModel.StatusText);
        Assert.False(viewModel.IsStarting);

        warmup.Dispose();
        Assert.Equal(UiStrings.Ready, viewModel.StatusText);
    }
}
