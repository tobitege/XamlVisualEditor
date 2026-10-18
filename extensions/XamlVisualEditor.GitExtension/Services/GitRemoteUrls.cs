namespace XamlVisualEditor.GitExtension.Services;

/// <summary>Formats remote URLs for display.</summary>
internal static class GitRemoteUrls
{
    private const string HIDDEN_PASSWORD = "***";

    /// <summary>
    /// Returns <paramref name="url"/> with the password of its user information replaced, such as the token in
    /// <c>https://user:token@host/repo.git</c>, so a list or a screenshot does not show it. Other URLs stay as they are.
    /// </summary>
    public static string Redact(string url)
    {
        int start = url.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return url;
        }

        start += 3;
        int end = url.IndexOfAny(['/', '?', '#'], start);
        if (end < 0)
        {
            end = url.Length;
        }

        int at = url.LastIndexOf('@', end - 1, end - start);
        int colon = at < 0 ? -1 : url.IndexOf(':', start, at - start);
        return colon < 0 ? url : string.Concat(url.AsSpan(0, colon + 1), HIDDEN_PASSWORD, url.AsSpan(at));
    }
}
