using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting.IdeBridge;
using XamlVisualEditor.Extensions.Hosting.Mcp;
using XamlVisualEditor.Core.Localization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

[CollectionDefinition(nameof(UiLocalizationCollection), DisableParallelization = true)]
public sealed class UiLocalizationCollection;

[Collection(nameof(UiLocalizationCollection))]
public sealed class IdeBridgePermissionServiceTests
{
    [Theory]
    [InlineData("en", 0)]
    [InlineData("en", 1)]
    [InlineData("en", 2)]
    [InlineData("en", -1)]
    [InlineData("en", 3)]
    [InlineData("de", 0)]
    [InlineData("de", 1)]
    [InlineData("de", 2)]
    [InlineData("de", -1)]
    [InlineData("de", 3)]
    public async Task LocalizedConsentPreservesTheSelectedAccessLevel(string language, int selection)
    {
        CultureInfo? previous = UiStrings.Culture;
        UiStrings.Culture = CultureInfo.GetCultureInfo(language);
        try
        {
            FakeWindow window = new()
            {
                Pick = items => selection < 0 ? null : selection < items.Count ? items[selection] : new QuickPickItem("unexpected", null, null)
            };
            IdeBridgePermissionService ide = new(new FakeSettings(), window);
            McpPermissionService mcp = new(new FakeSettings(), window);
            IdeBridgeWorkspacePermissionState? ideState = await ide.AuthorizeAsync("localized-test", null, CancellationToken.None);
            McpWorkspacePermissionState? mcpState = await mcp.AuthorizeAsync("localized-test", null, CancellationToken.None);
            if (selection is 0 or 1)
            {
                Assert.NotNull(ideState);
                Assert.NotNull(mcpState);
                Assert.Equal(selection == 1, ideState.Capabilities.Write);
                Assert.Equal(selection == 1, ideState.Capabilities.Terminal);
                Assert.Equal(selection == 1 ? McpAccessLevel.Full : McpAccessLevel.ReadOnly, mcpState.AccessLevel);
            }
            else
            {
                Assert.Null(ideState);
                Assert.Null(mcpState);
            }
        }
        finally { UiStrings.Culture = previous; }
    }

    [Fact]
    public async Task AuthorizeAsync_AllowsFullAccessWhenApproved()
    {
        FakeSettings settings = new();
        FakeWindow window = new()
        {
            NextPick = new QuickPickItem(UiStrings.AllowFullAccess, "", null)
        };

        IdeBridgePermissionService service = new(settings, window);
        IdeBridgeWorkspacePermissionState? state = await service.AuthorizeAsync("ws1", null, CancellationToken.None);

        Assert.NotNull(state);
        Assert.True(state!.Capabilities.Write);
        Assert.True(state.Capabilities.Terminal);
    }

    [Fact]
    public async Task AuthorizeAsync_DeniesWhenTokenDoesNotMatch()
    {
        FakeSettings settings = new();
        FakeWindow window = new()
        {
            NextPick = new QuickPickItem(UiStrings.AllowReadOnly, "", null)
        };

        IdeBridgePermissionService service = new(settings, window);
        IdeBridgeWorkspacePermissionState? state = await service.AuthorizeAsync("ws2", null, CancellationToken.None);
        Assert.NotNull(state);

        IdeBridgeWorkspacePermissionState? denied = await service.AuthorizeAsync("ws2", "wrong-token", CancellationToken.None);
        Assert.Null(denied);
    }

    private sealed class FakeWindow : IWindow
    {
        public QuickPickItem? NextPick { get; set; }
        public Func<IReadOnlyList<QuickPickItem>, QuickPickItem?>? Pick { get; init; }

        public Task ShowInformationMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShowWarningMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShowErrorMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions options, CancellationToken cancellationToken)
            => Task.FromResult(Pick is null ? NextPick : Pick(items));

        public IOutputChannel CreateOutputChannel(string name) => new NullOutputChannel(name);

        public Task<IReadOnlyList<OutputChannelInfo>> GetOutputChannelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OutputChannelInfo>>(Array.Empty<OutputChannelInfo>());

#pragma warning disable CS0067
        public event EventHandler<OutputChannelEventArgs>? OutputChannelCreated;

        public event EventHandler<OutputChannelEventArgs>? OutputChannelRemoved;

        public event EventHandler<OutputChannelMessageEventArgs>? OutputChannelMessage;

        public event EventHandler<OutputChannelClearedEventArgs>? OutputChannelCleared;
#pragma warning restore CS0067

        public IStatusBarItem CreateStatusBarItem(StatusBarAlignment alignment, int priority) => new NullStatusBarItem();

        private sealed class NullOutputChannel : IOutputChannel
        {
            public NullOutputChannel(string name) => Name = name;

            public string Name { get; }

            public void Append(string value)
            {
            }

            public void AppendLine(string value)
            {
            }

            public void Show()
            {
            }

            public void Hide()
            {
            }

            public void Clear()
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class NullStatusBarItem : IStatusBarItem
        {
            public string Text { get; set; } = string.Empty;
            public string? Tooltip { get; set; }
            public string? CommandId { get; set; }

            public void Show()
            {
            }

            public void Hide()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeSettings : ISettings
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SettingsSectionSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<SettingsSectionChangedEventArgs>? SectionChanged;

        public T? Get<T>(string section, T? defaultValue = default)
        {
            if (_values.TryGetValue(section, out object? value) && value is T typed)
            {
                return typed;
            }

            return defaultValue;
        }

        public Task UpdateAsync(string section, object? value, SettingsTarget target, CancellationToken cancellationToken)
        {
            _values[section] = value;
            SectionChanged?.Invoke(this, new SettingsSectionChangedEventArgs(section, target, value));
            return Task.CompletedTask;
        }

        public IDisposable RegisterSchema(SettingsSectionSchema schema)
        {
            _schemas[schema.Section] = schema;
            return new Registration(() => _schemas.Remove(schema.Section));
        }

        public IReadOnlyList<SettingsSectionSchema> GetSchemas()
        {
            return _schemas.Values.ToList();
        }

        public bool TryGetSchema(string section, out SettingsSectionSchema schema)
        {
            return _schemas.TryGetValue(section, out schema!);
        }

        public IReadOnlyList<SettingsValidationIssue> Validate(string section, object? value)
        {
            if (!_schemas.TryGetValue(section, out SettingsSectionSchema? schema) || schema.Validator is null)
            {
                return Array.Empty<SettingsValidationIssue>();
            }

            return schema.Validator(value) ?? Array.Empty<SettingsValidationIssue>();
        }

        public IDisposable SubscribeSection<T>(string section, Action<SettingsSectionChangedEventArgs<T>> handler)
        {
            EventHandler<SettingsSectionChangedEventArgs> wrapped = (_, args) =>
            {
                if (!string.Equals(args.Section, section, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (args.Value is null)
                {
                    handler(new SettingsSectionChangedEventArgs<T>(args.Section, args.Target, default));
                    return;
                }

                if (args.Value is T typed)
                {
                    handler(new SettingsSectionChangedEventArgs<T>(args.Section, args.Target, typed));
                }
            };

            SectionChanged += wrapped;
            return new Registration(() => SectionChanged -= wrapped);
        }

        private sealed class Registration : IDisposable
        {
            private readonly Action _unsubscribe;
            private bool _disposed;

            public Registration(Action unsubscribe)
            {
                _unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _unsubscribe();
            }
        }
    }
}
