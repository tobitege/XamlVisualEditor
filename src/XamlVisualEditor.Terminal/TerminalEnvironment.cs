using System;
using System.Collections;
using System.Collections.Generic;

namespace XamlVisualEditor.Terminal;

/// <summary>
/// The environment of a terminal session: the process environment as it was when
/// <see cref="CaptureStartup"/> ran, so variables that the host sets for itself later, such as the
/// MSBuild paths an MSBuild locator registers, do not reach the shell.
/// </summary>
public static class TerminalEnvironment
{
    private static Dictionary<string, string>? s_startup;

    /// <summary>
    /// Captures the process environment. Call it before the host changes the environment; later
    /// calls keep the first capture. Without a capture, sessions start from the current environment.
    /// </summary>
    public static void CaptureStartup() => s_startup ??= ReadCurrent();

    /// <summary>The captured startup environment, or the current one when none was captured.</summary>
    internal static IReadOnlyDictionary<string, string> Base => s_startup ?? ReadCurrent();

    /// <summary>
    /// Builds a session environment: <paramref name="baseEnvironment"/>, then the provider defaults,
    /// then <paramref name="overrides"/>, where an empty value removes the variable.
    /// </summary>
    internal static Dictionary<string, string> Compose(
        IReadOnlyDictionary<string, string> baseEnvironment,
        Action<Dictionary<string, string>> applyDefaults,
        IReadOnlyDictionary<string, string> overrides)
    {
        Dictionary<string, string> result = new(baseEnvironment, KeyComparer);
        applyDefaults(result);
        foreach (KeyValuePair<string, string> variable in overrides)
        {
            if (string.IsNullOrEmpty(variable.Value))
            {
                result.Remove(variable.Key);
            }
            else
            {
                result[variable.Key] = variable.Value;
            }
        }

        return result;
    }

    internal static Dictionary<string, string> ReadCurrent()
    {
        Dictionary<string, string> result = new(KeyComparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }

    internal static StringComparer KeyComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
