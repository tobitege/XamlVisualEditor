using System.ComponentModel;

namespace XamlVisualEditor.Core;

/// <summary>Display state of an editor document that its view shows; changes are reported like any other property.</summary>
public interface ITextEditorDisplayState : INotifyPropertyChanged
{
    /// <summary>Gets or sets the font size of the editor.</summary>
    double FontSize { get; set; }

    /// <summary>Gets or sets whether long lines wrap.</summary>
    bool WordWrap { get; set; }

    /// <summary>Gets or sets whether line numbers are shown.</summary>
    bool ShowLineNumbers { get; set; }
}
