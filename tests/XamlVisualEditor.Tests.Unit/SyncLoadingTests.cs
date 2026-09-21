using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class SyncLoadingTests
{
    [Fact]
    public async Task CancellationDuringParsingKeepsTheDocumentAndUndoHistory()
    {
        CancelingParser parser = new();
        using SyncEngine engine = new(parser, new XamlSerializationService());
        await engine.LoadAsync("<Button xmlns='https://github.com/avaloniaui' Width='100' />");
        using (var edit = engine.BeginEditTransaction("Resize"))
        {
            engine.CurrentDocument!.Root!.SetPropertyValue("Width", "140");
            edit.Commit();
        }
        string original = engine.GetCurrentText();
        var document = engine.CurrentDocument;
        Assert.Equal(1, engine.UndoRedo.UndoCount);
        using CancellationTokenSource cancellation = new();
        parser.AfterParse = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.LoadAsync("<Canvas xmlns='https://github.com/avaloniaui' />", cancellation.Token));

        Assert.Same(document, engine.CurrentDocument);
        Assert.Equal(original, engine.GetCurrentText());
        Assert.Equal(1, engine.UndoRedo.UndoCount);
        parser.AfterParse = null;
        engine.Undo();
        Assert.Equal("100", engine.CurrentDocument!.Root!.GetPropertyValue("Width"));
    }

    private sealed class CancelingParser : IXamlParsingService
    {
        private readonly XamlParsingService _inner = new();
        public Action? AfterParse { get; set; }

        public ParseResult Parse(string xamlText, XamlParserOptions? options = null)
        {
            ParseResult result = _inner.Parse(xamlText, options);
            AfterParse?.Invoke();
            return result;
        }
    }
}
