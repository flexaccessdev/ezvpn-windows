using Ezvpn.Core;

namespace Ezvpn.App.ViewModels;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,

    /// <summary>
    /// The tunnel was running but the core stopped trying for good (retries
    /// used up, a non-recoverable error, or a lost session with auto-reconnect
    /// off). <see cref="TunnelViewModel.Error"/> holds the reason.
    /// </summary>
    Stopped,
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
    private string _authKeyName = "Not in the key list — pick one in Edit";
    private string? _otherActiveName;

    public TunnelViewModel(TunnelProfile profile)
    {
        Profile = profile;
    }

    public TunnelProfile Profile { get; }

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string ServerNodeId => Profile.ServerNodeId;

    /// <summary>
    /// The display name of the auth key this profile connects with, kept in sync
    /// by <see cref="TunnelsManager"/> (the profile itself only stores the key
    /// id, and the key may since have been renamed or removed from the list).
    /// </summary>
    public string AuthKeyName
    {
        get => _authKeyName;
        set => SetProperty(ref _authKeyName, value);
    }

    /// <summary>
    /// The name of another profile that is connected or connecting, or null.
    /// Only one tunnel runs at a time, so connecting this one switches away from
    /// it — the UI says so instead of silently dropping the other tunnel.
    /// </summary>
    public string? OtherActiveName
    {
        get => _otherActiveName;
        set
        {
            if (SetProperty(ref _otherActiveName, value))
            {
                OnPropertyChanged(nameof(ConnectButtonText));
                OnPropertyChanged(nameof(StatusDetailText));
            }
        }
    }

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (!EqualityComparer<ConnectionState>.Default.Equals(_state, value))
            {
                _state = value;
                // Most display fields (status line, button labels, which detail
                // sections show) derive from the state.
                OnAllPropertiesChanged();
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
                OnPropertyChanged(nameof(StatusDetailText));
            }
        }
    }

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsBusy => State == ConnectionState.Connecting;

    public bool CanConnect => State is ConnectionState.Disconnected or ConnectionState.Error or ConnectionState.Stopped;

    public bool CanDisconnect => State is ConnectionState.Connected or ConnectionState.Connecting;

    public string StateText => State switch
    {
        // The core retries every failed attempt itself; say so once one has
        // failed, so a backoff wait reads as waiting rather than stuck.
        ConnectionState.Connecting when IsReconnecting => "Reconnecting…",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Connected => "Connected",
        ConnectionState.Error => "Couldn't connect",
        ConnectionState.Stopped => "Stopped",
        _ => "Disconnected",
    };

    /// <summary>
    /// The line under the big status word in the detail pane: uptime and
    /// address while connected, retry progress while reconnecting.
    /// </summary>
    public string StatusDetailText => State switch
    {
        ConnectionState.Connected when AssignedIp != "—" => $"Up {ConnectedSinceText} · {AssignedIp}",
        ConnectionState.Connected => $"Up {ConnectedSinceText}",
        ConnectionState.Connecting when IsReconnecting => ReconnectText,
        ConnectionState.Connecting => "Reaching the server…",
        _ when OtherActiveName is { } other => $"Connecting here disconnects \u201c{other}\u201d.",
        ConnectionState.Error => "Fix the problem below, then try again.",
        ConnectionState.Stopped => "Stopped trying to connect. Connect to try again.",
        _ => "Not connected",
    };

    /// <summary>"Connect", or "Switch here" when another profile's tunnel is up.</summary>
    public string ConnectButtonText => OtherActiveName is null ? "Connect" : "Switch here";

    /// <summary>The profile list's second line: the state, plus the address once connected.</summary>
    public string ListStatusText =>
        State == ConnectionState.Connected && _status?.AssignedIp is { Length: > 0 } ip
            ? $"Connected · {ip}"
            : StateText;

    /// <summary>"Cancel" while still connecting (nothing is up yet), else "Disconnect".</summary>
    public string DisconnectButtonText => State == ConnectionState.Connecting ? "Cancel" : "Disconnect";

    // --- Profile summary (what is configured, independent of the session) ----

    public string RelaysText => Profile.RelayUrls.Count > 0
        ? string.Join(", ", Profile.RelayUrls)
            + (Profile.RelayQuicAddrDiscovery ? "" : " (QUIC address discovery off)")
        : "Default iroh relays";

    public string ConfiguredRoutesText
    {
        get
        {
            var all = Profile.Routes.Concat(Profile.Routes6).ToList();
            return all.Count > 0 ? string.Join(", ", all) : "None — only the server's gateway";
        }
    }

    public string ReconnectPolicyText => (Profile.AutoReconnect, Profile.MaxReconnectAttempts) switch
    {
        (false, _) => "Off",
        (true, null) => "Automatic, no attempt limit",
        (true, 1) => "Automatic, up to 1 attempt",
        (true, var n) => $"Automatic, up to {n} attempts",
    };

    // --- Derived display fields (safe when Status is null) --------------------

    public string Mode => _status?.Mode ?? "—";

    public string AssignedIp => Join(_status?.AssignedIp, _status?.AssignedIp6);

    public string Gateway => Join(_status?.Gateway, _status?.Gateway6);

    public string RoutesText => JoinList(_status?.Routes, _status?.Routes6);

    public string BypassText => (_status?.BypassAddrs is { Count: > 0 } b)
        ? string.Join(", ", b)
        : "—";

    public string ErrorText => Error ?? "";

    public bool HasError =>
        State is ConnectionState.Error or ConnectionState.Stopped && !string.IsNullOrEmpty(Error);

    /// <summary>
    /// True while the core's reconnect loop is between attempts after at least
    /// one failure (the status reports <c>failed_attempts</c> &gt; 0).
    /// </summary>
    public bool IsReconnecting => State == ConnectionState.Connecting && _status?.FailedAttempts > 0;

    /// <summary>
    /// How far the retry loop has got, e.g. "3 failed attempts, next in 8s"
    /// (the same line <c>ezvpn client status</c> prints), or "trying now" while
    /// an attempt is in progress.
    /// </summary>
    public string ReconnectText
    {
        get
        {
            if (_status is null || _status.FailedAttempts == 0)
            {
                return "";
            }
            var n = _status.FailedAttempts;
            var attempts = n == 1 ? "1 failed attempt" : $"{n} failed attempts";
            var next = _status.NextAttemptSecs switch
            {
                > 0 and var secs => $"next in {FormatWait(TimeSpan.FromSeconds(secs))}",
                _ => "trying now",
            };
            return $"{attempts}, {next}";
        }
    }

    /// <summary>The last attempt's error while reconnecting, or "" when unknown.</summary>
    public string ReconnectErrorText => IsReconnecting ? _status?.LastError ?? "" : "";

    /// <summary>True while reconnecting and the core reported why the last attempt failed.</summary>
    public bool HasReconnectError => ReconnectErrorText.Length > 0;

    public string ConnectedSinceText =>
        _status?.ConnectedSinceSecs is { } secs
            ? FormatElapsed(TimeSpan.FromSeconds(secs))
            : "—";

    // Format as total hours:minutes:seconds so a session longer than a day does
    // not wrap the hours component back to 0 (TimeSpan's "hh" is 0–23).
    private static string FormatElapsed(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    // A backoff wait is at most a minute or so ("8s", "1m 0s"), so a compact
    // form reads better than the clock-style elapsed time above.
    private static string FormatWait(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";

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

    /// <summary>
    /// The core's run loop ended on its own; <paramref name="reason"/> is why
    /// (e.g. "Gave up after 3 reconnect attempt(s): …"), or null if it gave none.
    /// </summary>
    public void SetStopped(string? reason)
    {
        Error = reason ?? "The tunnel stopped.";
        Status = null;
        State = ConnectionState.Stopped;
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
}
