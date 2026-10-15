using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Acp;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.AcpExtension;

public sealed partial class AcpToolViewModel : ReactiveObject, IDisposable
{
    private readonly IAcpService? _service;
    private readonly IAcpProfileStore? _profileStore;
    private readonly ISecretStore? _secretStore;
    private readonly IAcpOAuthDeviceFlowService? _oauthService;
    private readonly Func<string?>? _workspacePathProvider;
    private bool _disposed;
    private readonly CompositeDisposable _disposables = new();
    private readonly SerialDisposable _profileSubscription = new();
    private readonly Subject<Unit> _saveRequests = new();
    private readonly Dictionary<string, int> _toolCallIndexes = new(StringComparer.Ordinal);
    // Terminal id to tool call id for terminals that have not exited; only touched on the main thread.
    private readonly Dictionary<string, string> _openTerminals = new(StringComparer.Ordinal);
    private readonly SerialDisposable _terminalPolling = new();
    private bool _isPollingTerminals;
    private CancellationTokenSource? _authCts;

    public ObservableCollection<AcpSessionEntry> Sessions { get; } = new();

    public ObservableCollection<AcpActivityEntry> Activity { get; } = new();

    public ObservableCollection<AcpTranscriptEntry> Transcript { get; } = new();

    public ObservableCollection<AcpProfileViewModel> Profiles { get; } = new();

    public ObservableCollection<AcpAuthenticationMethod> AuthenticationMethods { get; } = new();

    [Reactive]
    public partial AcpAuthenticationMethod? SelectedAuthenticationMethod { get; set; }

    [Reactive]
    public partial AcpProfileViewModel? SelectedProfile { get; set; }

    [Reactive]
    public partial bool IsConnected { get; private set; }

    [Reactive]
    public partial string StatusText { get; private set; } = UiStrings.Disconnected;

    [Reactive]
    public partial string? ActiveSessionId { get; private set; }

    [Reactive]
    public partial string PromptText { get; set; } = string.Empty;

    [Reactive]
    public partial bool IsPromptRunning { get; private set; }

    [Reactive]
    public partial string AgentName { get; private set; } = string.Empty;

    [Reactive]
    public partial string? ApiKeyInput { get; set; }

    [Reactive]
    public partial string ApiKeyStatus { get; private set; } = UiStrings.StateUnknown;

    [Reactive]
    public partial string WebAuthStatus { get; private set; } = UiStrings.NotSignedIn;

    [Reactive]
    public partial string? WebAuthUserCode { get; private set; }

    [Reactive]
    public partial string? WebAuthVerificationUri { get; private set; }

    [Reactive]
    public partial string? WebAuthVerificationUriComplete { get; private set; }

    [Reactive]
    public partial bool IsWebAuthBusy { get; private set; }

    public Interaction<string, RxVoid> CopyToClipboardInteraction { get; } = new();

    public Interaction<string, RxVoid> OpenUrlInteraction { get; } = new();

    public Interaction<AcpPermissionRequest, AcpPermissionOutcome> PermissionInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> ConnectMockCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ConnectProfileCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DisconnectCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> NewSessionCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelPromptCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SendPromptCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> AuthenticateCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SaveApiKeyCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ClearApiKeyCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> StartWebAuthCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ClearWebAuthCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CopyUserCodeCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> OpenVerificationUrlCommand { get; }

    public AcpToolViewModel(
        IAcpService? service = null,
        IAcpProfileStore? profileStore = null,
        ISecretStore? secretStore = null,
        IAcpOAuthDeviceFlowService? oauthService = null,
        Func<string?>? workspacePathProvider = null)
    {
        _service = service;
        _profileStore = profileStore;
        _secretStore = secretStore;
        _oauthService = oauthService;
        _workspacePathProvider = workspacePathProvider;

        IObservable<bool> canConnect = this.WhenAnyValue(x => x.IsConnected).Select(value => !value);
        IObservable<bool> canDisconnect = this.WhenAnyValue(x => x.IsConnected);

        if (_service is null)
        {
            ConnectMockCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            ConnectProfileCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            DisconnectCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            NewSessionCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            CancelPromptCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            SendPromptCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            AuthenticateCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            SaveApiKeyCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            ClearApiKeyCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            StartWebAuthCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            ClearWebAuthCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            CopyUserCodeCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            OpenVerificationUrlCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));
            LoadMockData();
            return;
        }

        ConnectMockCommand = ReactiveCommand.CreateFromTask(ConnectMockAsync, canConnect);
        ConnectProfileCommand = ReactiveCommand.CreateFromTask(ConnectProfileAsync, canConnect);
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync, canDisconnect);
        IObservable<bool> canStartSession = this.WhenAnyValue(x => x.IsConnected, x => x.IsPromptRunning,
            (connected, running) => connected && !running);
        NewSessionCommand = ReactiveCommand.CreateFromTask(CreateSessionAsync, canStartSession);

        IObservable<bool> canSend = this.WhenAnyValue(x => x.IsConnected, x => x.ActiveSessionId,
            x => x.PromptText, x => x.IsPromptRunning,
            (connected, sessionId, text, running) => connected && !string.IsNullOrWhiteSpace(sessionId)
                && !string.IsNullOrWhiteSpace(text) && !running);
        SendPromptCommand = ReactiveCommand.CreateFromTask(SendPromptAsync, canSend);
        AuthenticateCommand = ReactiveCommand.CreateFromTask(AuthenticateAsync,
            this.WhenAnyValue(x => x.IsConnected, x => x.SelectedAuthenticationMethod, x => x.IsPromptRunning,
                (connected, method, running) => connected && method is not null && !running));

        IObservable<bool> canCancel = this.WhenAnyValue(
            x => x.IsConnected,
            x => x.IsPromptRunning,
            (connected, running) => connected && running);
        CancelPromptCommand = ReactiveCommand.CreateFromTask(CancelPromptAsync, canCancel);

        IObservable<bool> canSaveKey = this.WhenAnyValue(
            x => x.SelectedProfile,
            x => x.ApiKeyInput,
            (profile, input) => profile is not null && !string.IsNullOrWhiteSpace(input));
        SaveApiKeyCommand = ReactiveCommand.CreateFromTask(SaveApiKeyAsync, canSaveKey);

        IObservable<bool> canClearKey = this.WhenAnyValue(x => x.SelectedProfile)
            .Select(profile => profile is not null);
        ClearApiKeyCommand = ReactiveCommand.CreateFromTask(ClearApiKeyAsync, canClearKey);

        IObservable<bool> canStartAuth = this.WhenAnyValue(
            x => x.SelectedProfile,
            x => x.IsWebAuthBusy,
            (profile, busy) => profile is not null && !busy);
        StartWebAuthCommand = ReactiveCommand.CreateFromTask(StartWebAuthAsync, canStartAuth);

        IObservable<bool> canClearAuth = this.WhenAnyValue(x => x.SelectedProfile)
            .Select(profile => profile is not null);
        ClearWebAuthCommand = ReactiveCommand.CreateFromTask(ClearWebAuthAsync, canClearAuth);

        IObservable<bool> canCopyCode = this.WhenAnyValue(x => x.WebAuthUserCode)
            .Select(code => !string.IsNullOrWhiteSpace(code));
        CopyUserCodeCommand = ReactiveCommand.CreateFromTask(CopyUserCodeAsync, canCopyCode);

        IObservable<bool> canOpenLink = this.WhenAnyValue(
            x => x.WebAuthVerificationUri,
            x => x.WebAuthVerificationUriComplete,
            (uri, uriComplete) => !string.IsNullOrWhiteSpace(uriComplete) || !string.IsNullOrWhiteSpace(uri));
        OpenVerificationUrlCommand = ReactiveCommand.CreateFromTask(OpenVerificationUrlAsync, canOpenLink);

        _service.NotificationReceived += OnNotificationReceived;
        _service.ConnectionChanged += OnConnectionChanged;
        _service.StderrReceived += OnStderrReceived;
        _disposables.Add(_terminalPolling);
        UpdateConnectionState();

        _disposables.Add(_saveRequests
            .Throttle(TimeSpan.FromMilliseconds(400))
            .WitnessOn(RxSchedulers.TaskpoolScheduler)
            .SelectMany(_ => Observable.FromAsync(SaveProfilesAsync))
            .Subscribe());

        _disposables.Add(this.WhenAnyValue(x => x.SelectedProfile)
            .Subscribe(SubscribeToProfile));

        _ = LoadProfilesAsync();
    }

    private void SubscribeToProfile(AcpProfileViewModel? profile)
    {
        _profileSubscription.Disposable = Disposable.Empty;
        if (profile is null)
        {
            ApiKeyStatus = UiStrings.StateUnknown;
            WebAuthStatus = UiStrings.NotSignedIn;
            return;
        }

        _profileSubscription.Disposable = profile.Changed.Subscribe(_ => _saveRequests.OnNext(Unit.Default));
        _saveRequests.OnNext(Unit.Default);
        _ = RefreshApiKeyStatusAsync(profile);
        _ = RefreshWebAuthStatusAsync(profile);
    }

    private void LoadMockData()
    {
        IsConnected = true;
        StatusText = UiStrings.Connected;
        ActiveSessionId = "session-001";

        Sessions.Add(new AcpSessionEntry(
            "session-001",
            "Mock Agent",
            "Ready",
            "10:12:05"));
        Sessions.Add(new AcpSessionEntry(
            "session-000",
            "Mock Agent",
            "Completed",
            "09:58:41"));

        Activity.Add(new AcpActivityEntry("10:12:07", "Initialized ACP client.", "info"));
        Activity.Add(new AcpActivityEntry("10:12:12", "Created session session-001.", "info"));
        Activity.Add(new AcpActivityEntry("10:12:30", "Waiting for prompt.", "idle"));

        Transcript.Add(new AcpTranscriptMessageEntry("assistant", "Mock agent connected.", "10:12:07"));
        Transcript.Add(new AcpTranscriptToolEntry(
            "tool-001",
            "Read file",
            "completed",
            "read",
            "10:12:15",
            null,
            null,
            null,
            null,
            "exit: 0\n$ cat README.md\n...",
            false,
            0,
            null));
        Transcript.Add(new AcpTranscriptMessageEntry("assistant", "Ready when you are.", "10:12:30"));
    }

    private async Task ConnectMockAsync()
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            await _service.ConnectMockAgentAsync(CancellationToken.None);
            UpdateConnectionState();
            AddActivity(UiStrings.ConnectedMockAgent, "info");
            await InitializeSessionAsync();
        }
        catch (Exception ex)
        {
            AddActivity(UiStrings.ConnectFailedPrefix + ex.Message, "error");
        }
    }

    private async Task ConnectProfileAsync()
    {
        if (_service is null || SelectedProfile is null)
        {
            return;
        }

        try
        {
            AcpProfile profile = SelectedProfile.ToProfile();
            AcpAgentProcessOptions options = await BuildProcessOptionsAsync(profile);
            await _service.ConnectAsync(options, CancellationToken.None);
            UpdateConnectionState();
            AddActivity(UiStrings.ConnectedToPrefix + profile.Name + ".", "info");
            await InitializeSessionAsync();
        }
        catch (Exception ex)
        {
            AddActivity(UiStrings.ConnectFailedPrefix + ex.Message, "error");
        }
    }

    private async Task DisconnectAsync()
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            await _service.DisconnectAsync();
            UpdateConnectionState();
            AddActivity(UiStrings.DisconnectedMessage, "info");
        }
        catch (Exception ex)
        {
            AddActivity(UiStrings.DisconnectFailedPrefix + ex.Message, "error");
        }
    }

    private async Task AuthenticateAsync()
    {
        if (_service is null || SelectedAuthenticationMethod is null) return;
        try
        {
            await _service.SendRequestAsync("authenticate", new { methodId = SelectedAuthenticationMethod.Id }, CancellationToken.None);
            if (string.IsNullOrEmpty(ActiveSessionId)) await CreateSessionAsync();
        }
        catch (Exception error)
        {
            AddActivity(string.Format(UiStrings.AcpAuthenticationFailed, error.Message), "error");
        }
    }

    private async Task SendPromptAsync()
    {
        if (_service is null || string.IsNullOrWhiteSpace(ActiveSessionId) || string.IsNullOrWhiteSpace(PromptText))
            return;

        string text = PromptText;
        string sessionId = ActiveSessionId;
        IsPromptRunning = true;
        PromptText = string.Empty;
        AppendMessageChunk("user", text);
        try
        {
            await _service.PromptAsync(sessionId, new[] { new { type = "text", text } }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddActivity(string.Format(UiStrings.AcpPromptFailed, ex.Message), "error");
            if (string.IsNullOrEmpty(PromptText)) PromptText = text;
        }
        finally
        {
            IsPromptRunning = false;
            UpdateConnectionState();
        }
    }

    private async Task CancelPromptAsync()
    {
        if (_service is null || string.IsNullOrWhiteSpace(ActiveSessionId))
        {
            return;
        }

        try
        {
            await _service.CancelAsync(ActiveSessionId, CancellationToken.None).ConfigureAwait(false);
            AddActivity(UiStrings.CancelSessionPrefix + ActiveSessionId + ".", "info");
        }
        catch (Exception ex)
        {
            AddActivity(UiStrings.CancelFailedPrefix + ex.Message, "error");
        }
    }

    private void OnConnectionChanged() => RxSchedulers.MainThreadScheduler.Schedule(() =>
    {
        if (!_disposed) UpdateConnectionState();
    });

    private void UpdateConnectionState()
    {
        if (_service is null)
        {
            return;
        }

        IsConnected = _service.IsConnected;
        StatusText = IsConnected ? UiStrings.Connected : UiStrings.Disconnected;
        if (!IsConnected && _openTerminals.Count > 0)
        {
            _openTerminals.Clear();
            UpdateTerminalPolling();
        }
        ActiveSessionId = _service.ActiveSessionId;

        if (!string.IsNullOrWhiteSpace(ActiveSessionId))
        {
            AddOrUpdateSession(new AcpSessionEntry(
                ActiveSessionId,
                AgentName,
                UiStrings.StateActive,
                DateTime.Now.ToString("HH:mm:ss")));
        }
    }

    private async Task InitializeSessionAsync()
    {
        if (_service is null)
        {
            return;
        }

        object initializePayload = new
        {
            protocolVersion = 1,
            clientInfo = new
            {
                name = "XamlVisualEditor",
                version = GetClientVersion()
            },
            clientCapabilities = new
            {
                fs = new { readTextFile = true, writeTextFile = true },
                terminal = true
            }
        };

        JsonElement initializeResult = await _service.InitializeAsync(initializePayload, CancellationToken.None);
        ApplyAgentInfo(initializeResult);
        AuthenticationMethods.Clear();
        if (initializeResult.TryGetProperty("authMethods", out JsonElement methods) && methods.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement method in methods.EnumerateArray())
            {
                string? type = TryGetString(method, "type");
                string? id = TryGetString(method, "id");
                if (id is not null && (type is null or "agent"))
                    AuthenticationMethods.Add(new AcpAuthenticationMethod(id, TryGetString(method, "name") ?? id));
            }
        }
        SelectedAuthenticationMethod = AuthenticationMethods.FirstOrDefault();

        string cwd = ResolveWorkspaceDirectory();
        object sessionPayload = new
        {
            cwd,
            mcpServers = Array.Empty<object>()
        };

        string sessionId = await _service.CreateSessionAsync(sessionPayload, CancellationToken.None);
        _toolCallIndexes.Clear();
        UpdateConnectionState();
        AddActivity(UiStrings.CreatedSessionPrefix + sessionId + ".", "info");
    }

    private async Task CreateSessionAsync()
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            string cwd = ResolveWorkspaceDirectory();
            object sessionPayload = new
            {
                cwd,
                mcpServers = Array.Empty<object>()
            };

            string sessionId = await _service.CreateSessionAsync(sessionPayload, CancellationToken.None);
            _toolCallIndexes.Clear();
            UpdateConnectionState();
            AddActivity(UiStrings.CreatedSessionPrefix + sessionId + ".", "info");
        }
        catch (Exception ex)
        {
            AddActivity(UiStrings.CreateSessionFailedPrefix + ex.Message, "error");
        }
    }

    private async Task<AcpAgentProcessOptions> BuildProcessOptionsAsync(AcpProfile profile)
    {
        AcpAgentProcessOptions options = new()
        {
            FileName = profile.Command,
            Arguments = BuildArguments(profile.Arguments),
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                ? ResolveWorkspaceDirectory()
                : profile.WorkingDirectory
        };

        foreach (KeyValuePair<string, string> pair in profile.Environment)
        {
            options.EnvironmentVariables[pair.Key] = pair.Value;
        }

        if (!string.IsNullOrWhiteSpace(profile.Model) && !string.IsNullOrWhiteSpace(profile.ModelEnvVar))
        {
            options.EnvironmentVariables[profile.ModelEnvVar] = profile.Model;
        }

        string? oauthToken = await GetOAuthAccessTokenAsync(profile).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(oauthToken) && !string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar))
        {
            options.EnvironmentVariables[profile.ApiKeyEnvVar] = oauthToken;
            return options;
        }

        if (profile.UseKeychain && !string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar) && _secretStore is not null)
        {
            string key = BuildApiKeyKey(profile.Id);
            string? secret = await _secretStore.GetSecretAsync(key, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(UiStrings.ApiKeyNotStored);
            }

            options.EnvironmentVariables[profile.ApiKeyEnvVar] = secret;
        }

        return options;
    }

    private static string BuildArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return string.Empty;
        }

        List<string> parts = new();
        foreach (string arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (arg.IndexOf(' ') >= 0)
            {
                string escaped = arg.Replace("\"", "\\\"");
                parts.Add("\"" + escaped + "\"");
            }
            else
            {
                parts.Add(arg);
            }
        }

        return string.Join(' ', parts);
    }

    private string ResolveWorkspaceDirectory()
    {
        string? workspacePath = _workspacePathProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            if (Directory.Exists(workspacePath))
            {
                return workspacePath;
            }

            string? directory = Path.GetDirectoryName(workspacePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return Environment.CurrentDirectory;
    }

    private async Task LoadProfilesAsync()
    {
        if (_profileStore is null)
        {
            return;
        }

        IReadOnlyList<AcpProfile> profiles = await _profileStore.LoadAsync(CancellationToken.None)
            .ConfigureAwait(false);
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            Profiles.Clear();
            foreach (AcpProfile profile in profiles)
            {
                Profiles.Add(new AcpProfileViewModel(profile));
            }

            SelectedProfile = Profiles.Count > 0 ? Profiles[0] : null;
            return Disposable.Empty;
        });
    }

    private async Task SaveProfilesAsync()
    {
        if (_profileStore is null)
        {
            return;
        }

        List<AcpProfile> snapshot = new();
        foreach (AcpProfileViewModel profile in Profiles)
        {
            snapshot.Add(profile.ToProfile());
        }

        await _profileStore.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SaveApiKeyAsync()
    {
        if (_secretStore is null || SelectedProfile is null || string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            return;
        }

        string key = BuildApiKeyKey(SelectedProfile.Id);
        await _secretStore.SetSecretAsync(key, ApiKeyInput, CancellationToken.None).ConfigureAwait(false);
        ApiKeyInput = string.Empty;
        await RefreshApiKeyStatusAsync(SelectedProfile).ConfigureAwait(false);
        AddActivity(UiStrings.ApiKeyStored, "info");
    }

    private async Task ClearApiKeyAsync()
    {
        if (_secretStore is null || SelectedProfile is null)
        {
            return;
        }

        string key = BuildApiKeyKey(SelectedProfile.Id);
        await _secretStore.RemoveSecretAsync(key, CancellationToken.None).ConfigureAwait(false);
        await RefreshApiKeyStatusAsync(SelectedProfile).ConfigureAwait(false);
        AddActivity(UiStrings.ApiKeyRemoved, "info");
    }

    private async Task RefreshApiKeyStatusAsync(AcpProfileViewModel profile)
    {
        if (_secretStore is null)
        {
            ApiKeyStatus = UiStrings.StateUnavailable;
            return;
        }

        OAuthTokenInfo? oauthInfo = await ReadOAuthTokenAsync(profile.Id).ConfigureAwait(false);
        if (oauthInfo is not null && !string.IsNullOrWhiteSpace(oauthInfo.AccessToken))
        {
            ApiKeyStatus = UiStrings.StoredOAuth;
            return;
        }

        string key = BuildApiKeyKey(profile.Id);
        string? secret = await _secretStore.GetSecretAsync(key, CancellationToken.None).ConfigureAwait(false);
        ApiKeyStatus = string.IsNullOrWhiteSpace(secret) ? UiStrings.NotSet : UiStrings.StateStored;
    }

    private async Task RefreshWebAuthStatusAsync(AcpProfileViewModel profile)
    {
        if (_secretStore is null)
        {
            WebAuthStatus = UiStrings.StateUnavailable;
            return;
        }

        OAuthTokenInfo? oauthInfo = await ReadOAuthTokenAsync(profile.Id).ConfigureAwait(false);
        if (oauthInfo is null || string.IsNullOrWhiteSpace(oauthInfo.AccessToken))
        {
            WebAuthStatus = UiStrings.NotSignedIn;
            return;
        }

        if (oauthInfo.ExpiresAt is not null && oauthInfo.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            WebAuthStatus = UiStrings.StateExpired;
            return;
        }

        WebAuthStatus = UiStrings.SignedIn;
    }

    private static string BuildApiKeyKey(string profileId)
    {
        return "acp.profile." + profileId + ".apiKey";
    }

    private static string BuildOAuthKey(string profileId)
    {
        return "acp.profile." + profileId + ".oauth";
    }

    private async Task StartWebAuthAsync()
    {
        if (_oauthService is null || SelectedProfile is null)
        {
            return;
        }

        string? clientId = SelectedProfile.OAuthClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            AddActivity(UiStrings.OAuthClientIdRequired, "error");
            WebAuthStatus = UiStrings.MissingClientId;
            return;
        }

        string? deviceCodeUrl = SelectedProfile.OAuthDeviceCodeUrl;
        string? tokenUrl = SelectedProfile.OAuthTokenUrl;
        if (!Uri.TryCreate(deviceCodeUrl, UriKind.Absolute, out _)
            || !Uri.TryCreate(tokenUrl, UriKind.Absolute, out _))
        {
            WebAuthStatus = UiStrings.OAuthEndpointsRequired;
            return;
        }
        string scope = string.IsNullOrWhiteSpace(SelectedProfile.OAuthScopes)
            ? "openid profile email offline_access"
            : SelectedProfile.OAuthScopes;

        _authCts?.Cancel();
        _authCts = new CancellationTokenSource();
        IsWebAuthBusy = true;
        WebAuthStatus = UiStrings.RequestingDeviceCode;

        try
        {
            AcpDeviceCodeResponse deviceCode = await _oauthService.StartDeviceFlowAsync(
                    clientId,
                    scope,
                    deviceCodeUrl,
                    _authCts.Token)
                .ConfigureAwait(false);

            WebAuthUserCode = deviceCode.UserCode;
            WebAuthVerificationUri = deviceCode.VerificationUri;
            WebAuthVerificationUriComplete = deviceCode.VerificationUriComplete;
            WebAuthStatus = UiStrings.WaitingAuthorization;

            string url = deviceCode.VerificationUriComplete ?? deviceCode.VerificationUri;
            await TryOpenUrlAsync(url).ConfigureAwait(false);

            AcpTokenResponse token = await _oauthService.CompleteDeviceFlowAsync(
                    clientId,
                    deviceCode.DeviceCode,
                    deviceCode.Interval,
                    tokenUrl,
                    _authCts.Token)
                .ConfigureAwait(false);

            await StoreOAuthTokenAsync(SelectedProfile.Id, token).ConfigureAwait(false);
            WebAuthStatus = UiStrings.SignedIn;
            await RefreshApiKeyStatusAsync(SelectedProfile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WebAuthStatus = UiStrings.SignInFailed;
            AddActivity(UiStrings.OAuthFailedPrefix + ex.Message, "error");
        }
        finally
        {
            IsWebAuthBusy = false;
        }
    }

    private async Task ClearWebAuthAsync()
    {
        if (_secretStore is null || SelectedProfile is null)
        {
            return;
        }

        _authCts?.Cancel();

        string oauthKey = BuildOAuthKey(SelectedProfile.Id);
        await _secretStore.RemoveSecretAsync(oauthKey, CancellationToken.None).ConfigureAwait(false);
        string apiKey = BuildApiKeyKey(SelectedProfile.Id);
        await _secretStore.RemoveSecretAsync(apiKey, CancellationToken.None).ConfigureAwait(false);

        WebAuthUserCode = null;
        WebAuthVerificationUri = null;
        WebAuthVerificationUriComplete = null;
        WebAuthStatus = UiStrings.NotSignedIn;
        await RefreshApiKeyStatusAsync(SelectedProfile).ConfigureAwait(false);
        AddActivity(UiStrings.OAuthSignOutComplete, "info");
    }

    private async Task CopyUserCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(WebAuthUserCode))
        {
            return;
        }

        try
        {
            await CopyToClipboardInteraction.Handle(WebAuthUserCode).ConfigureAwait(false);
            AddActivity(UiStrings.VerificationCodeCopied, "info");
        }
        catch
        {
        }
    }

    private async Task OpenVerificationUrlAsync()
    {
        string? url = WebAuthVerificationUriComplete ?? WebAuthVerificationUri;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        await TryOpenUrlAsync(url).ConfigureAwait(false);
    }

    private async Task TryOpenUrlAsync(string url)
    {
        try
        {
            await OpenUrlInteraction.Handle(url).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task StoreOAuthTokenAsync(string profileId, AcpTokenResponse token)
    {
        if (_secretStore is null)
        {
            return;
        }

        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn));
        OAuthTokenInfo info = new(token.AccessToken, token.RefreshToken, expiresAt);
        string payload = JsonSerializer.Serialize(info);
        string oauthKey = BuildOAuthKey(profileId);
        await _secretStore.SetSecretAsync(oauthKey, payload, CancellationToken.None).ConfigureAwait(false);

        string apiKey = BuildApiKeyKey(profileId);
        await _secretStore.SetSecretAsync(apiKey, token.AccessToken, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<OAuthTokenInfo?> ReadOAuthTokenAsync(string profileId)
    {
        if (_secretStore is null)
        {
            return null;
        }

        string oauthKey = BuildOAuthKey(profileId);
        string? payload = await _secretStore.GetSecretAsync(oauthKey, CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            OAuthTokenInfo? info = JsonSerializer.Deserialize<OAuthTokenInfo>(payload);
            return info;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetOAuthAccessTokenAsync(AcpProfile profile)
    {
        if (_oauthService is null || string.IsNullOrWhiteSpace(profile.OAuthClientId))
        {
            return null;
        }

        OAuthTokenInfo? info = await ReadOAuthTokenAsync(profile.Id).ConfigureAwait(false);
        if (info is null || string.IsNullOrWhiteSpace(info.AccessToken))
        {
            return null;
        }

        if (info.ExpiresAt is null || info.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return info.AccessToken;
        }

        bool hasTokenEndpoint = Uri.TryCreate(profile.OAuthTokenUrl, UriKind.Absolute, out _);
        if (!string.IsNullOrWhiteSpace(info.RefreshToken) && hasTokenEndpoint)
        {
            try
            {
                AcpTokenResponse refreshed = await _oauthService.RefreshTokenAsync(
                        profile.OAuthClientId,
                        info.RefreshToken,
                        profile.OAuthTokenUrl!,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await StoreOAuthTokenAsync(profile.Id, refreshed).ConfigureAwait(false);
                return refreshed.AccessToken;
            }
            catch (Exception ex)
            {
                AddActivity(UiStrings.OAuthFailedPrefix + ex.Message, "error");
            }
        }

        if (info.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return info.AccessToken;
        }

        // The stored API key holds the same expired token, so the connection must not fall back to it.
        WebAuthStatus = hasTokenEndpoint ? UiStrings.StateExpired : UiStrings.OAuthEndpointsRequired;
        throw new InvalidOperationException(UiStrings.OAuthTokenExpired);
    }


    private void ApplyAgentInfo(JsonElement initializeResult)
    {
        if (!initializeResult.TryGetProperty("agentInfo", out JsonElement agentInfo)
            || agentInfo.ValueKind != JsonValueKind.Object)
        {
            AgentName = UiStrings.AcpAgent;
            return;
        }

        string? title = TryGetString(agentInfo, "title");
        string? name = TryGetString(agentInfo, "name");
        AgentName = !string.IsNullOrWhiteSpace(title) ? title : name ?? UiStrings.AcpAgent;
    }

    private static string GetClientVersion()
    {
        Version? version = typeof(AcpToolViewModel).Assembly.GetName().Version;
        return version?.ToString() ?? "0.0.0";
    }

    private void OnNotificationReceived(string method, System.Text.Json.JsonElement? parameters)
    {
        if (_disposed)
        {
            return;
        }

        if (string.Equals(method, "session/update", StringComparison.OrdinalIgnoreCase))
        {
            HandleSessionUpdate(parameters);
            return;
        }

        AddActivity(method, "update");
    }

    private void HandleSessionUpdate(JsonElement? parameters)
    {
        string? sessionId = TryGetString(parameters, "sessionId");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            AddOrUpdateSession(new AcpSessionEntry(
                sessionId,
                AgentName,
                UiStrings.StateActive,
                DateTime.Now.ToString("HH:mm:ss")));
        }

        JsonElement? updateElement = TryGetObject(parameters, "update");
        if (updateElement is null)
        {
            AddActivity("session/update", "update");
            return;
        }

        string? updateKind = TryGetString(updateElement, "sessionUpdate");
        JsonElement? updatePayload = updateElement;

        if (string.IsNullOrWhiteSpace(updateKind))
        {
            AddActivity("session/update", "update");
            return;
        }

        string summary = BuildUpdateSummary(updateKind, updatePayload);
        string message = string.IsNullOrWhiteSpace(summary)
            ? updateKind
            : updateKind + ": " + summary;
        AddActivity(message, "update");
        ApplyTranscriptUpdate(updateKind, updatePayload);
    }

    private void ApplyTranscriptUpdate(string updateKind, JsonElement? payload)
    {
        if (payload is null)
        {
            return;
        }

        switch (updateKind)
        {
            case "agent_message_chunk":
                AppendMessageChunk("assistant", ExtractContentText(payload.Value));
                break;
            case "user_message_chunk":
                AppendMessageChunk("user", ExtractContentText(payload.Value));
                break;
            case "agent_thought_chunk":
                AppendMessageChunk("thought", ExtractContentText(payload.Value));
                break;
            case "tool_call":
            case "tool_call_update":
                UpsertToolEntry(payload.Value);
                break;
            case "plan":
                AppendStatusEntry(UiStrings.PlanUpdate, SummarizePlan(payload.Value));
                break;
            case "current_mode_update":
                AppendStatusEntry(UiStrings.ModeLabel, SummarizeMode(payload.Value));
                break;
            case "config_option_update":
                AppendStatusEntry(UiStrings.ConfigLabel, SummarizeConfigOptions(payload.Value));
                break;
        }
    }

    private void AppendMessageChunk(string role, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            if (Transcript.Count > 0 && Transcript[^1] is AcpTranscriptMessageEntry last
                && string.Equals(last.Role, role, StringComparison.Ordinal))
            {
                Transcript[^1] = last with { Text = last.Text + text };
                return Disposable.Empty;
            }

            Transcript.Add(new AcpTranscriptMessageEntry(role, text, timestamp));
            return Disposable.Empty;
        });
    }

    private void UpsertToolEntry(JsonElement payload)
    {
        string? toolCallId = TryGetString(payload, "toolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        string? title = TryGetString(payload, "title");
        string? status = TryGetString(payload, "status");
        string? kind = TryGetString(payload, "kind");
        ToolContentSnapshot contentSnapshot = ExtractToolContent(payload);
        string timestamp = DateTime.Now.ToString("HH:mm:ss");

        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            if (_toolCallIndexes.TryGetValue(toolCallId, out int index)
                && index >= 0
                && index < Transcript.Count
                && Transcript[index] is AcpTranscriptToolEntry existing)
            {
                AcpTranscriptToolEntry updated = existing with
                {
                    Title = title ?? existing.Title,
                    Status = status ?? existing.Status,
                    ToolKind = kind ?? existing.ToolKind,
                    Timestamp = timestamp,
                    DiffPath = contentSnapshot.DiffPath ?? existing.DiffPath,
                    DiffOldText = contentSnapshot.DiffOldText ?? existing.DiffOldText,
                    DiffNewText = contentSnapshot.DiffNewText ?? existing.DiffNewText,
                    TerminalId = contentSnapshot.TerminalId ?? existing.TerminalId,
                    OutputText = contentSnapshot.OutputText ?? existing.OutputText
                };
                Transcript[index] = updated;
                TrackOpenTerminal(updated);
                return Disposable.Empty;
            }

            AcpTranscriptToolEntry added = new(
                toolCallId,
                title ?? UiStrings.ToolCall,
                status ?? "pending",
                kind ?? "other",
                timestamp,
                contentSnapshot.DiffPath,
                contentSnapshot.DiffOldText,
                contentSnapshot.DiffNewText,
                contentSnapshot.TerminalId,
                null,
                false,
                null,
                null,
                contentSnapshot.OutputText);
            Transcript.Add(added);
            _toolCallIndexes[toolCallId] = Transcript.Count - 1;
            TrackOpenTerminal(added);
            return Disposable.Empty;
        });

        if (!string.IsNullOrWhiteSpace(contentSnapshot.TerminalId))
        {
            RefreshTerminalOutput(toolCallId, contentSnapshot.TerminalId);
        }
    }

    private void RefreshTerminalOutput(string toolCallId, string terminalId)
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            JsonElement? snapshot = _service.GetTerminalOutput(terminalId);
            if (snapshot is null)
            {
                // The client no longer knows this terminal, so no further output can arrive.
                RxSchedulers.MainThreadScheduler.Schedule(() => ForgetOpenTerminal(terminalId));
                return;
            }

            JsonElement response = snapshot.Value;

            string? output = TryGetString(response, "output");
            bool truncated = response.TryGetProperty("truncated", out JsonElement truncElement)
                && truncElement.ValueKind == JsonValueKind.True;
            int? exitCode = null;
            string? signal = null;
            bool exited = response.TryGetProperty("exitStatus", out JsonElement exitStatus)
                && exitStatus.ValueKind == JsonValueKind.Object;

            if (exited)
            {
                if (exitStatus.TryGetProperty("exitCode", out JsonElement exitCodeElement)
                    && exitCodeElement.ValueKind == JsonValueKind.Number
                    && exitCodeElement.TryGetInt32(out int code))
                {
                    exitCode = code;
                }

                signal = TryGetString(exitStatus, "signal");
            }

            RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
            {
                if (exited) ForgetOpenTerminal(terminalId);
                if (!_toolCallIndexes.TryGetValue(toolCallId, out int index)
                    || index < 0
                    || index >= Transcript.Count
                    || Transcript[index] is not AcpTranscriptToolEntry existing)
                {
                    return Disposable.Empty;
                }

                Transcript[index] = existing with
                {
                    TerminalOutput = output ?? existing.TerminalOutput,
                    TerminalTruncated = truncated,
                    TerminalExitCode = exitCode ?? existing.TerminalExitCode,
                    TerminalSignal = signal ?? existing.TerminalSignal
                };
                return Disposable.Empty;
            });
        }
        catch (Exception ex)
        {
            AddActivity(UiStrings.TerminalOutputFailedPrefix + ex.Message, "error");
        }
    }

    private void TrackOpenTerminal(AcpTranscriptToolEntry entry)
    {
        if (entry.TerminalId is null || entry.TerminalExitCode is not null || entry.TerminalSignal is not null) return;
        _openTerminals[entry.TerminalId] = entry.ToolCallId;
        UpdateTerminalPolling();
    }

    private void ForgetOpenTerminal(string terminalId)
    {
        if (_openTerminals.Remove(terminalId)) UpdateTerminalPolling();
    }

    private void UpdateTerminalPolling()
    {
        bool poll = _openTerminals.Count > 0 && !_disposed;
        if (poll == _isPollingTerminals) return;
        _isPollingTerminals = poll;
        _terminalPolling.Disposable = poll
            ? Observable.Interval(TimeSpan.FromSeconds(1))
                .WitnessOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => PollOpenTerminals())
            : Disposable.Empty;
    }

    private void PollOpenTerminals()
    {
        if (!IsConnected) return;
        foreach (KeyValuePair<string, string> terminal in _openTerminals.ToArray())
            RefreshTerminalOutput(terminal.Value, terminal.Key);
    }

    private readonly record struct ToolContentSnapshot(
        string? DiffPath,
        string? DiffOldText,
        string? DiffNewText,
        string? TerminalId,
        string? OutputText);

    private static ToolContentSnapshot ExtractToolContent(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        string? diffPath = null;
        string? diffOld = null;
        string? diffNew = null;
        string? terminalId = null;
        List<string> texts = new();

        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = TryGetString(item, "type");
            if (diffPath is null && type == "diff")
            {
                diffPath = TryGetString(item, "path");
                diffOld = TruncateText(TryGetString(item, "oldText"), 2000);
                diffNew = TruncateText(TryGetString(item, "newText"), 2000);
            }

            if (terminalId is null && type == "terminal")
            {
                terminalId = TryGetString(item, "terminalId");
            }
            if (type == "content" && ExtractContentText(item) is { } text)
            {
                texts.Add(text);
            }
        }

        return new ToolContentSnapshot(diffPath, diffOld, diffNew, terminalId,
            texts.Count > 0 ? string.Join(Environment.NewLine, texts) : null);
    }

    private static string? TruncateText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, maxLength) + "...";
    }

    private void AppendStatusEntry(string title, string detail)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            Transcript.Add(new AcpTranscriptStatusEntry(title, detail, timestamp));
            return Disposable.Empty;
        });
    }

    private static string SummarizePlan(JsonElement payload)
    {
        if (payload.TryGetProperty("entries", out JsonElement entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            int count = entries.GetArrayLength();
            return UiStrings.EntriesPrefix + count;
        }

        return UiStrings.StateUpdated;
    }

    private static string SummarizeMode(JsonElement payload)
    {
        string? modeId = TryGetString(payload, "currentModeId");
        return string.IsNullOrWhiteSpace(modeId) ? UiStrings.StateChanged : UiStrings.CurrentPrefix + modeId;
    }

    private static string SummarizeConfigOptions(JsonElement payload)
    {
        if (payload.TryGetProperty("configOptions", out JsonElement options)
            && options.ValueKind == JsonValueKind.Array)
        {
            int count = options.GetArrayLength();
            return UiStrings.OptionsPrefix + count;
        }

        return UiStrings.StateUpdated;
    }

    private static string BuildUpdateSummary(string updateKind, JsonElement? payload)
    {
        if (payload is null)
        {
            return string.Empty;
        }

        switch (updateKind)
        {
            case "agent_message_chunk":
            case "user_message_chunk":
            case "agent_thought_chunk":
                return ExtractContentText(payload.Value) ?? string.Empty;
            case "tool_call":
            case "tool_call_update":
                return TryGetString(payload, "title")
                    ?? TryGetString(payload, "status")
                    ?? TryGetString(payload, "kind")
                    ?? TryGetString(payload, "toolCallId")
                    ?? string.Empty;
            case "plan":
                if (payload.Value.TryGetProperty("entries", out JsonElement entries)
                    && entries.ValueKind == JsonValueKind.Array)
                {
                    return "entries=" + entries.GetArrayLength();
                }

                return string.Empty;
            case "available_commands_update":
                if (payload.Value.TryGetProperty("availableCommands", out JsonElement commands)
                    && commands.ValueKind == JsonValueKind.Array)
                {
                    return "commands=" + commands.GetArrayLength();
                }

                return string.Empty;
            case "current_mode_update":
                return TryGetString(payload, "currentModeId") ?? string.Empty;
            case "config_option_update":
                if (payload.Value.TryGetProperty("configOptions", out JsonElement options)
                    && options.ValueKind == JsonValueKind.Array)
                {
                    return "options=" + options.GetArrayLength();
                }

                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private static string? ExtractContentText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("text", out JsonElement textBlock)
                && textBlock.ValueKind == JsonValueKind.Object
                && textBlock.TryGetProperty("text", out JsonElement textValue)
                && textValue.ValueKind == JsonValueKind.String)
            {
                return textValue.GetString();
            }

            if (content.TryGetProperty("text", out JsonElement textScalar)
                && textScalar.ValueKind == JsonValueKind.String)
            {
                return textScalar.GetString();
            }
        }

        return null;
    }

    private void OnStderrReceived(string message)
    {
        if (_disposed)
        {
            return;
        }

        AddActivity(message, "stderr");
    }

    private void AddActivity(string message, string kind)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            Activity.Insert(0, new AcpActivityEntry(timestamp, message, kind));
            return Disposable.Empty;
        });
    }

    private void AddOrUpdateSession(AcpSessionEntry entry)
    {
        RxSchedulers.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            for (int i = 0; i < Sessions.Count; i++)
            {
                if (string.Equals(Sessions[i].SessionId, entry.SessionId, StringComparison.Ordinal))
                {
                    Sessions[i] = entry;
                    return Disposable.Empty;
                }
            }

            Sessions.Insert(0, entry);
            return Disposable.Empty;
        });
    }

    private static string? TryGetString(System.Text.Json.JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (parameters.Value.TryGetProperty(name, out System.Text.Json.JsonElement element)
            && element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }

    private static JsonElement? TryGetObject(System.Text.Json.JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (parameters.Value.TryGetProperty(name, out System.Text.Json.JsonElement element)
            && element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            return element;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveRequests.OnCompleted();
        _saveRequests.Dispose();
        _authCts?.Cancel();
        _authCts?.Dispose();
        _profileSubscription.Dispose();
        _disposables.Dispose();
        if (_service is not null)
        {
            _service.NotificationReceived -= OnNotificationReceived;
            _service.ConnectionChanged -= OnConnectionChanged;
            _service.StderrReceived -= OnStderrReceived;
        }
    }
}

public sealed partial record AcpSessionEntry(string SessionId, string Agent, string Status, string LastUpdated);

public sealed record AcpAuthenticationMethod(string Id, string Name);

public sealed partial record AcpActivityEntry(string Timestamp, string Message, string Kind);
