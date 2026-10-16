using Avalonia.Headless.XUnit;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Threading;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerErrorOutputTests : IDisposable
{
    private const string Source = """
        <UserControl xmlns="https://github.com/avaloniaui">
            <!-- Original formatting remains unchanged. -->
            <StackPanel>
                <Button Content="First" />
                <!-- The second button fails. -->
                <Button Width="invalid" />
            </StackPanel>
        </UserControl>
        """;

    private const string Preview = "<UserControl xmlns='https://github.com/avaloniaui'><StackPanel>"
        + "<Button Content='First' /><Button Width='invalid' /></StackPanel></UserControl>";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-PreviewerErrorOutput-" + Guid.NewGuid().ToString("N"));

    public PreviewerErrorOutputTests()
    {
        Directory.CreateDirectory(_root);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Output_Names_The_Source_Position_Of_A_Previewer_Error()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(_root, "View.axaml");
        File.WriteAllText(path, Source);
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        await main.OpenFileAsync(path).WaitAsync(cancellationToken);
        int position = Preview.LastIndexOf("<Button", StringComparison.Ordinal) + 1;
        PreviewerErrorInfo error = PreviewerTcpSession.CreateXamlError(new UpdateXamlResultMessage
        {
            Error = "Full exception stack",
            Exception = new ExceptionDetails { Message = "Invalid Width", LineNumber = 1, LinePosition = position }
        }, Preview, path);

        main.OnPreviewerErrorReceived(error);
        Dispatcher.UIThread.RunJobs();

        OutputMessage message = Assert.Single(main.Output.Messages, candidate => candidate.Text == "Invalid Width");
        Assert.Equal((6, 10), (message.Line, message.Column));
        Assert.Equal(path, message.FilePath);
    }

    [Fact]
    public void Text_Added_As_A_Control_Is_Named_With_Its_Source_Position()
    {
        const string source = """
            <UserControl xmlns="https://github.com/avaloniaui">
                <Grid>
                    <TextBlock Text="Contact" />l
                    <TextBox />
                </Grid>
            </UserControl>
            """;
        const string preview = "<UserControl xmlns='https://github.com/avaloniaui'><Grid>"
            + "<TextBlock Text='Contact' />l<TextBox /></Grid></UserControl>";

        PreviewerErrorInfo error = PreviewerTcpSession.CreateXamlError(new UpdateXamlResultMessage
        {
            Error = "Full exception stack",
            Exception = new ExceptionDetails
            {
                ExceptionType = "InvalidCastException",
                Message = "Unable to cast object of type 'System.String' to type 'Avalonia.Controls.Control'."
            }
        }, preview, "View.axaml");

        Assert.Equal(string.Format(XamlVisualEditor.Core.Localization.UiStrings.PreviewerTextNotAllowed, "l", "Grid"), error.Message);
        string sourceLine = source.Split('\n')[2];
        Assert.Equal((3, sourceLine.IndexOf("/>l", StringComparison.Ordinal) + 3),
            DesignerDocumentViewModel.ResolvePreviewerErrorPosition(error, source));
    }

    [Theory]
    [InlineData("<TextBlock HorizontalAlignment=\"Middle\" />",
        "Unable to find suitable setter or adder for property HorizontalAlignment of type Avalonia.Base:Avalonia.Layout.Layoutable for argument System.Private.CoreLib:System.String, available setter parameter lists are:\nAvalonia.UnsetValueType\nAvalonia.Data.BindingBase\nAvalonia.Layout.HorizontalAlignment",
        "HorizontalAlignment", "Stretch, Left, Center, Right")]
    [InlineData("<TextBlock Widht=\"5\" />",
        "Unable to resolve suitable regular or attached property Widht on type Avalonia.Controls:Avalonia.Controls.TextBlock",
        "Widht", null)]
    [InlineData("<TextBlock Width=\"abc\" />", "The input string 'abc' was not in a correct format.", "Width", null)]
    [InlineData("<TextBlock Grid.Row=\"x\" />", "The input string 'x' was not in a correct format.", "Grid.Row", null)]
    public void Value_Errors_Name_The_Attribute_And_Point_To_It(string element, string loaderMessage, string attribute, string? allowed)
    {
        string source = "<StackPanel xmlns=\"https://github.com/avaloniaui\">\n  " + element + "\n</StackPanel>";

        PreviewerErrorInfo error = PreviewerTcpSession.CreateXamlError(new UpdateXamlResultMessage
        {
            Error = loaderMessage,
            Exception = new ExceptionDetails { ExceptionType = "XamlLoadException", Message = loaderMessage, LineNumber = 2, LinePosition = 4 }
        }, source, "View.axaml");

        Assert.Equal(attribute, error.AttributeName);
        Assert.NotEqual(loaderMessage, error.Message);
        Assert.Contains(attribute, error.Message, StringComparison.Ordinal);
        if (allowed is not null) Assert.Contains(allowed, error.Message, StringComparison.Ordinal);
        Assert.Equal((2, source.Split('\n')[1].IndexOf(attribute, StringComparison.Ordinal) + 1),
            DesignerDocumentViewModel.ResolvePreviewerErrorPosition(error, source));
    }

    [Fact]
    public void Text_In_A_Single_Child_Element_Is_Named_With_Its_Position()
    {
        const string source = "<StackPanel xmlns=\"https://github.com/avaloniaui\">\n  <Border>x</Border>\n</StackPanel>";

        PreviewerErrorInfo error = PreviewerTcpSession.CreateXamlError(new UpdateXamlResultMessage
        {
            Error = BorderTextMessage,
            Exception = new ExceptionDetails { ExceptionType = "XamlLoadException", Message = BorderTextMessage, LineNumber = 2, LinePosition = 4 }
        }, source, "View.axaml");

        Assert.Equal(string.Format(XamlVisualEditor.Core.Localization.UiStrings.PreviewerTextNotAllowed, "x", "Border"), error.Message);
        Assert.Equal((2, source.Split('\n')[1].IndexOf(">x", StringComparison.Ordinal) + 2),
            DesignerDocumentViewModel.ResolvePreviewerErrorPosition(error, source));
    }

    private const string BorderTextMessage = "Unable to find suitable setter or adder for property Child of type Avalonia.Controls:Avalonia.Controls.Decorator for argument System.Private.CoreLib:System.String, available setter parameter lists are:\nAvalonia.Controls.Control";

    [Theory]
    [InlineData("<Border><TextBlock /><TextBlock /></Border>",
        "Unable to find a setter that allows multiple assignments to the property Child of type Avalonia.Controls:Avalonia.Controls.Decorator",
        "<Border>", "Child")]
    [InlineData("<Buton />",
        "Unable to resolve type Buton from namespace https://github.com/avaloniaui",
        "<Buton>", "xmlns")]
    public void Structure_Errors_Are_Restated(string element, string loaderMessage, string named, string hint)
    {
        string source = "<StackPanel xmlns=\"https://github.com/avaloniaui\">\n  " + element + "\n</StackPanel>";

        PreviewerErrorInfo error = PreviewerTcpSession.CreateXamlError(new UpdateXamlResultMessage
        {
            Error = loaderMessage,
            Exception = new ExceptionDetails { ExceptionType = "XamlLoadException", Message = loaderMessage, LineNumber = 2, LinePosition = 4 }
        }, source, "View.axaml");

        Assert.NotEqual(loaderMessage, error.Message);
        Assert.Contains(named, error.Message, StringComparison.Ordinal);
        Assert.Contains(hint, error.Message, StringComparison.Ordinal);
        Assert.Equal((2, 4), DesignerDocumentViewModel.ResolvePreviewerErrorPosition(error, source));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
