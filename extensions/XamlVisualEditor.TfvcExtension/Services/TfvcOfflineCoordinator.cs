using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.ViewModels;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>
/// Switches TFVC between online and offline work: offers offline work when the server is unreachable at startup,
/// returns to offline work when going online fails, and lets the user pend local changes once after going online.
/// </summary>
public sealed class TfvcOfflineCoordinator : IDisposable
{
    private readonly TfvcSession _session;
    private readonly TfvcOfflineReconciler _reconciler;
    private readonly IWindow _window;
    private readonly IDialogHost _dialogHost;
    private readonly IExtensionLogger _logger;
    private int _isReconciling;
    private bool _wasOffline;

    /// <summary>Creates the coordinator.</summary>
    public TfvcOfflineCoordinator(
        TfvcSession session,
        TfvcOfflineReconciler reconciler,
        IWindow window,
        IDialogHost dialogHost,
        IExtensionLogger logger)
    {
        _session = session;
        _reconciler = reconciler;
        _window = window;
        _dialogHost = dialogHost;
        _logger = logger;
        _session.StateChanged += OnSessionStateChanged;
    }

    /// <summary>
    /// Runs after the session initialization: offers offline work when the connection failed, and proposes the files
    /// made writable during an earlier offline session when the connection succeeded.
    /// </summary>
    public async Task HandleInitializedAsync(CancellationToken cancellationToken)
    {
        if (_session.Settings.IsConfigured && _session.ConnectionFailed)
        {
            string message = string.Format(
                CultureInfo.CurrentCulture,
                TfvcStrings.GoOfflinePromptMessageFormat,
                _session.Settings.CollectionUrl,
                _session.StatusMessage);
            bool confirmed = await _dialogHost.ShowDialogAsync<bool>(
                ConfirmationDialogRequest.DIALOG_ID,
                new ConfirmationDialogRequest(TfvcStrings.GoOfflinePromptTitle, message, TfvcStrings.GoOfflineConfirm),
                cancellationToken);
            if (confirmed)
            {
                await _session.GoOfflineAsync(cancellationToken);
            }

            return;
        }

        // XVE was closed while offline, so going online happens now.
        if (_session.IsConnected && _session.OfflineEdits.Count > 0)
        {
            await ReconcileAsync(cancellationToken);
        }
    }

    /// <summary>Switches to offline work.</summary>
    public Task GoOfflineAsync(CancellationToken cancellationToken) => _session.GoOfflineAsync(cancellationToken);

    /// <summary>Connects again; when the connection fails, offline work continues and the reason is shown.</summary>
    public async Task GoOnlineAsync(CancellationToken cancellationToken)
    {
        await _session.ConnectAsync(cancellationToken);
        if (_session.IsConnected)
        {
            return;
        }

        string reason = _session.StatusMessage ?? string.Empty;
        await _session.GoOfflineAsync(cancellationToken);
        await _window.ShowErrorMessageAsync(
            string.Format(CultureInfo.CurrentCulture, TfvcStrings.GoOnlineFailedFormat, reason),
            CancellationToken.None);
    }

    /// <summary>
    /// Lists the local changes without pending change once after going online and pends the ones the user selects.
    /// Afterwards the offline edit records are forgotten: a change the user did not take stays a local change that
    /// has to be pended by hand.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!_session.IsConnected || Interlocked.Exchange(ref _isReconciling, 1) == 1)
        {
            return;
        }

        try
        {
            IReadOnlyList<TfvcOfflineChange> changes = await _session.RunBusyAsync(() => _reconciler.DetectAsync(cancellationToken));
            try
            {
                if (changes.Count == 0)
                {
                    return;
                }

                IReadOnlyList<TfvcOfflineChange>? selected = await _dialogHost.ShowDialogAsync<IReadOnlyList<TfvcOfflineChange>>(
                    TfvcReconcileDialogViewModel.DIALOG_ID,
                    new TfvcReconcileDialogViewModel(changes),
                    cancellationToken);
                if (selected is { Count: > 0 })
                {
                    await _session.RunBusyAsync(async () =>
                    {
                        await _reconciler.ApplyAsync(selected, cancellationToken);
                        return true;
                    });
                }
            }
            finally
            {
                await _session.ForgetOfflineEditsAsync([.. _session.OfflineEdits], CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(TfvcStrings.LogReconcileFailed, ex);
            await _window.ShowErrorMessageAsync(
                string.Format(CultureInfo.CurrentCulture, TfvcStrings.ReconcileFailedFormat, TfvcDisplayText.DescribeError(ex)),
                CancellationToken.None);
        }
        finally
        {
            Volatile.Write(ref _isReconciling, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _session.StateChanged -= OnSessionStateChanged;
    }

    // Going online by any route, the menu or the connect button of a panel, proposes the local changes.
    private async void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (_session.IsOffline)
        {
            _wasOffline = true;
            return;
        }

        if (!_wasOffline || _session.IsBusy)
        {
            return;
        }

        _wasOffline = false;
        try
        {
            await ReconcileAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(TfvcStrings.LogReconcileAfterGoingOnlineFailed, ex);
        }
    }
}
