using System.Collections.ObjectModel;
using Ezvpn.App.Services;
using Ezvpn.Core;
using Ezvpn.Core.Interop;
using Microsoft.UI.Dispatching;

namespace Ezvpn.App.ViewModels;

/// <summary>
/// Owns the profile list and the single active tunnel session. Only one tunnel
/// may be connected at a time (mirroring the Apple app and the Rust
/// single-instance lock). Polls the active session's status on a timer.
/// </summary>
public sealed class TunnelsManager : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ProfileStore _store;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;

    private EzvpnSession? _session;
    private TunnelViewModel? _active;

    // Bumped on every connect attempt and every disconnect. An in-flight
    // EzvpnSession.Start that returns after its generation is superseded is stale
    // and its session is discarded instead of installed.
    private int _generation;

    public TunnelsManager(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _store = new ProfileStore();
        AuthKeys = new AuthKeyStore(new CredentialAuthKeyStore(), AuthKey.PublicKeyFor);

        foreach (var profile in _store.LoadAll())
        {
            Tunnels.Add(new TunnelViewModel(profile));
        }
        RefreshKeyNames();

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = PollInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Poll();
    }

    public ObservableCollection<TunnelViewModel> Tunnels { get; } = new();

    /// <summary>
    /// The app's shared, named auth keys. Profiles reference one by id; its
    /// secret is copied into the profile's own credential on save.
    /// </summary>
    public AuthKeyStore AuthKeys { get; }

    public TunnelViewModel? Active => _active;

    // --- Profile CRUD ---------------------------------------------------------

    /// <summary>Add a new profile with its secrets and persist it.</summary>
    public TunnelViewModel Add(TunnelProfile profile, string? relayAuthToken)
    {
        var authKey = RequireAuthKey(profile);

        // Persist both stores before touching the live model. If a credential
        // write fails, roll the profile (and any secret) back so nothing is left
        // half-created.
        _store.Save(profile);
        try
        {
            SecretStore.SaveAuthKey(profile.Id, authKey);
        }
        catch
        {
            _store.Delete(profile.Id);
            throw;
        }
        try
        {
            SetRelayToken(profile.Id, relayAuthToken);
        }
        catch
        {
            SecretStore.DeleteAuthKey(profile.Id);
            _store.Delete(profile.Id);
            throw;
        }

        var vm = new TunnelViewModel(profile);
        vm.AuthKeyName = KeyNameFor(profile);
        Tunnels.Add(vm);
        return vm;
    }

    /// <summary>Persist edits to an existing profile and its secrets.</summary>
    public void Update(TunnelViewModel vm, string? relayAuthToken)
    {
        var authKey = RequireAuthKey(vm.Profile);

        // Write the credentials first (nothing on disk changes if they fail),
        // then the profile atomically. If a later step fails, restore the prior
        // credentials so the stores can't diverge.
        var previousKey = SecretStore.LoadAuthKey(vm.Profile.Id);
        var previousRelay = SecretStore.LoadRelayToken(vm.Profile.Id);
        SecretStore.SaveAuthKey(vm.Profile.Id, authKey);
        try
        {
            SetRelayToken(vm.Profile.Id, relayAuthToken);
        }
        catch
        {
            RestoreAuthKey(vm.Profile.Id, previousKey);
            throw;
        }
        try
        {
            _store.Save(vm.Profile);
        }
        catch
        {
            RestoreAuthKey(vm.Profile.Id, previousKey);
            SetRelayToken(vm.Profile.Id, previousRelay);
            throw;
        }

        vm.AuthKeyName = KeyNameFor(vm.Profile);
        vm.NotifyProfileChanged();
    }

    /// <summary>Disconnect (if active), then delete the profile and its secrets.</summary>
    public void Delete(TunnelViewModel vm)
    {
        if (ReferenceEquals(_active, vm))
        {
            DisconnectInternal();
        }

        // Remove the profile, then the credentials. If credential removal fails,
        // restore the profile so we don't orphan a credential with no profile.
        _store.Delete(vm.Profile.Id);
        try
        {
            SecretStore.DeleteAuthKey(vm.Profile.Id);
            SecretStore.DeleteRelayToken(vm.Profile.Id);
        }
        catch
        {
            _store.Save(vm.Profile);
            throw;
        }

        Tunnels.Remove(vm);
    }

    /// <summary>The current stored relay token for a profile, or null if none.</summary>
    public string? LoadRelayToken(TunnelViewModel vm) => SecretStore.LoadRelayToken(vm.Profile.Id);

    /// <summary>
    /// Re-read every profile's auth-key name from the key list — after the keys
    /// screen has been open, where a key may have been renamed or deleted.
    /// </summary>
    public void RefreshKeyNames()
    {
        foreach (var vm in Tunnels)
        {
            vm.AuthKeyName = KeyNameFor(vm.Profile);
        }
    }

    /// <summary>
    /// The secret of the key the profile selected. Saving requires a key that is
    /// still in the list — the editor enforces that, so reaching here without one
    /// is a bug rather than user error.
    /// </summary>
    private string RequireAuthKey(TunnelProfile profile) =>
        AuthKeys.Find(profile.AuthKeyId)?.Secret
        ?? throw new InvalidOperationException(
            "This profile has no auth key selected. Pick one in the profile editor.");

    /// <summary>
    /// How the UI names a profile's key: its name, or a notice when the profile
    /// has no key in the list — because it was deleted, or because the profile
    /// predates key auth. Those are one state, not two: either way the profile
    /// cannot be re-saved until a key is picked.
    /// </summary>
    private string KeyNameFor(TunnelProfile profile) =>
        AuthKeys.Find(profile.AuthKeyId)?.Name ?? "Not in the key list";

    /// <summary>Persist (or clear) the optional relay token. A null/blank token
    /// deletes any stored item, so removing it in the editor removes the secret.</summary>
    private static void SetRelayToken(Guid id, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            SecretStore.DeleteRelayToken(id);
        }
        else
        {
            SecretStore.SaveRelayToken(id, token);
        }
    }

    private static void RestoreAuthKey(Guid id, string? previousKey)
    {
        if (previousKey is not null)
        {
            SecretStore.SaveAuthKey(id, previousKey);
        }
        else
        {
            SecretStore.DeleteAuthKey(id);
        }
    }

    // --- Connection lifecycle -------------------------------------------------

    /// <summary>
    /// Connect the given tunnel, disconnecting any other active one first. The
    /// blocking native setup runs off the UI thread; a setup failure is surfaced
    /// on the view model as an error.
    /// </summary>
    public async Task ConnectAsync(TunnelViewModel vm)
    {
        if (_active is not null && !ReferenceEquals(_active, vm))
        {
            // Wait for the previous tunnel's teardown to finish before starting
            // this one: they share the wintun adapter and the Rust single-instance
            // lock, so overlapping A's teardown with B's setup would race.
            await DisconnectAsync().ConfigureAwait(true);
        }

        // Claim this attempt. A subsequent disconnect/reconnect bumps _generation,
        // marking the in-flight Start below as stale.
        var generation = ++_generation;

        vm.SetConnecting();

        try
        {
            // Reading the credentials and building the config can fail too (an
            // unreadable or missing auth key); keep it inside the try so it
            // surfaces as a tunnel error like any other connect failure.
            var authKey = SecretStore.LoadAuthKey(vm.Profile.Id);
            var relayToken = SecretStore.LoadRelayToken(vm.Profile.Id);
            var json = EzvpnConfig.Build(vm.Profile, authKey, relayToken);

            var session = await Task.Run(() => EzvpnSession.Start(json)).ConfigureAwait(true);
            if (generation != _generation)
            {
                // Superseded while starting (disconnected or reconnected): the
                // session we just got is orphaned — tear it down, don't install it.
                session.Dispose();
                return;
            }
            _session = session;
            _active = vm;
            OnPropertyChanged(nameof(Active));
            _timer.Start();
        }
        catch (Exception ex)
        {
            // Only surface the failure if this attempt is still current; a stale
            // attempt's failure is irrelevant to whatever replaced it.
            if (generation == _generation)
            {
                vm.SetError(ex.Message);
            }
        }
    }

    /// <summary>Disconnect the given tunnel if it is the active one.</summary>
    public void Disconnect(TunnelViewModel vm)
    {
        if (ReferenceEquals(_active, vm))
        {
            DisconnectInternal();
        }
    }

    /// <summary>
    /// Stop and tear down whatever is active without blocking the UI thread. The
    /// UI reflects "disconnected" immediately; the blocking native teardown runs
    /// in the background. Safe to call when idle.
    /// </summary>
    public void DisconnectInternal() => _ = DisconnectAsync();

    /// <summary>
    /// Detach the active session from the UI immediately, then tear it down on a
    /// background thread. The returned task completes when the native teardown
    /// (<c>ezvpn_stop</c> removes routes/adapter and can take a few seconds)
    /// finishes — await it when teardown must complete before the next step
    /// (switching tunnels, app exit); fire-and-forget otherwise.
    /// </summary>
    public Task DisconnectAsync()
    {
        var session = DetachActive();
        return session is null ? Task.CompletedTask : Task.Run(() => DisposeSession(session));
    }

    /// <summary>
    /// Tear down synchronously, blocking until the native teardown completes. For
    /// app-exit paths only, where routes/adapter must be removed before the
    /// process ends and there is no live UI left to keep responsive.
    /// </summary>
    public void ShutdownSync() => DisposeSession(DetachActive());

    /// <summary>
    /// Detach the active session from the UI state and return it (now orphaned)
    /// for the caller to dispose. Runs on the UI thread and does no blocking
    /// native work: it invalidates any pending <see cref="ConnectAsync"/> (via the
    /// generation bump), stops polling, and clears the active view model.
    /// </summary>
    private EzvpnSession? DetachActive()
    {
        _generation++;
        _timer.Stop();

        var session = _session;
        _session = null;

        var previous = _active;
        _active = null;
        OnPropertyChanged(nameof(Active));
        previous?.SetDisconnected();
        return session;
    }

    /// <summary>Dispose a session, swallowing teardown failures.</summary>
    private static void DisposeSession(EzvpnSession? session)
    {
        try
        {
            session?.Dispose();
        }
        catch (Exception ex)
        {
            // Teardown is best-effort; a failure must not crash the app via an
            // unobserved background-task exception. Volatile routes/adapter are
            // reclaimed by the OS at worst on the next reboot.
            System.Diagnostics.Debug.WriteLine($"ezvpn teardown failed: {ex}");
        }
    }

    private void Poll()
    {
        if (_session is null || _active is null)
        {
            return;
        }
        var status = _session.TryGetStatus();
        _active.ApplyStatus(status);
    }
}
