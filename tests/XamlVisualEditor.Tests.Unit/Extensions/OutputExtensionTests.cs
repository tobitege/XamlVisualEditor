using System.Reactive.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls.DataGridHierarchical;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.OutputExtension;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

#pragma warning disable CS0067

public sealed class OutputExtensionTests
{
    [Fact]
    public async Task NavigateToRelativeAsync_ReturnsFalse_WhenNoDiagnostics()
    {
        StubEditorServices editor = new();
        using ProblemsPanelViewModel viewModel = new(new StubDiagnosticsService(), editor);

        bool navigated = await viewModel.NavigateToRelativeAsync(1, CancellationToken.None);

        Assert.False(navigated);
        Assert.Empty(editor.OpenedLocations);
    }

    [Fact]
    public async Task NavigateToRelativeAsync_CyclesDiagnosticsAndOpensLocations()
    {
        StubEditorServices editor = new();
        using ProblemsPanelViewModel viewModel = new(new StubDiagnosticsService(), editor);
        viewModel.HandleSnapshotsPublished(
        [
            new DiagnosticsDocumentSnapshot(
                "/repo",
                [
                    CreateDiagnostic("/repo/FileA.axaml", 2, 5, "A"),
                    CreateDiagnostic("/repo/FileB.axaml", 9, 3, "B"),
                    CreateDiagnostic(string.Empty, 0, 0, "Workspace error")
                ])
        ]);

        HierarchicalNode firstRow = viewModel.Model.Flattened.OfType<HierarchicalNode>()
            .Single(row => row.Item is DiagnosticEntryViewModel { Message: "A" });
        viewModel.SelectedRow = firstRow;
        Assert.Same(firstRow.Item, viewModel.SelectedDiagnostic);
        Assert.True(((ICommand)viewModel.OpenLocationCommand).CanExecute(null));
        await viewModel.OpenLocationCommand.Execute().ToTask();
        Assert.Equal(2, editor.OpenedLocations[0].Range.Start.Line);
        Assert.Equal(5, editor.OpenedLocations[0].Range.Start.Column);
        Assert.Empty(editor.PlainLocations);
        viewModel.SelectedRow = null;

        bool first = await viewModel.NavigateToRelativeAsync(1, CancellationToken.None);
        bool second = await viewModel.NavigateToRelativeAsync(1, CancellationToken.None);
        bool previous = await viewModel.NavigateToRelativeAsync(-1, CancellationToken.None);
        bool wrapped = await viewModel.NavigateToRelativeAsync(-1, CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.True(previous);
        Assert.True(wrapped);
        Assert.Equal(5, editor.OpenedLocations.Count);
        Assert.Equal("/repo/FileA.axaml", editor.OpenedLocations[0].FilePath);
        Assert.Equal("/repo/FileA.axaml", editor.OpenedLocations[1].FilePath);
        Assert.Equal("/repo/FileB.axaml", editor.OpenedLocations[2].FilePath);
        Assert.Equal("/repo/FileA.axaml", editor.OpenedLocations[3].FilePath);
        Assert.Equal("/repo/FileB.axaml", editor.OpenedLocations[4].FilePath);
        Assert.Same(viewModel.SelectedDiagnostic, Assert.IsType<HierarchicalNode>(viewModel.SelectedRow).Item);

        HierarchicalNode workspaceRow = viewModel.Model.Flattened.OfType<HierarchicalNode>()
            .Single(row => row.Item is DiagnosticEntryViewModel { FilePath: "" });
        viewModel.SelectedRow = workspaceRow;
        Assert.Equal("Workspace", viewModel.SelectedDiagnostic!.Location);
        Assert.False(((ICommand)viewModel.OpenLocationCommand).CanExecute(null));
        Assert.True(((ICommand)viewModel.CopySelectedCommand).CanExecute(null));

        HierarchicalNode fileGroupRow = viewModel.Model.Flattened.OfType<HierarchicalNode>()
            .Single(row => row.Item is ProblemsGroupViewModel { Title: "FileA.axaml" });
        viewModel.SelectedRow = fileGroupRow;
        Assert.IsType<ProblemsGroupViewModel>(fileGroupRow.Item).IsExpanded = false;
        viewModel.HandleSnapshotsPublished(
        [
            new DiagnosticsDocumentSnapshot("/repo",
            [
                CreateDiagnostic("/repo/FileA.axaml", 2, 5, "A"),
                CreateDiagnostic("/repo/FileB.axaml", 9, 3, "B")
            ])
        ]);
        ProblemsGroupViewModel collapsedGroup = viewModel.Model.Flattened.OfType<HierarchicalNode>()
            .Select(row => row.Item).OfType<ProblemsGroupViewModel>().Single(group => group.Title == "FileA.axaml");
        Assert.False(collapsedGroup.IsExpanded);
        Assert.True(await viewModel.NavigateToRelativeAsync(1, CancellationToken.None));
        Assert.Equal("A", viewModel.SelectedDiagnostic!.Message);
        Assert.Contains(viewModel.SelectedRow, viewModel.Model.Flattened);

        viewModel.SelectedSeverity = "Warning";
        Assert.Empty(viewModel.Model.Flattened);
        Assert.False(await viewModel.NavigateToRelativeAsync(1, CancellationToken.None));
        viewModel.SelectedSeverity = "All";
        editor.OpenLocationResult = false;
        Assert.False(await viewModel.NavigateToRelativeAsync(1, CancellationToken.None));
        viewModel.HandleSnapshotsPublished(Array.Empty<DiagnosticsDocumentSnapshot>());
        Assert.Empty(viewModel.Model.Flattened);
        Assert.Null(viewModel.SelectedDiagnostic);
        Assert.False(((ICommand)viewModel.OpenLocationCommand).CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_DoesNotRestoreSupersededOrClearedDiagnostics(bool honorsCancellation)
    {
        StubEditorServices editor = new();
        StubDiagnosticsService diagnostics = new();
        using ProblemsPanelViewModel viewModel = new(diagnostics, editor);
        viewModel.SelectedChannel = new DiagnosticsChannelViewModel("all", "All");
        TaskCompletionSource<IReadOnlyList<DiagnosticsDocumentSnapshot>> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        diagnostics.SnapshotLoader = (_, token) => honorsCancellation ? pending.Task.WaitAsync(token) : pending.Task;
        Task refresh = viewModel.RefreshCommand.Execute().ToTask();
        DiagnosticsDocumentSnapshot[] current =
        [
            new("/repo/Current.axaml", [CreateDiagnostic("/repo/Current.axaml", 7, 4, "Current")])
        ];
        viewModel.HandleSnapshotsPublished(current);
        pending.SetResult([new("/repo/Stale.axaml", [CreateDiagnostic("/repo/Stale.axaml", 1, 1, "Stale")])]);
        await refresh;
        Assert.True(await viewModel.NavigateToRelativeAsync(1, CancellationToken.None));
        Assert.Equal("/repo/Current.axaml", Assert.Single(editor.OpenedLocations).FilePath);

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        refresh = viewModel.RefreshCommand.Execute().ToTask();
        await viewModel.ClearCommand.Execute().ToTask();
        pending.SetResult(current);
        await refresh;
        Assert.Empty(viewModel.Model.Flattened);
        Assert.False(await viewModel.NavigateToRelativeAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Diagnostics_KeepFirstSeenAcrossRefreshAndFiltersUntilResolved()
    {
        ManualTimeProvider clock = new();
        DateTimeOffset initialTime = clock.UtcNow;
        StubDiagnosticsService diagnostics = new();
        LanguageDiagnostic[] current = [CreateDiagnostic("/repo/View.axaml", 2, 4, "Shared", "compiler")];
        diagnostics.SnapshotLoader = (query, _) => Task.FromResult<IReadOnlyList<DiagnosticsDocumentSnapshot>>(
            [new("/repo/View.axaml", current.Where(entry => query.ChannelId is null || entry.Source == query.ChannelId).ToArray())]);
        using ProblemsPanelViewModel viewModel = new(diagnostics, new StubEditorServices(), clock);
        viewModel.SelectedChannel = new DiagnosticsChannelViewModel("all", "All");
        Assert.True(await viewModel.NavigateToRelativeAsync(1, CancellationToken.None));
        Assert.Equal(initialTime, viewModel.SelectedDiagnostic!.FirstSeen);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        DateTimeOffset designerTime = clock.UtcNow;
        current =
        [
            CreateDiagnostic("/repo/View.axaml", 2, 4, "Shared", "compiler"),
            CreateDiagnostic("/repo/View.axaml", 2, 4, "Shared", "designer")
        ];
        viewModel.HandleSnapshotsPublished([new("/repo/View.axaml", current)]);
        Assert.Equal("compiler", viewModel.SelectedDiagnostic!.Source);
        Assert.Equal(initialTime, viewModel.SelectedDiagnostic.FirstSeen);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        viewModel.SelectedSeverity = "Warning";
        viewModel.SelectedSeverity = "All";
        viewModel.SelectedChannel = new DiagnosticsChannelViewModel("compiler", "Compiler");
        viewModel.SelectedChannel = new DiagnosticsChannelViewModel("all", "All");
        DiagnosticEntryViewModel Entry(string source) => viewModel.Model.Flattened.OfType<HierarchicalNode>()
            .Select(row => row.Item).OfType<DiagnosticEntryViewModel>().Single(entry => entry.Source == source);
        Assert.Equal(initialTime, Entry("compiler").FirstSeen);
        Assert.Equal(designerTime, Entry("designer").FirstSeen);

        current = [CreateDiagnostic("/repo/View.axaml", 2, 4, "Shared", "designer")];
        viewModel.HandleSnapshotsPublished([new("/repo/View.axaml", current)]);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        current =
        [
            CreateDiagnostic("/repo/View.axaml", 2, 4, "Shared", "compiler"),
            CreateDiagnostic("/repo/View.axaml", 2, 4, "Shared", "designer")
        ];
        viewModel.HandleSnapshotsPublished([new("/repo/View.axaml", current)]);
        Assert.Equal(clock.UtcNow, Entry("compiler").FirstSeen);
        Assert.Equal(designerTime, Entry("designer").FirstSeen);

        DateTimeOffset recurrenceTime = clock.UtcNow;
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await viewModel.ClearCommand.Execute().ToTask();
        await viewModel.RefreshCommand.Execute().ToTask();
        Assert.Equal(recurrenceTime, Entry("compiler").FirstSeen);
        Assert.Equal(designerTime, Entry("designer").FirstSeen);
    }

    private static LanguageDiagnostic CreateDiagnostic(string filePath, int line, int column, string message, string? source = null)
    {
        return new LanguageDiagnostic
        {
            Severity = DiagnosticSeverity.Error,
            Message = message,
            Source = source,
            FilePath = filePath,
            Range = new LanguageTextRange(
                new LanguageTextPosition(line, column),
                new LanguageTextPosition(line, column))
        };
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2030, 1, 2, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class StubEditorServices : IEditorServices
    {
        public List<LanguageLocation> OpenedLocations { get; } = new();
        public bool OpenLocationResult { get; set; } = true;

        public IEditorDocument? ActiveDocument => null;

        public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

        public IReadOnlyList<IEditorDocument> GetOpenDocuments() => Array.Empty<IEditorDocument>();

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct)
            => Task.FromResult<IEditorDocument?>(null);

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct)
            => Task.FromResult<IEditorDocument?>(null);

        // The problems list opens diagnostics only through OpenDiagnosticLocationAsync.
        public List<LanguageLocation> PlainLocations { get; } = new();

        public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct)
        {
            PlainLocations.Add(location);
            return Task.FromResult(OpenLocationResult);
        }

        public Task<bool> OpenDiagnosticLocationAsync(LanguageLocation location, CancellationToken ct)
        {
            OpenedLocations.Add(location);
            return Task.FromResult(OpenLocationResult);
        }
    }

    private sealed class StubDiagnosticsService : IDiagnosticsService
    {
        public Func<DiagnosticsQuery, CancellationToken, Task<IReadOnlyList<DiagnosticsDocumentSnapshot>>>? SnapshotLoader { get; set; }
        public event EventHandler<DiagnosticsChannelsChangedEventArgs>? ChannelsChanged;
        public event EventHandler<DiagnosticsChannelPublishedEventArgs>? DiagnosticsChannelPublished;
        public event EventHandler<DiagnosticsSnapshotPublishedEventArgs>? DiagnosticsSnapshotPublished;
        public event EventHandler<DiagnosticsPublishedEventArgs>? DiagnosticsPublished;
        public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(string? filePath, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DiagnosticsQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());

        public Task<IReadOnlyList<DiagnosticsDocumentSnapshot>> GetDiagnosticsSnapshotAsync(
            DiagnosticsQuery query,
            CancellationToken ct)
            => SnapshotLoader?.Invoke(query, ct)
                ?? Task.FromResult<IReadOnlyList<DiagnosticsDocumentSnapshot>>(Array.Empty<DiagnosticsDocumentSnapshot>());

        public Task<IReadOnlyList<DiagnosticsChannelInfo>> GetChannelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiagnosticsChannelInfo>>(Array.Empty<DiagnosticsChannelInfo>());
    }
}

#pragma warning restore CS0067
