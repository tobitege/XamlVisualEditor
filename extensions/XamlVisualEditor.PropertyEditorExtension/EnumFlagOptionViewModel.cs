using System;
using ReactiveUI;

namespace XamlVisualEditor.PropertyEditorExtension;

/// <summary>A named enum mask, including zero and composite masks.</summary>
public sealed class EnumFlagOptionViewModel : ReactiveObject
{
    private readonly Action<EnumFlagOptionViewModel, bool> _setChecked;
    private bool _isChecked;

    public EnumFlagOptionViewModel(string name, ulong bits, Action<EnumFlagOptionViewModel, bool> setChecked)
    {
        Name = name;
        Bits = bits;
        _setChecked = setChecked;
    }

    public string Name { get; }
    public ulong Bits { get; }
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            this.RaiseAndSetIfChanged(ref _isChecked, value);
            _setChecked(this, value);
        }
    }

    internal void Refresh(bool isChecked) => this.RaiseAndSetIfChanged(ref _isChecked, isChecked, nameof(IsChecked));
}
