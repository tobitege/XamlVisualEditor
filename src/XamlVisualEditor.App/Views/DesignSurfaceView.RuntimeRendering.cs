using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Serilog;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.App.Views;

public sealed partial class DesignSurfaceView
{
    private RuntimeDesignRenderResult? _runtimeRenderedDocument;
    private DesignerDocumentViewModel? _runtimeDocumentOwner;
    private Border? _renderFailurePanel;

    private static RuntimeDesignRenderResult RenderRuntimeDocument(DesignerDocumentViewModel document, MutableAstDocument source)
    {
        if (!document.SyncEngine.CanEditAst)
            throw new InvalidOperationException("Correct the XAML syntax errors before updating the preview.");
        var metadata = document.MetadataService
            ?? throw new InvalidOperationException("The designer type metadata is unavailable.");
        Assembly? localAssembly = null;
        string? className = source.Root?.Properties.FirstOrDefault(property => property.PropertyName == "x:Class"
            || property.XmlNamespace == "http://schemas.microsoft.com/winfx/2006/xaml"
                && property.PropertyName.EndsWith(":Class", StringComparison.Ordinal))?.Value is MutableAstTextNode text
            ? text.Text : null;
        if (!string.IsNullOrWhiteSpace(className))
        {
            TypeMetadata[] matches = metadata.GetAvailableTypes().Where(type => type.FullName == className).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException($"The code-behind type {className} is present in multiple loaded assemblies.");
            if (matches.Length == 1) localAssembly = metadata.ResolveClrType(matches[0])?.Assembly;
        }
        if (localAssembly is null && source.Root is { } root
            && metadata.GetType(root.XmlNamespace, root.TypeName) is { IsStandardControl: false } rootType)
            localAssembly = metadata.ResolveClrType(rootType)?.Assembly;

        return new RuntimeDesignRenderer(metadata).Render(source, new Uri(document.FilePath, UriKind.Absolute), localAssembly);
    }

    private Control CreateRuntimeSurface(RuntimeDesignRenderResult rendered, ITypeMetadataService? metadata)
    {
        if (rendered.Root is not Window window)
        {
            ApplyWorkspaceThemes(rendered.Root, metadata);
            return rendered.Root;
        }
        try
        {
            ApplyWorkspaceThemes(window, metadata);
            ApplyDesignModeProperties(window);
            return new DesignWindowContentHost(window);
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    private void ShowRuntimeRenderFailure(Panel canvas, DesignerDocumentViewModel document, Exception exception)
    {
        Log.Error(exception, "Designer rendering failed for {DocumentPath}", document.FilePath);
        if (_renderFailurePanel is not null) canvas.Children.Remove(_renderFailurePanel);
        _renderFailurePanel = new Border
        {
            Background = Brushes.DarkRed, Padding = new Thickness(12), MaxHeight = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
            Child = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                    Text = $"Render failed: {document.FilePath}\n{exception.Message}\n"
                        + "The preview is not editable. Check the document and its project dependencies; details are in the application log."
                }
            }
        };
        canvas.Children.Add(_renderFailurePanel);
        _adornerLayer?.ClearAll();
        _currentVm?.SetDesignTree(null, new Dictionary<Guid, DesignItem>(), new Dictionary<Control, DesignItem>());
        _currentVm?.ClearSelectionFromSync();
    }

    private void OnRuntimeLayoutUpdated(object? sender, EventArgs args)
    {
        if (_renderFailurePanel is not null || _currentVm is null || _rootControl is null
            || _runtimeRenderedDocument is null || _runtimeDocumentOwner?.SyncEngine.CurrentDocument?.Root is not { } root
            || !ReferenceEquals(_runtimeDocumentOwner.DesignSurface, _currentVm)) return;
        IReadOnlyDictionary<Control, Guid> identities = _runtimeRenderedDocument.GetControlNodeIds(_rootControl);
        int expectedCount = identities.Count + (identities.ContainsKey(_rootControl) ? 0 : 1);
        if (expectedCount == _currentVm.ControlMap.Count && identities.All(pair =>
            _currentVm.ControlMap.TryGetValue(pair.Key, out DesignItem? item) && item.AstNodeId == pair.Value)) return;

        Guid[] selected = _currentVm.Selection.SelectedItems.OfType<DesignItem>().Select(item => item.AstNodeId).ToArray();
        Dictionary<Guid, DesignItem> items = new();
        Dictionary<Control, DesignItem> controls = new();
        DesignItem rootItem = BuildDesignItemTree(root, _rootControl, identities, items, controls);
        _currentVm.SetDesignTree(rootItem, items, controls);
        _currentVm.SelectNodesFromSync(selected);
    }
}
