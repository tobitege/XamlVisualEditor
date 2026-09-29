using System;
using System.Diagnostics;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using ReactiveUI;
using XamlVisualEditor.Acp;

namespace XamlVisualEditor.AcpExtension.Views;

public sealed partial class AcpToolView : UserControl
{
    public AcpToolView()
    {
        InitializeComponent();
    }
}
