using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.AcpExtension;
using XamlVisualEditor.AcpExtension.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.IdeBridgeExtension.Views;
using XamlVisualEditor.McpExtension.Views;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class ServerPanelLayoutTests
{
    private const double Tolerance = 0.5;

    private static readonly IReadOnlyDictionary<string, string> LongValues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Status"] = "Running: http listener bound on all interfaces, 3 clients connected, waiting for the next workspace handshake",
        ["Endpoint"] = "http://127.0.0.1:47120/xaml-visual-editor/mcp/very/long/path/segment/that/keeps/going/for/layout",
        ["Workspace"] = @"D:\github\XamlVisualEditor\extensions\XamlVisualEditor.IdeBridgeExtension\Views\SubFolder\AnotherFolder\Deep",
        ["Token"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        ["AgentName"] = "very-long-agent-name-with-many-segments/vendor/product/preview-build-2026.09.22"
    };

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(360)]
    [InlineData(900)]
    public async Task IdeBridgePanelKeepsControlsInsideAndDisjoint(double width)
    {
        IdeBridgePanelView view = new();
        await AssertPanelLayoutAsync(view, width, UiStrings.IdeBridge, "IdeBridgeStatus", "IdeBridgeEndpoint", "IdeBridgeWorkspace", "IdeBridgeToken");
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(360)]
    [InlineData(900)]
    public async Task McpPanelKeepsControlsInsideAndDisjoint(double width)
    {
        McpPanelView view = new();
        await AssertPanelLayoutAsync(view, width, UiStrings.Mcp, "McpStatus", "McpEndpoint", "McpWorkspace", "McpToken");
    }

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(360)]
    [InlineData(900)]
    public async Task AcpPanelKeepsControlsInsideAndDisjoint(double width)
    {
        using AcpToolViewModel viewModel = new();
        AcpToolView view = new() { DataContext = viewModel };
        await AssertPanelLayoutAsync(view, width, UiStrings.Acp, "AcpStatus", "AcpAgentName");
    }

    private static async Task AssertPanelLayoutAsync(Control view, double width, string panelTitle, params string[] longValueControls)
    {
        Window window = new() { Content = view, Width = width, Height = 760 };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            foreach (string name in longValueControls)
            {
                Control control = view.FindControl<Control>(name) ?? throw new Xunit.Sdk.XunitException($"Control '{name}' not found.");
                string value = LongValues.Single(pair => name.EndsWith(pair.Key, StringComparison.Ordinal)).Value;
                switch (control)
                {
                    case TextBlock textBlock:
                        textBlock.Text = value;
                        break;
                    case TextBox textBox:
                        textBox.Text = value;
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException($"Control '{name}' is a {control.GetType().Name}.");
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            view.UpdateLayout();

            Assert.Equal(width, view.Bounds.Width, 1.0);
            Control[] ownControls = view.GetVisualDescendants()
                .OfType<Control>()
                .Where(control => control.TemplatedParent is null && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0)
                .ToArray();
            Assert.NotEmpty(ownControls);

            foreach (string name in longValueControls)
            {
                Control control = view.FindControl<Control>(name)!;
                Assert.Contains(control, ownControls);
            }

            List<string> failures = new();
            foreach (Control control in ownControls)
            {
                Rect rect = ToViewRect(control, view);
                if (rect.Left < -Tolerance || rect.Right > view.Bounds.Width + Tolerance)
                {
                    failures.Add($"{Describe(control)} overflows horizontally: {rect} in width {view.Bounds.Width}");
                }

                if (rect.Top < -Tolerance || rect.Bottom > view.Bounds.Height + Tolerance)
                {
                    failures.Add($"{Describe(control)} overflows vertically: {rect} in height {view.Bounds.Height}");
                }
            }

            foreach (Panel panel in ownControls.OfType<Panel>())
            {
                Control[] children = panel.Children
                    .Where(child => child.IsEffectivelyVisible && child.Bounds.Width > 0 && child.Bounds.Height > 0)
                    .ToArray();
                for (int i = 0; i < children.Length; i++)
                {
                    Rect first = ToViewRect(children[i], view);
                    for (int j = i + 1; j < children.Length; j++)
                    {
                        Rect second = ToViewRect(children[j], view);
                        Rect intersection = first.Intersect(second);
                        if (intersection.Width > Tolerance && intersection.Height > Tolerance)
                        {
                            failures.Add($"{Describe(children[i])} {first} overlaps {Describe(children[j])} {second} inside {Describe(panel)}");
                        }
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

            TextBlock[] titleBlocks = ownControls.OfType<TextBlock>()
                .Where(block => string.Equals(block.Text, panelTitle, StringComparison.Ordinal))
                .ToArray();
            Assert.Empty(titleBlocks);
        }
        finally
        {
            window.Close();
        }
    }

    private static Rect ToViewRect(Control control, Control view)
    {
        Point origin = control.TranslatePoint(default, view) ?? throw new Xunit.Sdk.XunitException($"{Describe(control)} is not attached to the view.");
        return new Rect(origin, control.Bounds.Size);
    }

    private static string Describe(Control control)
    {
        string text = control switch
        {
            TextBlock block => block.Text ?? string.Empty,
            Button button => button.Content?.ToString() ?? string.Empty,
            _ => string.Empty
        };
        return string.IsNullOrEmpty(control.Name)
            ? $"{control.GetType().Name}('{text}')"
            : $"{control.GetType().Name}#{control.Name}";
    }
}
