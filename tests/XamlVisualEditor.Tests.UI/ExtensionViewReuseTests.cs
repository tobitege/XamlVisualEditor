using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class ExtensionViewReuseTests
{
    [AvaloniaFact]
    public async Task Custom_view_model_survives_unrelated_contribution_changes_and_stays_owned_by_the_extension()
    {
        CommandRegistry commands = new();
        ExtensionContributionRegistry contributions = new();
        ExtensionViewRegistry views = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: commands,
            extensionContributionRegistry: contributions,
            extensionViewRegistry: views);
        CountingProvider provider = new();
        using IDisposable registeredProvider = views.RegisterCustomViewProvider("test.panel", provider);
        IDisposable registeredView = contributions.RegisterViews("test.reuse", new[]
        {
            new ExtensionViewContribution("test.panel", "Panel", ExtensionViewType.Custom, ExtensionViewLocation.Right, 50)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        DisposableViewModel created = Assert.Single(provider.Created);

        for (int i = 0; i < 3; i++)
        {
            using IDisposable menu = contributions.RegisterMenuItems("test.other" + i, new[]
            {
                new ExtensionMenuContribution("test.command" + i, "Command " + i, ExtensionMenuLocations.View, "views.right", 80 + i)
            });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        Assert.Single(provider.Created);
        ExtensionCustomViewModel hosted = Assert.Single(viewModel.ExtensionViews.OfType<ExtensionCustomViewModel>(), view => view.ViewId == "test.panel");
        Assert.Same(created, hosted.ViewModel);
        Assert.False(created.IsDisposed);

        registeredView.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        // The extension disposes its view models itself; a host dispose would run a second time on deactivation.
        Assert.DoesNotContain(viewModel.ExtensionViews, view => view.ViewId == "test.panel");
        Assert.False(created.IsDisposed);
    }

    private sealed class CountingProvider : ICustomViewProvider
    {
        public System.Collections.Generic.List<DisposableViewModel> Created { get; } = new();

        public object CreateViewModel()
        {
            DisposableViewModel created = new();
            Created.Add(created);
            return created;
        }
    }

    private sealed class DisposableViewModel : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
