# ezvpn-windows

A native Windows GUI for [`ezvpn`](https://github.com/andrewtheguy/ezvpn), the
IP-over-QUIC VPN. This is the Windows sibling of
[`ezvpn-apple`](https://github.com/andrewtheguy/ezvpn-apple): a **WinUI 3** app
that drives the `ezvpn` Rust core through its C FFI (`ezvpn.dll`), P/Invoked
from .NET.

Scoped for development-signed personal use — the MSI is unsigned.

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

Prerequisites: .NET 10 SDK. To run the tunnel you also need `ezvpn.dll` and
`wintun.dll` next to the app, and you must run elevated.

```powershell
# 1. Build ezvpn.dll from the sibling core repo
cd ..\ezvpn
./build-windows.ps1                     # -> ..\ezvpn\dist\windows\ezvpn.dll

# 2. Put ezvpn.dll + wintun.dll where the app build expects them
cd ..\ezvpn-windows
mkdir native -Force
copy ..\ezvpn\dist\windows\ezvpn.dll native\
# download wintun.dll from https://www.wintun.net/ (wintun\bin\amd64\wintun.dll)
copy <path>\wintun.dll native\

# 3. Build & test
dotnet build ezvpn-windows.slnx
dotnet test tests/Ezvpn.Core.Tests/Ezvpn.Core.Tests.csproj

# 4. Run (must be an ELEVATED terminal, or the app's UAC prompt will elevate it)
dotnet run --project src/Ezvpn.App
```

For quick FFI dev, set `EZVPN_LOCAL_DLL=1` so the app build pulls `ezvpn.dll`
straight from `..\ezvpn\dist\windows` (you still stage `wintun.dll` under
`native/`).

## Installer (MSI)

```powershell
dotnet publish src/Ezvpn.App/Ezvpn.App.csproj -c Release -r win-x64 --self-contained -o publish
# ensure publish\ezvpn.dll and publish\wintun.dll are present
dotnet build installer/Ezvpn.Installer.wixproj -c Release -p:PublishDir=(Resolve-Path publish) -p:ProductVersion=0.1.0.0
# -> installer/bin/Release/ezvpn.msi
```

CI builds the MSI automatically on a `v*` tag (`.github/workflows/release.yml`),
building `ezvpn.dll` from the core repo and downloading `wintun.dll`.

## Usage

1. Launch ezvpn (accept the UAC prompt).
2. **+** to add a profile: give it a name, the server's iroh node id, an optional
   auth token, and optional split-tunnel routes (`10.0.0.0/8`, `fd00::/8`, …).
3. Select it and **Connect**. The status panel shows the assigned IP, gateway,
   routes, and the live iroh connection path once connected.
4. **Disconnect** tears down the tunnel and routes.
