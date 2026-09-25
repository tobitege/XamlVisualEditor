using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignPreviewHookTests
{
    [Fact]
    public void Registry_RegistersAndRemovesHooks()
    {
        RecordingHook hook = new();

        IDisposable registration = DesignPreviewHookRegistry.Register(hook);
        Assert.Contains(hook, DesignPreviewHookRegistry.Hooks);

        registration.Dispose();
        Assert.DoesNotContain(hook, DesignPreviewHookRegistry.Hooks);
    }

    [Fact]
    public void Registry_DisposeTwiceLeavesOtherHooksRegistered()
    {
        RecordingHook removed = new();
        RecordingHook kept = new();
        IDisposable registration = DesignPreviewHookRegistry.Register(removed);
        using IDisposable keptRegistration = DesignPreviewHookRegistry.Register(kept);

        registration.Dispose();
        registration.Dispose();

        Assert.DoesNotContain(removed, DesignPreviewHookRegistry.Hooks);
        Assert.Contains(kept, DesignPreviewHookRegistry.Hooks);
    }

    [Fact]
    public void Registry_IgnoresRepeatedRegistrationOfTheSameHook()
    {
        RecordingHook hook = new();
        IDisposable first = DesignPreviewHookRegistry.Register(hook);
        IDisposable repeated = DesignPreviewHookRegistry.Register(hook);

        Assert.Single(DesignPreviewHookRegistry.Hooks, registered => ReferenceEquals(registered, hook));

        repeated.Dispose();
        Assert.Contains(hook, DesignPreviewHookRegistry.Hooks);

        first.Dispose();
        Assert.DoesNotContain(hook, DesignPreviewHookRegistry.Hooks);
    }

    [AvaloniaFact]
    public void Apply_CallsARepeatedlyRegisteredHookOnce()
    {
        RecordingHook hook = new();
        Border root = new();
        MutableAstObjectNode node = new() { TypeName = "UserControl", XmlNamespace = "https://github.com/avaloniaui" };

        using (DesignPreviewHookRegistry.Register(hook))
        using (DesignPreviewHookRegistry.Register(hook))
        {
            DesignPreviewHookRegistry.Apply(root, node, (_, _) => { });
        }

        Assert.Single(hook.Calls);
    }

    [Fact]
    public void Register_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => DesignPreviewHookRegistry.Register(null!));
    }

    [AvaloniaFact]
    public void Apply_PassesRootAndSourceNodeToEveryHookInRegistrationOrder()
    {
        List<string> order = new();
        RecordingHook first = new(order, "first");
        RecordingHook second = new(order, "second");
        Border root = new();
        MutableAstObjectNode node = new() { TypeName = "UserControl", XmlNamespace = "https://github.com/avaloniaui" };

        using (DesignPreviewHookRegistry.Register(first))
        using (DesignPreviewHookRegistry.Register(second))
        {
            DesignPreviewHookRegistry.Apply(root, node, (_, exception) => throw exception);
        }

        Assert.Equal(new[] { "first", "second" }, order.Where(name => name is "first" or "second"));
        Assert.Same(root, Assert.Single(first.Calls).Root);
        Assert.Same(node, Assert.Single(first.Calls).Node);
        Assert.Same(root, Assert.Single(second.Calls).Root);
    }

    [AvaloniaFact]
    public void Apply_ReportsFailingHookAndRunsTheOthers()
    {
        FailingHook failing = new();
        RecordingHook recording = new();
        List<(IDesignPreviewHook Hook, Exception Error)> errors = new();
        Border root = new();
        MutableAstObjectNode node = new() { TypeName = "UserControl", XmlNamespace = "https://github.com/avaloniaui" };

        using (DesignPreviewHookRegistry.Register(failing))
        using (DesignPreviewHookRegistry.Register(recording))
        {
            DesignPreviewHookRegistry.Apply(root, node, (hook, error) => errors.Add((hook, error)));
        }

        (IDesignPreviewHook failedHook, Exception failure) = Assert.Single(errors, error => ReferenceEquals(error.Hook, failing));
        Assert.Same(failing, failedHook);
        Assert.Equal(FailingHook.Message, failure.Message);
        Assert.Single(recording.Calls);
    }

    [AvaloniaFact]
    public void Apply_RejectsNullArguments()
    {
        Border root = new();
        MutableAstObjectNode node = new() { TypeName = "UserControl" };

        Assert.Throws<ArgumentNullException>(() => DesignPreviewHookRegistry.Apply(null!, node, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() => DesignPreviewHookRegistry.Apply(root, null!, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() => DesignPreviewHookRegistry.Apply(root, node, null!));
    }

    private sealed class RecordingHook(List<string>? order = null, string? name = null) : IDesignPreviewHook
    {
        public List<(Control Root, MutableAstObjectNode Node)> Calls { get; } = new();

        public void PostProcess(Control root, MutableAstObjectNode rootNode)
        {
            Calls.Add((root, rootNode));
            if (name is not null)
            {
                order?.Add(name);
            }
        }
    }

    private sealed class FailingHook : IDesignPreviewHook
    {
        public const string Message = "Hook failed.";

        public void PostProcess(Control root, MutableAstObjectNode rootNode) => throw new InvalidOperationException(Message);
    }
}
