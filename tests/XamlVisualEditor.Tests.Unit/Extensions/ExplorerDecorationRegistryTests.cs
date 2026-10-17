using System;
using System.Collections.Generic;
using System.IO;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class ExplorerDecorationRegistryTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "xve-decorations");
    private static readonly ExplorerDecoration Modified = new(ExplorerDecorationKind.Modified, "M", "Modified");
    private static readonly ExplorerDecoration Added = new(ExplorerDecorationKind.Added, "U", "Untracked");

    [Fact]
    public void Set_replaces_the_entries_of_the_layer_and_raises_Changed_once_per_call()
    {
        ExplorerDecorationRegistry registry = new();
        int changes = 0;
        registry.Changed += (_, _) => changes++;
        using IExplorerDecorationLayer layer = registry.CreateLayer();
        string first = Path.Combine(Root, "a.txt");
        string second = Path.Combine(Root, "b.txt");

        layer.Set([Entry(first, Modified), Entry(second, Added)]);
        layer.Set([Entry(second, Modified)]);

        Assert.Equal(2, changes);
        Assert.Null(registry.Find(first));
        Assert.Equal(Modified, registry.Find(second));
    }

    [Fact]
    public void Dispose_removes_the_entries_and_ignores_later_calls()
    {
        ExplorerDecorationRegistry registry = new();
        IExplorerDecorationLayer layer = registry.CreateLayer();
        string file = Path.Combine(Root, "a.txt");
        layer.Set([Entry(file, Modified)]);
        int changes = 0;
        registry.Changed += (_, _) => changes++;

        layer.Dispose();
        layer.Set([Entry(file, Modified)]);
        layer.Dispose();

        Assert.Equal(1, changes);
        Assert.Null(registry.Find(file));
    }

    [Fact]
    public void The_first_layer_with_an_entry_wins_and_the_next_one_fills_in()
    {
        ExplorerDecorationRegistry registry = new();
        using IExplorerDecorationLayer first = registry.CreateLayer();
        using IExplorerDecorationLayer second = registry.CreateLayer();
        string shared = Path.Combine(Root, "shared.txt");
        string other = Path.Combine(Root, "other.txt");

        second.Set([Entry(shared, Added), Entry(other, Added)]);
        first.Set([Entry(shared, Modified)]);

        Assert.Equal(Modified, registry.Find(shared));
        Assert.Equal(Added, registry.Find(other));
        first.Dispose();
        Assert.Equal(Added, registry.Find(shared));
    }

    [Fact]
    public void Keys_are_full_paths_without_trailing_separator()
    {
        ExplorerDecorationRegistry registry = new();
        using IExplorerDecorationLayer layer = registry.CreateLayer();
        string folder = Path.Combine(Root, "Folder");
        string file = Path.Combine(folder, "Größe ä.xaml");

        layer.Set([Entry(folder + Path.DirectorySeparatorChar, Modified), Entry(file.Replace('\\', '/'), Added)]);

        Assert.Equal(Modified, registry.Find(folder));
        Assert.Equal(Modified, registry.Find(folder + Path.DirectorySeparatorChar));
        Assert.Equal(Added, registry.Find(file));
        Assert.Null(registry.Find(null));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Added, registry.Find(file.ToUpperInvariant()));
        }
    }

    private static KeyValuePair<string, ExplorerDecoration> Entry(string path, ExplorerDecoration decoration) => new(path, decoration);
}
