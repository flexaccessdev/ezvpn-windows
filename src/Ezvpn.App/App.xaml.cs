using Ezvpn.Core.Interop;
using Microsoft.UI.Xaml;

namespace Ezvpn.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Route the Rust core's logs to stderr (honors RUST_LOG).
        EzvpnSession.InitLogging();

        _window = new MainWindow();
        _window.Activate();
    }
}
