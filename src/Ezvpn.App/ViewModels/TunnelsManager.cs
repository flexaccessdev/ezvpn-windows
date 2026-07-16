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

    public TunnelsManager(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _store = new ProfileStore();

        foreach (var profile in _store.LoadAll())
        {
            Tunnels.Add(new TunnelViewModel(profile));
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = PollInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Poll();
    }

    public ObservableCollection<TunnelViewModel> Tunnels { get; } = new();

    public TunnelViewModel? Active => _active;

    // --- Profile CRUD ---------------------------------------------------------

    /// <summary>Add a new profile with its required secret token and persist it.</summary>
    public TunnelViewModel Add(TunnelProfile profile, string authToken)
    {
        _store.Save(profile);
        TokenStore.Save(profile.Id, authToken);
        var vm = new TunnelViewModel(profile);
        Tunnels.Add(vm);
        return vm;
    }

    /// <summary>Persist edits to an existing profile and its required token.</summary>
    public void Update(TunnelViewModel vm, string authToken)
    {
        _store.Save(vm.Profile);
        TokenStore.Save(vm.Profile.Id, authToken);
        vm.NotifyProfileChanged();
    }

    /// <summary>Disconnect (if active), then delete the profile and its token.</summary>
    public void Delete(TunnelViewModel vm)
    {
        if (ReferenceEquals(_active, vm))
        {
            DisconnectInternal();
        }
        _store.Delete(vm.Profile.Id);
        TokenStore.Delete(vm.Profile.Id);
        Tunnels.Remove(vm);
    }

    /// <summary>The current stored token for a profile (for pre-filling the edit form).</summary>
    public string? LoadToken(TunnelViewModel vm) => TokenStore.Load(vm.Profile.Id);

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
            DisconnectInternal();
        }

        vm.SetConnecting();

        var token = TokenStore.Load(vm.Profile.Id);
        var json = EzvpnConfig.Build(vm.Profile, token);

        try
        {
            var session = await Task.Run(() => EzvpnSession.Start(json)).ConfigureAwait(true);
            _session = session;
            _active = vm;
            OnPropertyChanged(nameof(Active));
            _timer.Start();
        }
        catch (EzvpnException ex)
        {
            vm.SetError(ex.Message);
        }
        catch (Exception ex)
        {
            vm.SetError(ex.Message);
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

    /// <summary>Stop and tear down whatever is active. Safe to call when idle.</summary>
    public void DisconnectInternal()
    {
        _timer.Stop();
        _session?.Dispose();
        _session = null;

        var previous = _active;
        _active = null;
        OnPropertyChanged(nameof(Active));
        previous?.SetDisconnected();
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
