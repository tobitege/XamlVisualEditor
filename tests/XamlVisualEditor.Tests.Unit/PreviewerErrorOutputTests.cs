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

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
