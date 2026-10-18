using System.Text.RegularExpressions;

namespace XamlVisualEditor.Core;

/// <summary>Hides the credentials that URLs in git arguments, git output and remote lists may contain.</summary>
public static partial class GitUrlCredentials
{
    /// <summary>
    /// Replaces the user information of every URL in <paramref name="text"/> with <c>***</c>: the user and password
    /// of <c>https://user:password@host</c> as well as a token written as <c>https://token@host</c>.
    /// </summary>
    public static string Mask(string text)
    {
        return string.IsNullOrEmpty(text) ? text : UrlUserInfoPattern().Replace(text, "${scheme}***@");
    }

    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)[^/@\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoPattern();
}
