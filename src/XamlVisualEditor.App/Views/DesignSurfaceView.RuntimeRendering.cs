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
    private bool _runtimeRenderFailed;

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

    private Control CreateRuntimeSurface(RuntimeDesignRenderResult rendered, ITypeMetadataService? metadata,
        MutableAstObjectNode rootNode)
    {
        DesignPreviewHookRegistry.Apply(rendered.Root, rootNode, (hook, exception) =>
            Log.Warning(exception, "Design preview hook {Hook} failed for {Document}", hook.GetType().FullName, rendered.DocumentUri));
        if (rendered.Root is not Window window)
        {
            ApplyWorkspaceThemes(rendered.Root, metadata);
            return rendered.Root;
        }
        try
        {
            ApplyWorkspaceThemes(window, metadata);
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
        bool empty = string.IsNullOrWhiteSpace(document.SyncEngine.CurrentText);
        if (!empty) Log.Error(exception, "Designer rendering failed for {DocumentPath}", document.FilePath);
        bool keepPrevious = !empty && ReferenceEquals(_runtimeDocumentOwner, document)
            && _rootControl is not null && _runtimeRenderedDocument is not null;
        if (!keepPrevious)
        {
            if (_rootControl is not null) _rootControl.LayoutUpdated -= OnRuntimeLayoutUpdated;
            canvas.Children.Clear();
            _rootControl = null;
            _runtimeRenderedDocument = null;
            _runtimeDocumentOwner = document;
        }
        else SetPreviewHitTest(_rootControl!, false);
        _runtimeRenderFailed = true;
        canvas.ContextMenu = null;
        document.SetRuntimeRenderFailure(exception);
        SetPreviewStatus(empty ? "Add a root control to preview this document."
            : keepPrevious ? "Preview out of date and not editable. See Problems for rendering errors."
            : "Preview unavailable. See Problems for rendering errors.");
        ClearHover();
        _adornerLayer?.ClearAll();
        _currentVm?.SetDesignTree(null, new Dictionary<Guid, DesignItem>(), new Dictionary<Control, DesignItem>());
        _currentVm?.ClearSelectionFromSync();
    }

    private void SetPreviewStatus(string? message)
    {
        if (this.FindControl<TextBlock>("PreviewStatusText") is not { } status) return;
        status.Text = message;
        status.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void OnRuntimeLayoutUpdated(object? sender, EventArgs args)
    {
        if (_runtimeDocumentOwner?.UseExternalPreviewer == true || _runtimeRenderFailed || _currentVm is null || _rootControl is null
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
