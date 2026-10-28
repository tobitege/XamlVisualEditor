using System;
using System.Collections.Generic;
using System.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Xaml.Intellisense;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class BindingPathCompletionTests
{
    private const string Caret = "|";

    [Theory]
    [InlineData("""
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:App.ViewModels" x:DataType="vm:RootViewModel">
          <TextBlock Text="{Binding Path=|}" />
        </UserControl>
        """, "RootTitle")]
    [InlineData("""
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:App.ViewModels" x:DataType="vm:RootViewModel">
          <ListBox>
            <ListBox.ItemTemplate>
              <DataTemplate x:DataType="vm:ItemViewModel">
                <TextBlock Text="{Binding Path=|}" />
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
        </UserControl>
        """, "ItemName")]
    [InlineData("""
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:App.ViewModels" x:DataType="vm:RootViewModel">
          <ListBox>
            <ListBox.ItemTemplate>
              <DataTemplate x:DataType="vm:ItemViewModel">
                <TextBlock Text="Item" />
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
          <TextBlock Text="{Binding Path=|}" />
        </UserControl>
        """, "RootTitle")]
    [InlineData("""
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:App.ViewModels" x:DataType="vm:RootViewModel">
          <Border>
            <TextBlock x:DataType="vm:ItemViewModel" Text="{Binding Path=|}" />
          </Border>
        </UserControl>
        """, "ItemName")]
    [InlineData("""
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:App.ViewModels" x:DataType="vm:RootViewModel">
          <ContentControl>
            <ContentControl.ContentTemplate>
              <DataTemplate DataType="vm:ItemViewModel">
                <TextBlock Text="{Binding Path=|}" />
              </DataTemplate>
            </ContentControl.ContentTemplate>
          </ContentControl>
        </UserControl>
        """, "ItemName")]
    [InlineData("""
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:App.ViewModels" x:DataType="vm:RootViewModel">
          <ContentControl>
            <ContentControl.ContentTemplate>
              <DataTemplate DataType="{x:Type vm:ItemViewModel}">
                <TextBlock Text="{Binding Path=|}" />
              </DataTemplate>
            </ContentControl.ContentTemplate>
          </ContentControl>
        </UserControl>
        """, "ItemName")]
    [InlineData("""
        <?xml version="1.0" encoding="utf-8"?>
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:App.ViewModels" x:DataType="vm:RootViewModel">
          <UserControl.Styles>
            <Style Selector="StackPanel > TextBlock" />
          </UserControl.Styles>
          <!-- Items > one: <DataTemplate x:DataType="vm:ItemViewModel"> -->
          <StackPanel	Orientation="Vertical">
            <TextBlock Text="{Binding Path=|}" />
          </StackPanel>
        </UserControl>
        """, "RootTitle")]
    public void Path_Completion_Uses_The_Data_Type_Of_The_Nearest_Scope(string document, string expected)
    {
        IReadOnlyList<string> names = CompletePath(document);

        Assert.Contains(expected, names);
        Assert.DoesNotContain(expected == "RootTitle" ? "ItemName" : "RootTitle", names);
    }

    [Fact]
    public void Path_Completion_In_A_Data_Template_Without_Data_Type_Offers_No_Members()
    {
        IReadOnlyList<string> names = CompletePath("""
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:App.ViewModels" x:DataType="vm:RootViewModel">
              <ListBox>
                <ListBox.ItemTemplate>
                  <DataTemplate>
                    <TextBlock Text="{Binding Path=|}" />
                  </DataTemplate>
                </ListBox.ItemTemplate>
              </ListBox>
            </UserControl>
            """);

        Assert.DoesNotContain("RootTitle", names);
        Assert.DoesNotContain("ItemName", names);
    }

    [Fact]
    public void Closing_Tag_Completion_Skips_Comments_Instructions_And_Quoted_Brackets()
    {
        CompletionContext context = new()
        {
            TextBefore = "<?xml version=\"1.0\"?>\n<Grid>\n  <!-- <Border> -->\n  <Style Selector=\"Grid > Button\">\n"
                         + "    <Button\tContent=\"a > b\" />\n  </",
            Offset = 0,
            Trigger = CompletionTrigger.CharacterTyped
        };

        CompletionItem item = Assert.Single(new ClosingTagCompletionProvider().GetCompletions(context));

        Assert.Equal("</Style>", item.DisplayText);
    }

    // Completion triggers when the value of Path is opened with a quote, so the caret follows Path=".
    private static IReadOnlyList<string> CompletePath(string document)
    {
        int caret = document.IndexOf(Caret, StringComparison.Ordinal);
        string text = document.Remove(caret, Caret.Length).Insert(caret, "\"");
        CompletionContext context = new()
        {
            TextBefore = text[..(caret + 1)],
            DocumentText = text,
            Offset = caret + 1,
            Trigger = CompletionTrigger.CharacterTyped,
            Metadata = new ViewModelMetadata()
        };

        return new AttributeValueCompletionProvider().GetCompletions(context)
            .Where(item => item.Kind == CompletionItemKind.Value)
            .Select(item => item.DisplayText)
            .ToArray();
    }

    private sealed class ViewModelMetadata : ITypeMetadataService
    {
        private const string Namespace = "using:App.ViewModels";

        private static readonly Dictionary<string, string> s_properties = new()
        {
            ["RootViewModel"] = "RootTitle",
            ["ItemViewModel"] = "ItemName"
        };

        public TypeMetadata? GetType(string xmlNamespace, string typeName) =>
            xmlNamespace == Namespace && s_properties.ContainsKey(typeName)
                ? new TypeMetadata
                {
                    FullName = "App.ViewModels." + typeName,
                    Name = typeName,
                    XmlNamespace = Namespace,
                    ClrNamespace = "App.ViewModels",
                    AssemblyName = "App"
                }
                : null;

        public IReadOnlyList<PropertyMetadata> GetProperties(TypeMetadata type) =>
            s_properties.TryGetValue(type.Name, out string? property)
                ? [new PropertyMetadata { Name = property, TypeFullName = "System.String", Kind = PropertyKind.ClrProperty }]
                : [];

        public IReadOnlyList<TypeMetadata> GetAvailableTypes(string? xmlNamespace = null) => [];

        public IReadOnlyList<EventMetadata> GetEvents(TypeMetadata type) => [];

        public IReadOnlyList<string> GetAvailableNamespaces() => [];

        public void LoadAssembly(string assemblyPath)
        {
        }

        public void LoadAssemblies(IEnumerable<string> assemblyPaths)
        {
        }

        public Type? ResolveClrType(TypeMetadata type) => null;
    }
}
