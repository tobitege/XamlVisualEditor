using Dock.Model.Core;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Assigns empty capability objects to dockables and docks.</summary>
/// <remarks>
/// The Dock templates bind to <c>DockCapabilityOverrides.*</c> and <c>DockCapabilityPolicy.*</c>. A null object
/// breaks these bindings and floods the log with binding errors. Empty objects keep every capability unset,
/// so the resolver still falls back to the dockable, dock and root defaults as before.
/// </remarks>
public static class DockCapabilityDefaults
{
    /// <summary>Ensures that the dockable and, for docks, the dock policy carry capability objects.</summary>
    public static void Apply(IDockable dockable)
    {
        dockable.DockCapabilityOverrides ??= new DockCapabilityOverrides();
        if (dockable is IDock dock)
        {
            dock.DockCapabilityPolicy ??= new DockCapabilityPolicy();
        }
    }
}
