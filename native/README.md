# native/

Runtime native DLLs, acquired automatically by `native.targets` (imported by
`src/Ezvpn.App`) and copied into the app's build / publish output. Generated DLLs
in this folder are git-ignored; the DLLs are runtime-only (P/Invoke), so the
solution compiles and the Core tests run without them.

- **`ezvpn.dll`** — the ezvpn Rust core C FFI. By default downloaded from a pinned
  `ezvpn` GitHub release (`ezvpn-windows.dll.zip`) and verified by SHA256; staged
  here. Pin/update the release with `scripts\bump-dll.ps1`.
  - Override: set `EZVPN_LOCAL_DLL=1` to use a **local core build** at
    `..\ezvpn\dist\windows\ezvpn.dll` (build it with `..\ezvpn\build-windows.ps1`).
    In that mode the DLL is read straight from the sibling dist into the build
    output — it is **not** copied into this folder, so nothing is synced into this
    repo.
- **`wintun.dll`** — the WireGuard wintun adapter (amd64), downloaded from the
  official build at <https://www.wintun.net/> and verified by SHA256, then staged
  here. arm64 is out of scope for this project.

Pinned URLs, versions, and checksums live in `native.targets`.
