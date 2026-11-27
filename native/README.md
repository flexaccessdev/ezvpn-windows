# native/

Runtime native DLLs, acquired automatically by `native.targets` (imported by
`src/Ezvpn.App`) and copied into the app's build / publish output. Each is staged
in a git-ignored folder named after its pin — `ezvpn-<tag>-<sha>\ezvpn.dll`,
`wintun-<version>\wintun.dll` — so changing a pin (or checking out a branch with
another one) stages that pin's DLL instead of reusing one staged for a different
pin. Old pin folders are left in place for switching back; delete them freely.
The DLLs are runtime-only (P/Invoke), so the solution compiles and the Core tests
run without them.

- **`ezvpn.dll`** — the ezvpn Rust core C FFI. By default downloaded from a pinned
  `ezvpn` GitHub release (`ezvpn-windows.dll.zip`) and verified by SHA256; staged
  in its pin's folder here. Pin/update the release with `scripts\bump-dll.ps1`.
  - Override: set `EZVPN_LOCAL_DLL=1` to use a **local core build** at
    `..\ezvpn\dist\windows\ezvpn.dll` (build it with `..\ezvpn\build-windows.ps1`).
    In that mode the DLL is read straight from the sibling dist into the build
    output — it is **not** copied into this folder, so nothing is synced into this
    repo.
- **`wintun.dll`** — the WireGuard wintun adapter (amd64), downloaded from the
  official build at <https://www.wintun.net/> and verified by SHA256, then staged
  in its version's folder here. arm64 is out of scope for this project.

Pinned URLs, versions, and checksums live in `native.targets`.
