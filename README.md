# ezvpn-windows

A native Windows GUI for [`ezvpn`](https://github.com/flexaccessdev/ezvpn), the
IP-over-QUIC VPN. This is the Windows sibling of
[`ezvpn-apple`](https://github.com/flexaccessdev/ezvpn-apple): a **WinUI 3** app
that drives the `ezvpn` Rust core through its C FFI (`ezvpn.dll`), P/Invoked
from .NET.

Scoped for development-signed personal use on x64 Windows — the MSI is unsigned,
and ARM64 packaging is currently out of scope.

## How it works

The VPN transport is [iroh](https://github.com/n0-computer/iroh) (Rust-only), so
the app cannot reimplement the protocol in managed code — it reuses the Rust
core. Unlike the Apple app (whose Network Extension is handed a `utun` fd), the
Windows FFI wraps the desktop `VpnClient`, which creates the **wintun** adapter
and installs routes itself. So:

- The whole GUI runs **elevated** (the app manifest requests Administrator; a UAC
  prompt appears at launch). There is no separate service and no IPC — the tunnel
  runs in-process.
- `wintun.dll` (from [wintun.net](https://www.wintun.net/)) must sit next to
  `ezvpn.dll`. The MSI bundles both.

```
Ezvpn.App (WinUI 3, elevated)
  profiles ──▶ ezvpn_start(json) ──▶ ezvpn.dll ──▶ iroh + wintun + routes
  poll     ──▶ ezvpn_status()
  stop     ──▶ ezvpn_stop()
```

See `docs/Windows-App.md` in the `ezvpn` repo for the FFI contract.

## Layout

| Project | What |
|---|---|
| `src/Ezvpn.Core` | Pure model + config JSON builder + validation + status DTOs + the `ezvpn.dll` P/Invoke wrapper (`EzvpnSession`). No WinUI; unit-tested. |
| `src/Ezvpn.App` | The WinUI 3 app: profile list/detail/edit, connect/disconnect, live status polling. Stores profiles under `%ProgramData%\ezvpn\profiles` and the auth token in Windows Credential Manager. |
| `tests/Ezvpn.Core.Tests` | xUnit tests for the pure logic. |
| `installer/` | WiX v5 MSI that bundles the published app + `ezvpn.dll` + `wintun.dll`. |

## Build & run (local)

Prerequisites: .NET 10 SDK.

The app **compiles and tests without any native DLLs** (P/Invoke resolves them at
runtime), but it **refuses to start** unless both `ezvpn.dll` and `wintun.dll` are
in the build output beside the exe. On launch it checks for them and, if either
is missing, shows an *"ezvpn cannot start"* dialog naming what's absent and where
it looked, then exits — rather than crashing with no window (missing `ezvpn.dll`)
or failing only later at connect time (missing `wintun.dll`).

### 1. Native DLLs — fetched automatically

The build acquires both native DLLs for you (see `native/native.targets`), stages
them under `native\`, and copies them into the build/publish output — no manual
copying:

- **`wintun.dll`** — downloaded from the official WireGuard build at
  <https://www.wintun.net/> (amd64), verified by SHA256. Always automatic.
- **`ezvpn.dll`** — downloaded from a **pinned `ezvpn` release** asset
  (`ezvpn-windows.dll.zip`), verified by SHA256. This is the "reference the
  release zip directly, no local core build" path, mirroring how `ezvpn-apple`
  pins the xcframework. Pin/update the release with `scripts\bump-dll.ps1`
  (the Windows analog of `ezvpn-apple`'s `bump-xcframework.sh`):

  ```powershell
  ./scripts/bump-dll.ps1             # latest release
  ./scripts/bump-dll.ps1 v0.0.23     # or pin an explicit release
  ```

  The script is cross-platform — on Linux/macOS run it with PowerShell Core
  (`pwsh scripts/bump-dll.ps1`). It only needs the `gh` CLI when resolving the
  latest release (i.e. when no tag is given).

To iterate on the FFI against a **local core build** instead of a release, set
`EZVPN_LOCAL_DLL=1`; `ezvpn.dll` is then linked straight from
`..\ezvpn\dist\windows` (build it there with `./build-windows.ps1`). `wintun.dll`
is still downloaded automatically.

```powershell
# Local FFI dev against ..\ezvpn\dist\windows\ezvpn.dll
cd ..\ezvpn; ./build-windows.ps1; cd ..\ezvpn-windows
$env:EZVPN_LOCAL_DLL = "1"
```

The currently pinned tag and checksum are recorded in `native/native.targets`.
An ordinary build can still compile while offline if the runtime DLLs are
unavailable, but publish fails rather than producing an unusable MSI.

### 2. Build & test

```powershell
dotnet build ezvpn-windows.slnx        # fetches native DLLs; honors EZVPN_LOCAL_DLL
dotnet test tests/Ezvpn.Core.Tests/Ezvpn.Core.Tests.csproj
```

### 3. Run (elevated — this is the part that trips people up)

The app manifest requests Administrator, so it can only start **with UAC
elevation**. That has one important consequence:

> ⚠️ **`dotnet run` does _not_ work from a normal terminal.** It launches the exe
> with `CreateProcess`, which cannot elevate — Windows blocks the launch and you
> see *nothing* (no window, no UAC prompt, no error).

Use one of these instead:

```powershell
# A. Launch the built exe so Windows shows the UAC prompt (or just double-click it
#    in Explorer). Works from any terminal.
Start-Process src\Ezvpn.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Ezvpn.App.exe

# B. Or run `dotnet run` from an ALREADY-elevated terminal (open Windows Terminal /
#    PowerShell "as Administrator"). The child inherits elevation, so it starts.
dotnet run --project src/Ezvpn.App
```

Note it's a GUI app: output goes to a window, never to the terminal.

## Installer (MSI)

```powershell
dotnet publish src/Ezvpn.App/Ezvpn.App.csproj -c Release -r win-x64 --self-contained -o publish
# native.targets stages ezvpn.dll + wintun.dll into publish\ automatically
dotnet build installer/Ezvpn.Installer.wixproj -c Release -p:PublishDir="$(Resolve-Path publish)" -p:ProductVersion=0.1.0.0
# -> installer/bin/Release/ezvpn.msi
```

CI builds the MSI on demand (`.github/workflows/release.yml`, `workflow_dispatch`):
each run self-tags a `yyyymmddhhmmss-<short-sha>` prerelease. It does **no** Rust /
core build — `native.targets` downloads the prebuilt, SHA256-verified `ezvpn.dll`
from the pinned `ezvpn` release (and `wintun.dll` from wintun.net) during publish.
Change which DLL ships by re-pinning with `scripts\bump-dll.ps1`.

## Usage

1. Launch ezvpn (accept the UAC prompt).
2. **+** to add a profile: give it a name, the server's iroh node id, the auth
   token (required), and optional split-tunnel routes (`10.0.0.0/8`, `fd00::/8`, …).
3. Select it and **Connect**. The status panel shows the assigned IP, gateway,
   routes, and the live iroh connection path once connected.
4. **Disconnect** tears down the tunnel and routes.
