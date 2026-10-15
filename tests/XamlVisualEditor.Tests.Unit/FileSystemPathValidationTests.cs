using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class FileSystemPathValidationTests
{
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Projects\App\MainWindow.axaml")]
    [InlineData(@"d:\Projects\App\")]
    [InlineData(@"C:\Projects\My App\Views\Main View.axaml")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\server\share\folder\file.cs")]
    public void Windows_Accepts_Drive_And_Unc_Paths(string path)
    {
        Assert.True(FileSystemPathValidation.IsAcceptedWindowsPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:/Projects/App/MainWindow.axaml")]
    [InlineData(@"C:\Projects/App\MainWindow.axaml")]
    [InlineData("//server/share/file.cs")]
    [InlineData(@"\Projects\App\MainWindow.axaml")]
    [InlineData(@"C:Projects\App\MainWindow.axaml")]
    [InlineData(@"Projects\App\MainWindow.axaml")]
    [InlineData("MainWindow.axaml")]
    [InlineData(@"C:\Projects\\App\MainWindow.axaml")]
    [InlineData(@"C:\Projects\..\Windows\win.ini")]
    [InlineData(@"C:\Projects\.\MainWindow.axaml")]
    [InlineData(@"C:\Projects\App\MainWindow.axaml.")]
    [InlineData(@"C:\Projects\App \MainWindow.axaml")]
    [InlineData(@"C:\Projects\App\Main<Window>.axaml")]
    [InlineData(@"C:\Projects\App\MainWindow.axaml:stream")]
    [InlineData("C:\\Projects\\App\\Main\u0000Window.axaml")]
    [InlineData("C:\\Projects\\App\nMainWindow.axaml")]
    [InlineData(@"1:\Projects\MainWindow.axaml")]
    [InlineData(@"\\")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\")]
    [InlineData(@"\\\share\file.cs")]
    [InlineData(@"\\server\\file.cs")]
    [InlineData(@"\\?\C:\Projects\MainWindow.axaml")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\server\share\..\other\file.cs")]
    public void Windows_Rejects_Other_Paths(string? path)
    {
        Assert.False(FileSystemPathValidation.IsAcceptedWindowsPath(path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/home/user/App/MainWindow.axaml")]
    [InlineData("/home/user/App/")]
    public void Unix_Accepts_Absolute_Paths(string path)
    {
        Assert.True(FileSystemPathValidation.IsAcceptedUnixPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("home/user/MainWindow.axaml")]
    [InlineData("/home//user/MainWindow.axaml")]
    [InlineData("/home/user/../root/.ssh")]
    [InlineData("/home/./user/MainWindow.axaml")]
    [InlineData("/home/user/Main\u0000Window.axaml")]
    public void Unix_Rejects_Other_Paths(string? path)
    {
        Assert.False(FileSystemPathValidation.IsAcceptedUnixPath(path));
    }

    [Fact]
    public void IsAccepted_Follows_The_Current_Operating_System()
    {
        string path = Path.Combine(Path.GetTempPath(), "XvePathValidation", "Sample.axaml");

        Assert.True(FileSystemPathValidation.IsAccepted(path));
        Assert.False(FileSystemPathValidation.IsAccepted("relative" + Path.DirectorySeparatorChar + "Sample.axaml"));
    }
}
