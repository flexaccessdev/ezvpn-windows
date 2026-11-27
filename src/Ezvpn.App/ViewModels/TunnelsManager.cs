using System.Collections.ObjectModel;
using Ezvpn.App.Services;
using Ezvpn.Core;
using Ezvpn.Core.Interop;
using Microsoft.UI.Dispatching;

namespace Ezvpn.App.ViewModels;

/// <summary>
/// Owns the profile list and the single active tunnel session. Only one tunnel
/// may be connected at a time (mirroring the Apple app and the Rust
/// single-instance lock). Reacts to the active session's events (connected,
/// reconnecting, ended) and polls its status on a timer for the live details
/// the events don't carry (uptime, the next retry's countdown).
/// </summary>
public sealed class TunnelsManager : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ProfileStore _store;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;

    private EzvpnSession? _session;
    private TunnelViewModel? _active;

    // The tunnel whose EzvpnSession.Start is still running (setup, before there
    // is a session to poll). Tracked so Cancel works during setup and so a
    // superseded attempt is put back to Disconnected instead of being left
    // showing "Connecting…" forever.
    private TunnelViewModel? _pending;

    // Completes once every native setup/teardown begun so far has finished:
    // cancelled or superseded setups (including disposing the session they
    // produced) and background teardowns. A new Start waits on it, so it never
    // overlaps them — they share the wintun adapter, routes and the core's
    // single-instance lock. Never faults (DisposeSession swallows failures and
    // each setup completes its own gate in a finally).
    private Task _nativeWork = Task.CompletedTask;

    // Bumped on every connect attempt and every disconnect. An in-flight
    // EzvpnSession.Start that returns after its generation is superseded is stale
    // and its session is discarded instead of installed.
    private int _generation;

    public TunnelsManager(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _store = new ProfileStore(mock: SecretStore.IsMemory);
        AuthKeys = new AuthKeyStore(new CredentialAuthKeyStore(SecretStore.Backend), AuthKey.PublicKeyFor);

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

    /// <summary>
    /// The tunnel that is up or on its way up — the active one, or the one still
    /// in setup. Connecting any other profile disconnects this one first.
    /// </summary>
    public TunnelViewModel? Current => _active ?? _pending;

    /// <summary>
    /// Tell every profile which other tunnel (if any) is current, so a profile's
    /// Connect can say that it will switch away from it.
    /// </summary>
    private void RefreshCurrentMarkers()
    {
        var current = Current;
        foreach (var vm in Tunnels)
        {
            vm.OtherActiveName = current is null || ReferenceEquals(current, vm) ? null : current.Name;
        }
        OnPropertyChanged(nameof(Current));
    }

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
        RefreshCurrentMarkers();
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
        // Other profiles name this one in their "switch from" hint.
        RefreshCurrentMarkers();
    }

    /// <summary>Disconnect (if active), then delete the profile and its secrets.</summary>
    public void Delete(TunnelViewModel vm)
    {
        if (ReferenceEquals(Current, vm))
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
        RefreshCurrentMarkers();
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
    /// How the UI names a profile's key: its name, or a notice when the key the
    /// profile names has been deleted from the list. The profile still connects
    /// with its own copy of the secret, but cannot be re-saved until a key is
    /// picked again.
    /// </summary>
    private string KeyNameFor(TunnelProfile profile) =>
        AuthKeys.Find(profile.AuthKeyId)?.Name ?? "Not in the key list — pick one in Edit";

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
        // marking the in-flight Start below as stale. Another profile still in
        // setup is superseded: it goes back to Disconnected (its session is
        // discarded when its Start returns).
        var generation = ++_generation;
        if (_pending is not null && !ReferenceEquals(_pending, vm))
        {
            _pending.SetDisconnected();
        }
        _pending = vm;

        vm.SetConnecting();
        RefreshCurrentMarkers();

        // Queue behind whatever native work is still in flight, and make the
        // next attempt queue behind this one (including any stale disposal).
        var previousNative = _nativeWork;
        var thisSetup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nativeWork = Task.WhenAll(previousNative, thisSetup.Task);

        try
        {
            // Reading the credentials and building the config can fail too (an
            // unreadable or missing auth key); keep it inside the try so it
            // surfaces as a tunnel error like any other connect failure.
            var authKey = SecretStore.LoadAuthKey(vm.Profile.Id);
            var relayToken = SecretStore.LoadRelayToken(vm.Profile.Id);
            var json = EzvpnConfig.Build(vm.Profile, authKey, relayToken);

            await previousNative.ConfigureAwait(true);
            if (generation != _generation)
            {
                // Superseded while waiting: whoever bumped the generation has
                // already reset this view model; never start at all.
                return;
            }

            // Timed from the moment the core is asked, on the worker thread
            // (so no thread-pool or dispatcher wait is counted): ezvpn_start
            // returns once the endpoint is online (the relay phase), and
            // CONNECTED follows the dial, handshake, adapter and routes.
            var profile = vm.Profile;
            AppLog.Write("app", $"connect '{profile.Name}': {profile.RelayUrls.Count} custom relay(s)"
                + (profile.RelayUrls.Count > 0 ? $", QUIC address discovery {(profile.RelayQuicAddrDiscovery ? "on" : "off")}" : "")
                + $", routes [{string.Join(", ", profile.Routes)}], routes6 [{string.Join(", ", profile.Routes6)}]");
            var clock = new System.Diagnostics.Stopwatch();

            // Events arrive on a library thread; hop to the UI thread without
            // waiting (the core holds a lock while the handler runs, and
            // ezvpn_stop waits for it). Logged before the hop, so the time is
            // the core's; AppLog.Write only queues the line.
            var session = await Task.Run(() =>
            {
                clock.Start();
                var started = EzvpnSession.Start(
                    json,
                    ev =>
                    {
                        AppLog.Write("app", $"event {ev.Kind} at {clock.ElapsedMilliseconds} ms"
                            + (ev.Message is null ? "" : $": {ev.Message}"));
                        _dispatcher.TryEnqueue(() => OnTunnelEvent(generation, ev));
                    });
                AppLog.Write("app", $"ezvpn_start returned at {clock.ElapsedMilliseconds} ms");
                return started;
            }).ConfigureAwait(true);
            if (generation != _generation)
            {
                // Superseded while starting (disconnected or reconnected): the
                // session we just got is orphaned — tear it down, don't install
                // it. Off the UI thread; the next Start waits for it (thisSetup
                // completes only after this).
                await Task.Run(() => DisposeSession(session)).ConfigureAwait(true);
                return;
            }
            _session = session;
            _pending = null;
            _active = vm;
            OnPropertyChanged(nameof(Active));
            RefreshCurrentMarkers();
            _timer.Start();
            // An event that arrived before the session was installed was
            // dropped (no session to act on yet); catch up on the state now.
            Poll();
        }
        catch (Exception ex)
        {
            // Only surface the failure if this attempt is still current; a stale
            // attempt's failure is irrelevant to whatever replaced it.
            AppLog.Write("app", $"connect '{vm.Profile.Name}' failed: {ex.Message}");
            if (generation == _generation)
            {
                _pending = null;
                vm.SetError(ex.Message);
                RefreshCurrentMarkers();
            }
        }
        finally
        {
            thisSetup.SetResult();
        }
    }

    /// <summary>
    /// Disconnect the given tunnel if it is the current one — including while
    /// it is still in setup, so Cancel works before the session exists.
    /// </summary>
    public void Disconnect(TunnelViewModel vm)
    {
        if (ReferenceEquals(Current, vm))
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
        if (session is null)
        {
            return Task.CompletedTask;
        }
        var teardown = Task.Run(() => DisposeSession(session));
        // A reconnect right after this must not start until the teardown is done.
        _nativeWork = Task.WhenAll(_nativeWork, teardown);
        return teardown;
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

        // A tunnel still in setup is cancelled too: the generation bump above
        // makes its Start discard the session when it returns.
        var pending = _pending;
        _pending = null;
        pending?.SetDisconnected();

        RefreshCurrentMarkers();
        return session;
    }

    /// <summary>Dispose a session, swallowing teardown failures.</summary>
    private static void DisposeSession(EzvpnSession? session)
    {
        if (session is null)
        {
            return;
        }
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            session.Dispose();
            AppLog.Write("app", $"ezvpn_stop took {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            // Teardown is best-effort; a failure must not crash the app via an
            // unobserved background-task exception. Volatile routes/adapter are
            // reclaimed by the OS at worst on the next reboot.
            AppLog.Write("app", $"ezvpn teardown failed: {ex}");
        }
    }

    /// <summary>
    /// Snapshot how <paramref name="vm"/>'s running tunnel reaches the server,
    /// for the "Connection path…" dialog; null when it is not the active tunnel
    /// or the snapshot is unavailable. Runs off the UI thread: the call waits on
    /// the custom relays' health requests.
    /// </summary>
    public async Task<ConnPathSnapshot?> QueryConnPathAsync(TunnelViewModel vm)
    {
        if (_session is not { } session || !ReferenceEquals(_active, vm))
        {
            return null;
        }
        return await Task.Run(session.TryGetConnPath).ConfigureAwait(true);
    }

    /// <summary>
    /// React to the session's events (on the UI thread). Ignored unless the
    /// session of <paramref name="generation"/> is still the installed one.
    /// </summary>
    private void OnTunnelEvent(int generation, EzvpnEvent ev)
    {
        if (generation != _generation || _session is null)
        {
            return;
        }
        if (ev.Kind == EzvpnEventKind.Ended)
        {
            OnRunEnded(ev.Message);
        }
        else
        {
            // Connected / reconnecting: refresh at once rather than at the next tick.
            Poll();
        }
    }

    private void Poll()
    {
        if (_session is null || _active is null)
        {
            return;
        }
        var status = _session.TryGetStatus();
        if (status is { IsStopped: true })
        {
            // Normally the Ended event got here first; this covers an Ended
            // that arrived before the session was installed.
            OnRunEnded(status.StopReason);
            return;
        }
        _active.ApplyStatus(status);
    }

    /// <summary>
    /// The core stopped trying for good. It has already torn the tunnel down;
    /// free the handle like a disconnect, but leave the tunnel showing why it
    /// stopped instead of "Disconnected".
    /// </summary>
    private void OnRunEnded(string? reason)
    {
        var vm = _active;
        if (vm is null)
        {
            return;
        }
        _ = DisconnectAsync();
        vm.SetStopped(reason);
    }
}
