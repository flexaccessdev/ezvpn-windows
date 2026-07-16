# native/

Runtime native DLLs are staged here (git-ignored) and copied into the app's
build output:

- `ezvpn.dll` — the ezvpn Rust core C FFI. Build it with `../ezvpn/build-windows.ps1`
  (output in `../ezvpn/dist/windows/ezvpn.dll`), or set `EZVPN_LOCAL_DLL=1` to
  have the app build pull it from there automatically.
- `wintun.dll` — from https://www.wintun.net/ (`wintun/bin/amd64/wintun.dll`).

Both are bundled into the MSI by `installer/`. They are not needed to *compile*
the app (P/Invoke resolves them at runtime).
