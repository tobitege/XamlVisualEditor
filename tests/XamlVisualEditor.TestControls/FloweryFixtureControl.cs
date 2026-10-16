namespace XamlVisualEditor.TestControls;

/// <summary>A target-library control that uses the installed Flowery theme.</summary>
public sealed class FloweryFixtureControl : Flowery.Controls.DaisyButton
{
    protected override Type StyleKeyOverride => typeof(Flowery.Controls.DaisyButton);
}
