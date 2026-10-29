using System.Runtime.InteropServices;
using Ezvpn.App.Services;
using Ezvpn.Core;
using Ezvpn.Core.Interop;
using Microsoft.UI.Xaml;

namespace Ezvpn.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    private const uint MbIconError = 0x10;

    private Window? _window;

    public App()
    {
        InitializeComponent();

        // Anything unhandled that reaches the framework (e.g. a throwing async
        // event handler) would otherwise crash the app as a "stowed exception"
        // with no message. Log it and show it, but keep running — the window is
        // already up, so a single failed action shouldn't kill the process.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            TryLog("Unhandled exception", e.Exception.ToString());
            MessageBox(IntPtr.Zero, e.Exception.Message, "ezvpn error", MbIconError);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Fail fast if the native DLLs aren't beside the app: without ezvpn.dll
            // the process would crash on the first P/Invoke with no window; without
            // wintun.dll the window would open but Connect would fail later.
            EzvpnSession.EnsureNativeDependencies();

            // Route the Rust core's logs to stderr (honors RUST_LOG).
            EzvpnSession.InitLogging();

            // EZVPN_KEYCHAIN=mock: secrets in memory, profiles in their own
            // directory, before anything reads either. Any other value is
            // refused here rather than read as Credential Manager.
            bool mock;
            try
            {
                mock = KeychainMode.IsMock(Environment.GetEnvironmentVariable(KeychainMode.Variable));
            }
            catch (ArgumentException ex)
            {
                ReportFatalStartup("ezvpn cannot start", ex.Message);
                return;
            }
            if (mock)
            {
                SecretStore.UseMemory();
            }

            _window = new MainWindow();
            _window.Activate();
        }
        catch (EzvpnException ex)
        {
            // Expected, actionable message (missing native deps) — no stack trace.
            ReportFatalStartup("ezvpn cannot start", ex.Message);
        }
        catch (Exception ex)
        {
            // Any other startup failure (e.g. a missing resources.pri would surface
            // here rather than as a silent stowed-exception crash).
            ReportFatalStartup("ezvpn failed to start", ex.ToString());
        }
    }

    /// <summary>Log a fatal startup error, show it, and exit — the app can't run.</summary>
    private void ReportFatalStartup(string caption, string detail)
    {
        TryLog(caption, detail);
        MessageBox(IntPtr.Zero, detail, caption, MbIconError);
        Exit();
    }

    /// <summary>Best-effort append to %ProgramData%\ezvpn\startup-error.log.</summary>
    private static void TryLog(string caption, string detail)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ezvpn");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "startup-error.log"),
                $"[{DateTimeOffset.Now:o}] {caption}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never mask the original failure.
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
