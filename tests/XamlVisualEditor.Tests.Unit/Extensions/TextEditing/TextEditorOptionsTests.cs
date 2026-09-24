using System.Reactive.Concurrency;
using System.Reactive.Linq;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TextEditingExtension.Services;
using XamlVisualEditor.TextEditingExtension.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions.TextEditing;

public sealed class TextEditorOptionsTests
{
    [Fact]
    public void NormalizeClampsValuesAndDropsBlankColors()
    {
        TextEditorViewOptions options = new TextEditorViewOptions
        {
            FontFamily = "  ",
            FontSize = 200,
            ColumnRulerPosition = 5,
            IndentationSize = 0,
            WordWrapIndentation = double.NaN,
            BackgroundColor = " ",
            LineNumbersColor = " #808080 "
        }.Normalize();

        Assert.Equal(new TextEditorViewOptions().FontFamily, options.FontFamily);
        Assert.Equal(TextEditorViewOptions.MAX_FONT_SIZE, options.FontSize);
        Assert.Equal(20, options.ColumnRulerPosition);
        Assert.Equal(1, options.IndentationSize);
        Assert.Equal(0d, options.WordWrapIndentation);
        Assert.Null(options.BackgroundColor);
        Assert.Equal("#808080", options.LineNumbersColor);
    }

    [Fact]
    public async Task StoreRoundTripsTheOptionsThroughFileStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-text-editing-" + Guid.NewGuid().ToString("N"));
        try
        {
            TextEditorOptionsStore store = new(new FileExtensionStorage("text-editing-tests", root));
            TextEditorViewOptions saved = new() { FontSize = 18, ShowSpaces = true, BackgroundColor = "#FFFFE0" };

            Assert.Null(await store.LoadAsync(CancellationToken.None));
            await store.SaveAsync(saved, CancellationToken.None);

            Assert.Equal(saved, await store.LoadAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DialogPreviewsChangesAndBuildsTheEditedOptions()
    {
        List<TextEditorViewOptions> previews = new();
        TextEditorViewOptions current = new() { FontSize = 12, BackgroundColor = "#123456" };
        using TextEditorOptionsViewModel viewModel = new(current, ["Consolas"], previews.Add, ImmediateScheduler.Instance, TimeSpan.Zero);

        viewModel.ShowTabs = true;
        viewModel.BackgroundColor = viewModel.BackgroundColors[0];

        Assert.Contains("Consolas", viewModel.FontFamilies);
        Assert.Contains(viewModel.BackgroundColors, choice => choice.Value == "#123456");
        Assert.Equal(2, previews.Count);
        Assert.True(previews[^1].ShowTabs);
        Assert.Null(previews[^1].BackgroundColor);
        Assert.Equal(12, viewModel.BuildOptions().FontSize);
    }

    [Fact]
    public async Task CancelRestoresTheOriginalOptionsAndSaveKeepsTheResult()
    {
        List<TextEditorViewOptions> previews = new();
        TextEditorViewOptions current = new() { WordWrap = true };
        using TextEditorOptionsViewModel viewModel = new(current, Array.Empty<string>(), previews.Add, ImmediateScheduler.Instance, TimeSpan.Zero);
        List<bool> closes = new();
        using IDisposable handler = viewModel.CloseInteraction.RegisterHandler(context =>
        {
            closes.Add(context.Input);
            context.SetOutput(System.Reactive.Unit.Default);
        });

        viewModel.WordWrap = false;
        await viewModel.CancelCommand.Execute();
        viewModel.ShowSpaces = true;
        await viewModel.SaveCommand.Execute();

        Assert.Equal(current, previews[1]);
        Assert.Equal([false, true], closes);
        Assert.NotNull(viewModel.Result);
        Assert.True(viewModel.Result.ShowSpaces);
    }

    [Fact]
    public async Task ResetLoadsAndPreviewsTheDefaults()
    {
        List<TextEditorViewOptions> previews = new();
        using TextEditorOptionsViewModel viewModel = new(new TextEditorViewOptions { FontSize = 30, ShowColumnRuler = true },
            Array.Empty<string>(), previews.Add, ImmediateScheduler.Instance, TimeSpan.Zero);

        await viewModel.ResetCommand.Execute();

        Assert.Equal(new TextEditorViewOptions().Normalize(), viewModel.BuildOptions());
        Assert.Equal(new TextEditorViewOptions().Normalize(), previews[^1]);
    }

    [Fact]
    public async Task TypingPreviewsOnceAfterAPauseAndCancelDropsThePendingPreview()
    {
        HistoricalScheduler scheduler = new();
        List<TextEditorViewOptions> previews = new();
        TextEditorViewOptions current = new();
        using TextEditorOptionsViewModel viewModel = new(current, Array.Empty<string>(), previews.Add, scheduler,
            TimeSpan.FromMilliseconds(150));
        using IDisposable handler = viewModel.CloseInteraction.RegisterHandler(context => context.SetOutput(System.Reactive.Unit.Default));

        viewModel.FontFamily = "C";
        viewModel.FontFamily = "Co";
        viewModel.FontFamily = "Consolas";
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100));
        Assert.Empty(previews);
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100));
        Assert.Equal("Consolas", Assert.Single(previews).FontFamily);

        viewModel.FontSize = 20;
        await viewModel.CancelCommand.Execute();
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1));

        Assert.Equal(2, previews.Count);
        Assert.Equal(current, previews[^1]);
    }
}
