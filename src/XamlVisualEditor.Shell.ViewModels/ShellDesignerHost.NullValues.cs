using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Metadata;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Language;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class ShellDesignerHost : IDesignerPropertyValueHost
{
    public async Task<string> SetPropertyNullAsync(
        string nodeId, string propertyName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(
                () => SetPropertyNullAsync(nodeId, propertyName, cancellationToken), DispatcherPriority.Background);
        }

        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed || !document.SyncEngine.CanEditAst ||
            !Guid.TryParse(nodeId, out Guid id) || document.NodeMap.FindById(id) is not MutableAstObjectNode node)
        {
            throw new InvalidOperationException(UiStrings.DesignerSelectSynchronizedControl);
        }

        DesignerPropertyInfo? property = GetPropertiesCore(nodeId)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, propertyName, StringComparison.Ordinal));
        if (property is null || property.IsReadOnly || !property.CanAssignNull)
        {
            throw new InvalidOperationException(UiStrings.PropertyNullValueUnsupported);
        }

        MutableAstDocument ast = document.SyncEngine.CurrentDocument
            ?? throw new InvalidOperationException(UiStrings.DesignerDocumentNotSynchronized);
        var aliases = XamlNames.Namespaces(node, ast.NamespaceAliases);
        string? prefix = aliases.Where(pair => pair.Key.Length > 0 && pair.Value == XamlNames.XamlNamespace)
            .OrderBy(pair => pair.Key == "x" ? 0 : 1)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key).FirstOrDefault();
        bool declarePrefix = prefix is null;
        if (declarePrefix)
        {
            prefix = "x";
            int suffix = 1;
            while (aliases.ContainsKey(prefix))
            {
                prefix = "x" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }
        }

        bool clearImplicitContent = false;
        if (node.Children.Count > 0 && !property.IsAttached)
        {
            TypeMetadata? owner = document.MetadataService?.GetType(node.XmlNamespace, node.TypeName);
            Type? clrType = owner is null ? null : document.MetadataService?.ResolveClrType(owner);
            if (clrType is null)
            {
                throw new InvalidOperationException(UiStrings.PropertyImplicitContentTypeMissing);
            }

            clearImplicitContent = string.Equals(FindContentPropertyName(clrType), propertyName, StringComparison.Ordinal);
        }

        string expression = "{" + prefix + ":Null}";
        string localName = FindLocalProperty(document, node, propertyName)?.PropertyName ?? propertyName;
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = document.SyncEngine.BeginEditTransaction($"Set {propertyName} to null");
        if (declarePrefix) node.NamespaceAliases[prefix!] = XamlNames.XamlNamespace;
        if (clearImplicitContent) node.Children.Clear();
        node.SetPropertyValue(localName, expression);
        transaction.Commit();
        return expression;
    }

    private static string? FindContentPropertyName(Type type)
    {
        for (Type? owner = type; owner is not null; owner = owner.BaseType)
        {
            foreach (PropertyInfo property in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.CustomAttributes.Any(attribute => attribute.AttributeType == typeof(ContentAttribute)))
                {
                    return property.Name;
                }
            }
        }

        return null;
    }
}
