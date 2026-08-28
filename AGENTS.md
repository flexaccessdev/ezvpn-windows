# ezvpn-windows — notes for Claude

Native Windows GUI (WinUI 3, .NET) for the `ezvpn` VPN. Sibling of `ezvpn-apple`.
The Rust core + C FFI live in the `../ezvpn` repo (`src/ffi_windows.rs`,
`windows/ezvpn.h`, `build-windows.ps1`, `docs/Windows-App.md`).

## Key facts

- Strict no backward compatibility (0.0.x) or legacy code paths
- The transport is iroh (Rust-only) — never reimplement the protocol in .NET.
  The app P/Invokes `ezvpn.dll` (`ezvpn_start` / `ezvpn_status` / `ezvpn_stop`).
- Single elevated process: app manifest requests Administrator; the tunnel runs
  in-process (no service, no IPC). `wintun.dll` must be beside `ezvpn.dll`.
- `Ezvpn.Core` is `net8.0` and pure (unit-tested). `Ezvpn.App` is
  `net10.0-windows…` WinUI 3, needs a RID to build (defaults to `win-x64`).
- Build the whole solution with `dotnet build ezvpn-windows.slnx` (no `-r` — the
  app has a default RID; passing `-r` to a *solution* is rejected by the SDK).
- Native DLLs are runtime-only, so the app compiles/tests without them. They are
  copied into output for running/packaging (from `native/`, or from
  `..\ezvpn\dist\windows` when `EZVPN_LOCAL_DLL=1`).
- **To check a change on Windows, run `pwsh ci/windows/remote.ps1`.** From the
  macOS dev box (where none of this builds) it ships the *working tree* —
  uncommitted changes included — to the Hyper-V VM (`windows-ci-build`, shared
  with `../wrustic`) and runs build, test, publish and MSI there. `remote.ps1
  doctor` reports the VM's toolchain, `remote.ps1 provision` (re)installs it.
  See `docs/windows-vm-ci.md`.
- Do **not** reach for `.github/workflows/windows-ci.yml` to find out whether
  something works: it needs a commit and a push to trigger, and it is slow even
  warm, because a GitHub runner starts with a cold NuGet cache every time. It
  runs itself on push/PR — read it after the fact, don't drive it.
  `ci\windows\ci.ps1` is the same four steps on the VM, with a warm NuGet cache;
  when the two disagree, the workflow is right and `ci.ps1` is stale.

## Conventions

- The `ezvpn_start` config JSON shape is defined in `../ezvpn/windows/ezvpn.h`;
  `EzvpnConfig.Build` produces it. Keep them in sync.
- `ClientStatus` mirrors the Rust `ClientStatus` (snake_case) from
  `../ezvpn/src/control.rs`.
- The client authenticates with an **ed25519 keypair**, not a pre-shared token:
  the config's `auth_key` is the client's `ed25519-sec:…` secret, and its public
  half goes on the server's `authorized_keys` file. Keys are generated and parsed
  only through the FFI (`ezvpn_generate_client_key` / `ezvpn_client_public_key`,
  wrapped by `Core/Interop/AuthKey`) — never reimplement the key format in .NET.
- Like the Apple and Android apps, the app keeps one shared list of **named**
  keys (`AuthKeyStore`) that profiles reference by id (`TunnelProfile.AuthKeyId`);
  saving a profile copies that key's secret into the profile's own credential,
  which is what `ezvpn_start` is handed. So a key deleted from the list doesn't
  break profiles already saved with it.
- All secrets live in Windows Credential Manager (`SecretStore`), never in the
  profile JSON: `ezvpn-profile-key:<profileId>` (the profile's auth key),
  `ezvpn-relay:<profileId>` (the optional relay token) and `ezvpn-key:<keyId>`
  (one record per named key — one credential each, because a credential blob caps
  out at 2560 bytes).
- Installer uses **WiX v5** (v6/v7 require accepting the paid OSMF EULA). The MSI
  is unsigned by design; code signing and MSIX/Store packaging are out of scope.
- Use classic `[DllImport]` (not `[LibraryImport]`) for the `advapi32`
  Credential Manager calls — the `CREDENTIAL` struct isn't source-gen
  marshallable.
- Icons: `assets\icon.svg` (the shield/keyhole glyph, shared with ezvpn-apple) is
  the source of truth. `scripts\render-icons.ps1` renders it to the committed
  `src\Ezvpn.App\Assets\ezvpn.ico` (teal, multi-size) plus a gray
  `ezvpn-gray.ico` via GDI+ — re-run it (needs Windows PowerShell 5.1) only when
  the SVG changes; CI just uses the committed `.ico`s. The teal `ezvpn.ico` is
  the `.exe` icon (`<ApplicationIcon>`) and the title-bar icon
  (`AppWindow.SetIcon`); the tray shows `ezvpn-gray.ico` until the tunnel is
  connected, then swaps to the teal one.
- The system tray (`Services\TrayIcon.cs`) is hand-rolled on `Shell_NotifyIcon`
  (WinUI 3 has no tray API): it subclasses the window's WndProc for callbacks and
  uses a native `TrackPopupMenuEx` menu. Closing the window hides to the tray;
  only the tray's Quit exits the process. `SetConnected(bool)` swaps between the
  teal (connected) and gray (not-connected) icons; `MainWindow` drives it from
  the active tunnel's connection state.
