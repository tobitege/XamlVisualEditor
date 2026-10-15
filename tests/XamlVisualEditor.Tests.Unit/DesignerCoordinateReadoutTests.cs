using System;
using System.Globalization;
using System.IO;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Adorners;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerCoordinateReadoutTests : IDisposable
{
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-readout-" + Guid.NewGuid().ToString("N"));

    public DesignerCoordinateReadoutTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Fact]
    public void Pointer_ReportsThePixelThatContainsThePosition()
    {
        DesignerCoordinateReadout readout = new();

        readout.SetPointer(new Point(10.6, 19.9999999));
        Assert.Equal(10, readout.PointerX);
        Assert.Equal(20, readout.PointerY);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, UiStrings.CoordinateStripPointer, "10", "20"), readout.PointerText);
        Assert.Equal("10,20", readout.PointerValue);

        readout.SetPointer(new Point(-0.25, -7));
        Assert.Equal(-1, readout.PointerX);
        Assert.Equal("-1,-7", readout.PointerValue);

        readout.SetPointer(null);
        Assert.Null(readout.PointerX);
        Assert.Null(readout.PointerText);
        Assert.Null(readout.PointerValue);
    }

    [Fact]
    public void Hover_ShowsTypeAndNameLikeASelector()
    {
        DesignerCoordinateReadout readout = new();

        readout.SetHover("Button", "Action");
        Assert.Equal("Button#Action", readout.HoverText);
        readout.SetHover("Border", null);
        Assert.Equal("Border", readout.HoverText);
        readout.SetHover(null, "Ignored");
        Assert.Null(readout.HoverText);
    }

    [Fact]
    public void Selection_ShowsBoundsAndCountAndCopiesThemAsRect()
    {
        DesignerCoordinateReadout readout = new();

        readout.SetSelection(new Rect(12, 8.5, 100, 30.25), 1);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, UiStrings.CoordinateStripSelection, "12", "8.5", "100", "30.25"),
            readout.SelectionText);
        Assert.Equal("12,8.5,100,30.25", readout.SelectionValue);

        readout.SetSelection(new Rect(0, 0, 10, 10), 3);
        string single = string.Format(CultureInfo.InvariantCulture, UiStrings.CoordinateStripSelection, "0", "0", "10", "10");
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, UiStrings.CoordinateStripSelectionCount, single, 3), readout.SelectionText);

        readout.SetSelection(null, 0);
        Assert.Null(readout.SelectionText);
        Assert.Null(readout.SelectionValue);
    }

    [Fact]
    public void Change_ShowsSignedDifferenceSinceBeginUntilEnd()
    {
        DesignerCoordinateReadout readout = new();
        readout.SetSelection(new Rect(10, 10, 50, 20), 1);

        readout.BeginChange();
        Assert.Null(readout.ChangeText);
        readout.SetSelection(new Rect(10.000000001, 10, 50, 20), 1);
        Assert.Null(readout.ChangeText);

        readout.SetSelection(new Rect(14, 7, 50, 25), 1);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, UiStrings.CoordinateStripChange, "+4", "-3", "0", "+5"), readout.ChangeText);

        readout.EndChange();
        Assert.Null(readout.ChangeText);
    }

    [Fact]
    public void Measure_ReportsLegsAndLengthBetweenPixels()
    {
        DesignerCoordinateReadout readout = new();

        readout.SetMeasure(new Point(10.4, 20.9), new Point(40.2, 60.7));
        Assert.Equal(new Point(10, 20), readout.MeasureStart);
        Assert.Equal(new Point(40, 60), readout.MeasureEnd);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, UiStrings.CoordinateStripMeasure, "+30", "+40", "50"), readout.MeasureText);
        Assert.Equal("30,40", readout.MeasureValue);

        readout.ClearMeasure();
        Assert.Null(readout.MeasureStart);
        Assert.Null(readout.MeasureText);
    }

    [Fact]
    public void Surface_ClearsTheMeasurementWhenMeasuringEnds()
    {
        DesignSurfaceViewModel surface = new() { IsMeasuring = true };
        surface.Readout.SetMeasure(new Point(0, 0), new Point(5, 5));

        surface.IsMeasuring = false;

        Assert.Null(surface.Readout.MeasureText);
    }

    [Fact]
    public async Task CopyReadoutCommand_WritesTheValueToTheClipboard()
    {
        RecordingClipboard clipboard = new();
        DesignSurfaceViewModel surface = new() { ClipboardService = clipboard };

        await surface.CopyReadoutCommand.Execute("12,34").ToTask();
        await surface.CopyReadoutCommand.Execute(null).ToTask();

        Assert.Equal("12,34", clipboard.Text);
        Assert.Equal(1, clipboard.Writes);
    }

    [Theory]
    [InlineData(0.8734, 0.87)]
    [InlineData(1.006, 1.01)]
    [InlineData(0.1, 0.25)]
    [InlineData(5, 3)]
    public void Zoom_KeepsWholePercentages(double requested, double expected)
    {
        DesignSurfaceViewModel surface = new() { Zoom = requested };

        Assert.Equal(expected, surface.Zoom, 10);
    }

    [Theory]
    [InlineData(0.8799, 0.87)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.1, 0.25)]
    public void FitZoom_RoundsDownToWholePercentages(double fitted, double expected)
    {
        Assert.Equal(expected, DesignSurfaceViewModel.FitZoom(fitted), 10);
    }

    [Theory]
    [InlineData(8, 1, 8)]
    [InlineData(8, 0.25, 16)]
    [InlineData(1, 1, 4)]
    [InlineData(1, 3, 2)]
    [InlineData(0.5, 1, 0)]
    public void GridLineStep_ThinsOutLinesThatWouldBeDenserThanFourScreenPixels(double gridSize, double zoom, double expected)
    {
        Assert.Equal(expected, DesignGridLayer.GetLineStep(gridSize, zoom));
    }

    [Fact]
    public void ViewportOptions_PersistAndApplyToASurface()
    {
        string file = Path.Combine(_directory, "options.json");
        DesignerViewportOptions stored = new() { ShowGrid = true, SnapToGrid = false, GridSize = 12, ShowRulers = false, ShowCoordinateStrip = false };
        new EditorOptionsStore(file).Save(new EditorOptions { DesignerViewport = stored });

        DesignerViewportOptions loaded = new EditorOptionsStore(file).Current.DesignerViewport;
        DesignSurfaceViewModel surface = new();
        loaded.ApplyTo(surface);

        Assert.Equal(stored, loaded);
        Assert.Equal(stored, DesignerViewportOptions.From(surface));
        Assert.Equal(12, surface.GridSnapSize);
    }

    [Theory]
    [InlineData(250, 99)]
    [InlineData(0, 4)]
    [InlineData(double.NaN, 8)]
    [InlineData(7.6, 8)]
    public void ViewportOptions_LimitTheGridSizeToTheOfferedRange(double gridSize, double expected)
    {
        Assert.Equal(expected, DesignerViewportOptions.Normalize(new DesignerViewportOptions { GridSize = gridSize }).GridSize);
        Assert.Equal(new DesignerViewportOptions(), DesignerViewportOptions.Normalize(null));
    }

    [Fact]
    public void ViewportOptions_FromAFileWithoutTheEntryUseTheDefaults()
    {
        string file = Path.Combine(_directory, "legacy.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(file, """{ "Language": "en", "DesignerViewport": null }""");

        EditorOptionsStore store = new(file);

        Assert.Null(store.LoadError);
        Assert.Equal(new DesignerViewportOptions(), store.Current.DesignerViewport);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public int Writes { get; private set; }

        public Task<string?> GetTextAsync() => Task.FromResult(Text);

        public Task SetTextAsync(string text)
        {
            Text = text;
            Writes++;
            return Task.CompletedTask;
        }
    }
}
