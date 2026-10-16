using System;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Writes extension log messages to the host logger and to the "TFVC" output channel.</summary>
public sealed class TfvcOutputLogger : IExtensionLogger
{
    private readonly IExtensionLogger _inner;
    private readonly IOutputChannel? _channel;

    /// <summary>Creates the logger.</summary>
    public TfvcOutputLogger(IExtensionLogger inner, IOutputChannel? channel)
    {
        _inner = inner;
        _channel = channel;
    }

    /// <inheritdoc />
    public void Info(string message)
    {
        _inner.Info(message);
        Append("INFO", message, null);
    }

    /// <inheritdoc />
    public void Warn(string message)
    {
        _inner.Warn(message);
        Append("WARN", message, null);
    }

    /// <inheritdoc />
    public void Error(string message, Exception? exception = null)
    {
        _inner.Error(message, exception);
        Append("ERROR", message, exception);
    }

    private void Append(string level, string message, Exception? exception)
    {
        if (_channel is null)
        {
            return;
        }

        string line = DateTime.Now.ToString("HH:mm:ss") + " " + level + " " + message;
        if (exception is not null)
        {
            line += " " + exception.GetType().Name + ": " + exception.Message;
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
