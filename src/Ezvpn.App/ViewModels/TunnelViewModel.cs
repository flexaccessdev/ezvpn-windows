using Ezvpn.Core;

namespace Ezvpn.App.ViewModels;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

/// <summary>
/// A profile plus its live connection state, bound by the UI. Wraps a
/// <see cref="TunnelProfile"/> and the latest <see cref="ClientStatus"/> polled
/// from the tunnel.
/// </summary>
public sealed class TunnelViewModel : ObservableObject
{
    private ConnectionState _state = ConnectionState.Disconnected;
    private ClientStatus? _status;
    private string? _error;

    public TunnelViewModel(TunnelProfile profile)
    {
        Profile = profile;
    }

    public TunnelProfile Profile { get; }

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string ServerNodeId => Profile.ServerNodeId;

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
                // HasError depends on State as well as Error.
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public ClientStatus? Status
    {
        get => _status;
        private set
        {
            _status = value;
            // A status refresh can change any of the derived display fields.
            OnAllPropertiesChanged();
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(ErrorText));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsBusy => State == ConnectionState.Connecting;

    public bool CanConnect => State is ConnectionState.Disconnected or ConnectionState.Error;

    public bool CanDisconnect => State is ConnectionState.Connected or ConnectionState.Connecting;

    public string StateText => State switch
    {
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Connected => "Connected",
        ConnectionState.Error => "Error",
        _ => "Disconnected",
    };

    // --- Derived display fields (safe when Status is null) --------------------

    public string Mode => _status?.Mode ?? "—";

    public string AssignedIp => Join(_status?.AssignedIp, _status?.AssignedIp6);

    public string Gateway => Join(_status?.Gateway, _status?.Gateway6);

    public string RoutesText => JoinList(_status?.Routes, _status?.Routes6);

    public string ConnectionPath => _status?.Connection ?? "—";

    public string CustomRelaysText => (_status?.CustomRelays is { Count: > 0 } relays)
        ? string.Join(", ", relays.Select(FormatRelay))
        : "—";

    public string BypassText => (_status?.BypassAddrs is { Count: > 0 } b)
        ? string.Join(", ", b)
        : "—";

    public string ErrorText => Error ?? "";

    public bool HasError => State == ConnectionState.Error && !string.IsNullOrEmpty(Error);

    public string ConnectedSinceText =>
        _status?.ConnectedSinceSecs is { } secs
            ? FormatElapsed(TimeSpan.FromSeconds(secs))
            : "—";

    // Format as total hours:minutes:seconds so a session longer than a day does
    // not wrap the hours component back to 0 (TimeSpan's "hh" is 0–23).
    private static string FormatElapsed(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    // --- State transitions ----------------------------------------------------

    public void SetConnecting()
    {
        Error = null;
        Status = null;
        State = ConnectionState.Connecting;
    }

    public void SetError(string message)
    {
        Error = message;
        Status = null;
        State = ConnectionState.Error;
    }

    public void SetDisconnected()
    {
        Status = null;
        State = ConnectionState.Disconnected;
    }

    /// <summary>Apply a freshly polled status snapshot.</summary>
    public void ApplyStatus(ClientStatus? status)
    {
        Status = status;
        // Only downgrade from Connecting/Connected based on the snapshot; a null
        // snapshot while we still hold the session just means "not connected yet".
        if (status is not null)
        {
            State = status.IsConnected ? ConnectionState.Connected : ConnectionState.Connecting;
        }
    }

    /// <summary>Re-raise all bindings after the underlying profile is edited.</summary>
    public void NotifyProfileChanged() => OnAllPropertiesChanged();

    private static string Join(string? v4, string? v6)
    {
        var parts = new[] { v4, v6 }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        return parts.Length == 0 ? "—" : string.Join(", ", parts);
    }

    private static string JoinList(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        var all = new List<string>();
        if (a is not null) all.AddRange(a);
        if (b is not null) all.AddRange(b);
        return all.Count == 0 ? "—" : string.Join(", ", all);
    }

    private static string FormatRelay(CustomRelayStatus relay)
    {
        var state = relay.Working switch
        {
            true => "working",
            false => "not working",
            null => "status unavailable",
        };
        return string.IsNullOrEmpty(relay.Error)
            ? $"{relay.Url} ({state})"
            : $"{relay.Url} ({state}: {relay.Error})";
    }
}
