using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.TestControls;

// Standalone library for real project, metadata and designer integration.
[DisplayName("Library caption")]
[Category("Library content")]
public class CaptionControl : ContentControl
{
    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<CaptionControl, string>(nameof(Caption), "Caption");

    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }
}

public class LibraryGrid : Grid;

public abstract class AbstractControl : CaptionControl;

public class GenericControl<T> : CaptionControl;

public sealed class ClosedGenericControl : GenericControl<string>;

public sealed class ServiceControl : Control
{
    public ServiceControl(string service)
    {
        ArgumentNullException.ThrowIfNull(service);
    }
}

public sealed class ConstructorTrapControl : Control
{
    public ConstructorTrapControl()
    {
        throw new InvalidOperationException("Catalog discovery must not construct controls.");
    }
}

[DesignTimeVisible(false)]
public sealed class HiddenControl : Control;

public sealed class Button : CaptionControl;

public sealed class BUTTON : Control;
