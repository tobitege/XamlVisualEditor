using System.Collections.Generic;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Acp;

public sealed class AcpProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Command { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = new();

    public string? WorkingDirectory { get; set; }

    public Dictionary<string, string> Environment { get; set; } = new();

    public string? Model { get; set; }

    public string? ModelEnvVar { get; set; }

    public string? ApiKeyEnvVar { get; set; }

    public string? OAuthClientId { get; set; }

    public string? OAuthScopes { get; set; }

    public string? OAuthDeviceCodeUrl { get; set; }

    public string? OAuthTokenUrl { get; set; }

    public bool UseKeychain { get; set; } = true;

    public bool IsBuiltIn { get; set; }

    public static AcpProfile CreateCodexProfile()
    {
        return new AcpProfile
        {
            Id = "codex",
            Name = "OpenAI Codex",
            Description = UiStrings.OpenAiAcpAgent,
            Command = "codex-acp",
            ApiKeyEnvVar = "OPENAI_API_KEY",
            UseKeychain = true,
            IsBuiltIn = true
        };
    }

    public static AcpProfile CreateClaudeProfile()
    {
        return new AcpProfile
        {
            Id = "claude",
            Name = "Anthropic Claude",
            Description = UiStrings.AnthropicAcpAgent,
            Command = "claude-agent-acp",
            ModelEnvVar = "ANTHROPIC_MODEL",
            ApiKeyEnvVar = "ANTHROPIC_API_KEY",
            UseKeychain = true,
            IsBuiltIn = true
        };
    }
}
