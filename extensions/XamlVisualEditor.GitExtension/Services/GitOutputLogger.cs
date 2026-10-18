using System.Globalization;
using System.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// Writes extension log messages to the host logger and to the "Git" output channel, and writes the git commands
/// the extension ran to the channel. User names and passwords in URLs are masked.
/// </summary>
public sealed class GitOutputLogger : IExtensionLogger
{
    private readonly IExtensionLogger _inner;
    private readonly IOutputChannel? _channel;

    /// <summary>Creates the logger.</summary>
    public GitOutputLogger(IExtensionLogger inner, IOutputChannel? channel)
    {
        _inner = inner;
        _channel = channel;
    }

    /// <inheritdoc />
    public void Info(string message)
    {
        message = GitUrlCredentials.Mask(message);
        _inner.Info(message);
        Append("INFO " + message);
    }

    /// <inheritdoc />
    public void Warn(string message)
    {
        message = GitUrlCredentials.Mask(message);
        _inner.Warn(message);
        Append("WARN " + message);
    }

    /// <inheritdoc />
    public void Error(string message, Exception? exception = null)
    {
        message = GitUrlCredentials.Mask(message);
        _inner.Error(message, exception);
        string line = "ERROR " + message;
        if (exception is not null)
        {
            line += " " + exception.GetType().Name + ": " + GitUrlCredentials.Mask(exception.Message);
        }

        Append(line);
    }

    /// <summary>
    /// Writes a finished git command as <c>&gt; git args [duration]</c>, followed by git's error output.
    /// </summary>
    public void LogCommand(GitCommandLogEntry entry)
    {
        StringBuilder line = new("> git");
        foreach (string argument in entry.Arguments)
        {
            line.Append(' ').Append(QuoteArgument(argument));
        }

        line.Append(" [")
            .Append(((long)entry.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture))
            .Append(" ms]");
        if (entry.ExitCode != 0)
        {
            line.Append(" (exit code ").Append(entry.ExitCode.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        Append(GitUrlCredentials.Mask(line.ToString()));
        if (entry.ErrorOutput.Length > 0)
        {
            Append(GitUrlCredentials.Mask(entry.ErrorOutput));
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        return "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private void Append(string line)
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            _channel.AppendLine(line);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
