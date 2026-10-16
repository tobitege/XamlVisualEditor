namespace XamlVisualEditor.Xaml.Ast;

/// <summary>An XML comment or processing instruction; null Target denotes a comment.</summary>
public sealed record XmlTrivia(string? Target, string Content);
