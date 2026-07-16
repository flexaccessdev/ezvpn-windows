# ezvpn-windows — notes for Claude

Native Windows GUI (WinUI 3, .NET) for the `ezvpn` VPN. Sibling of `ezvpn-apple`.
The Rust core + C FFI live in the `../ezvpn` repo (`src/ffi_windows.rs`,
`windows/ezvpn.h`, `build-windows.ps1`, `docs/Windows-App.md`).

## Key facts

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

## Conventions

- The `ezvpn_start` config JSON shape is defined in `../ezvpn/windows/ezvpn.h`;
  `EzvpnConfig.Build` produces it. Keep them in sync.
- `ClientStatus` mirrors the Rust `ClientStatus` (snake_case) from
  `../ezvpn/src/control.rs`.
- Auth tokens live in Windows Credential Manager (`TokenStore`), never in the
  profile JSON.
- Installer uses **WiX v5** (v6/v7 require accepting the paid OSMF EULA). The MSI
  is unsigned by design; code signing and MSIX/Store packaging are out of scope.
- Use classic `[DllImport]` (not `[LibraryImport]`) for the `advapi32`
  Credential Manager calls — the `CREDENTIAL` struct isn't source-gen
  marshallable.
