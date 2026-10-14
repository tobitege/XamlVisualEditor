using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Xunit;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.Tests.Unit;

public class WorkspaceDesignThemeLoaderTests
{
    // The fixed end of the localized report after its last placeholder, in whatever UI language the test runs.
    private static string ScopeFailedTail => UiStrings.DesignThemesScopeFailed[(UiStrings.DesignThemesScopeFailed.IndexOf("{1}", StringComparison.Ordinal) + 3)..];

    [AvaloniaFact]
    public void LoadFromApplicationXaml_AppliesTheLoadableParts_WhenTheApplicationScopeCannotBeCompiled()
    {
        const string applicationXaml = """
            <Application xmlns="https://github.com/avaloniaui"
                         xmlns:missing="using:XamlVisualEditor.Tests.Missing">
              <Application.DataTemplates>
                <missing:ViewLocator />
              </Application.DataTemplates>
              <Application.Styles>
                <FluentTheme />
                <StyleInclude Source="avares://XamlVisualEditor.Tests.MissingAssembly/Styles.axaml" />
                <Style Selector="TextBlock.probe">
                  <Setter Property="Foreground" Value="Tomato" />
                </Style>
              </Application.Styles>
            </Application>
            """;
        string appPath = Path.Combine(Path.GetTempPath(), "xve-app-theme-" + Guid.NewGuid().ToString("N") + ".axaml");
        File.WriteAllText(appPath, applicationXaml);
        IReadOnlyList<Func<IStyle?>> previousStyles = WorkspaceDesignThemeRegistry.StyleFactories;
        IReadOnlyList<Func<IResourceProvider?>> previousResources = WorkspaceDesignThemeRegistry.ResourceFactories;
        List<string> reports = new();
        try
        {
            WorkspaceDesignThemeLoader.LoadFromApplicationXaml(appPath, reports.Add);
            Func<IStyle?> factory = Assert.Single(WorkspaceDesignThemeRegistry.StyleFactories);

            Styles scope = Assert.IsType<Styles>(factory());
            Style probe = Assert.IsType<Style>(Assert.Single(scope.OfType<Styles>().SelectMany(styles => styles)));
            Assert.Contains("probe", probe.Selector!.ToString(), StringComparison.Ordinal);
            Assert.Collection(reports,
                report => Assert.EndsWith(ScopeFailedTail, report, StringComparison.Ordinal),
                report => Assert.Contains("avares://XamlVisualEditor.Tests.MissingAssembly/Styles.axaml", report, StringComparison.Ordinal));

            Assert.IsType<Styles>(factory());
            Assert.Equal(2, reports.Count);
        }
        finally
        {
            WorkspaceDesignThemeRegistry.Update(previousStyles, previousResources);
            File.Delete(appPath);
        }
    }

    [Fact]
    public void GetApplicationXamlOverridePath_ReturnsNull_WhenVariableIsUnset()
    {
        using EnvironmentVariableScope scope = new(WorkspaceDesignThemeLoader.ApplicationXamlOverrideVariable, null);

        Assert.Null(WorkspaceDesignThemeLoader.GetApplicationXamlOverridePath());
    }

    [Fact]
    public void GetApplicationXamlOverridePath_ReturnsNull_WhenFileDoesNotExist()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "App.axaml");
        using EnvironmentVariableScope scope = new(WorkspaceDesignThemeLoader.ApplicationXamlOverrideVariable, missingPath);

        Assert.Null(WorkspaceDesignThemeLoader.GetApplicationXamlOverridePath());
    }

    [Fact]
    public void GetApplicationXamlOverridePath_ReturnsFullPath_WhenFileExists()
    {
        string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".axaml");
        File.WriteAllText(filePath, "<Application />");
        try
        {
            using EnvironmentVariableScope scope = new(WorkspaceDesignThemeLoader.ApplicationXamlOverrideVariable, filePath);

            Assert.Equal(Path.GetFullPath(filePath), WorkspaceDesignThemeLoader.GetApplicationXamlOverridePath());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void GetApplicationXamlOverridePath_ReturnsNull_WhenValueIsWhitespace()
    {
        using EnvironmentVariableScope scope = new(WorkspaceDesignThemeLoader.ApplicationXamlOverrideVariable, "   ");

        Assert.Null(WorkspaceDesignThemeLoader.GetApplicationXamlOverridePath());
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
        }
    }
}
