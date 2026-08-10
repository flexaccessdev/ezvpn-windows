# Running Windows CI locally, on a Hyper-V VM

`.github/workflows/windows-ci.yml` runs four steps on a `windows-latest`
runner: build the solution, run the Core tests, publish the self-contained app
(which is where the native DLLs are *required*), and build the MSI. This
describes how to run those same four steps on a local Windows Server VM, so a
Windows-only failure can be found without pushing a branch and waiting.

This mirrors the setup in the sibling [`wrustic`](../../wrustic) repo — same
VM, same three-file shape — and shares the VM with it.

The VM is close to the runner but not equal to it: `windows-latest` is
currently **Windows Server 2025**, and this VM is Server 2022. See
[Where this is not the real runner](#where-this-is-not-the-real-runner).

Everything is in `ci/windows/`:

| File | Runs on | Role |
| --- | --- | --- |
| `remote.ps1` | this machine | packs the working tree, ships it, invokes `ci.ps1` |
| `ci.ps1` | the VM | the four steps, natively |
| `provision.ps1` | the VM, once | installs the .NET toolchain on it |

`ci.ps1` installs nothing and changes no machine state, so it also runs on a
Windows dev box directly — `.\ci\windows\ci.ps1` — when you just want the four
steps without the trip over ssh.

`remote.ps1` runs under **PowerShell 7** on any OS (`brew install powershell`
on the Mac) and only shells out to `ssh`, `scp` and `tar`.

## Using it

```powershell
pwsh ./ci/windows/remote.ps1              # build, test, publish, MSI
pwsh ./ci/windows/remote.ps1 shell        # interactive shell on the VM, in the tree
pwsh ./ci/windows/remote.ps1 doctor       # report on the VM, change nothing
pwsh ./ci/windows/remote.ps1 clean        # drop the VM's NuGet package cache
pwsh ./ci/windows/remote.ps1 provision    # (re)run provision.ps1 on the VM
```

`EZVPN_WINCI_HOST` overrides the ssh target (default: the `windows-ci-build`
alias), `EZVPN_WINCI_REMOTE_DIR` the landing directory (default
`C:\ci-workspaces\ezvpn-windows`), and `EZVPN_WINCI_NUGET_DIR` what `clean`
deletes.

The tree is copied rather than fetched from git: the reason to run this instead
of pushing a branch is to test what is in front of you, uncommitted changes
included. There is no git on the VM and nothing needs one.

## The VM

`ci-builder`, on the Hyper-V host at `10.22.38.75`: 4 vCPU, 8 GB RAM, Windows
Server 2022 Datacenter **Evaluation**. The evaluation edition is time-limited;
when it lapses the VM stops being usable and has to be rebuilt or licensed.

It is the same VM `wrustic` uses, so it also carries rustup and VS Build Tools.
Nothing here depends on those, and nothing here removes them: this repo's
provisioner writes to its own log (`C:\provision\ezvpn-windows-provision.log`)
and its own scheduled task (`ezvpn-windows-provision`), leaving wrustic's
unprefixed pair alone.

## SSH

Auth is by key, as the `Administrator` account, through an ssh alias:

```
Host windows-ci-build
    HostName 10.22.38.75
    User Administrator
```

See `wrustic/docs/windows-vm-ci.md` for how the key was installed — the one
thing worth repeating is that keys for an administrator account go in
`C:\ProgramData\ssh\administrators_authorized_keys`, not
`~/.ssh/authorized_keys`, and sshd silently refuses that file if its ACL is too
loose or it has a UTF-8 BOM.

## Provisioning

`ci/windows/provision.ps1`, deployed to
`C:\provision\ezvpn-windows-provision.ps1` and run as a SYSTEM scheduled task,
logging to `C:\provision\ezvpn-windows-provision.log` with `DONE-OK` or
`DONE-FAIL` as its last line. `remote.ps1 provision` does the deploy, starts
the task and tails that log. It runs as a task rather than inline over ssh so a
dropped connection cannot kill the SDK install half way, and every step is
guarded so re-running it is a no-op.

It installs, via Microsoft's `dotnet-install.ps1` into `C:\dotnet`:

- the **.NET 10 SDK**, which builds everything — `Ezvpn.Core` (net8.0),
  `Ezvpn.App` (`net10.0-windows…`, WinUI 3) and the WiX 5 installer project;
- the **.NET 8 runtime**, which is what actually *runs* the net8.0 Core tests.

No Visual Studio workload is needed. WinUI 3 here is C#-only and unpackaged
(`WindowsPackageType=None`): the Windows App SDK, the Windows SDK ref pack and
the WiX toolset all arrive as NuGet packages.

Everything is machine-scoped, not per-profile — the provisioning task runs as
SYSTEM and ssh logs in as Administrator, so a profile-local install would be
invisible to every CI run:

```
Path                       += C:\dotnet
DOTNET_ROOT                   C:\dotnet
DOTNET_CLI_TELEMETRY_OPTOUT   1
DOTNET_NOLOGO                 1
NUGET_PACKAGES                C:\ci-cache\nuget
```

`NUGET_PACKAGES` points outside the workspace deliberately: `remote.ps1`
replaces the workspace directory outright on every run, so a package cache
inside it would mean re-downloading the Windows App SDK from nuget.org every
time. `bin\` and `obj\` do go with the workspace, so every run is a full
compile — it is the restore that is worth keeping, and MSBuild is fast enough
that redirecting the intermediate output too is not worth the per-project
plumbing.

Nothing pins the SDK, so it drifts with whatever `-Channel 10.0` resolves to on
install day. Re-running `remote.ps1 provision` is a no-op once a 10.x SDK is
present; to move to a newer one, delete `C:\dotnet\sdk\<old>` (or all of
`C:\dotnet`) first.

### The thing that will waste an afternoon

**sshd caches its environment.** The service captures its environment block
when it starts and every session inherits that copy, so a machine-scoped `PATH`
or `DOTNET_*` change is invisible over ssh until `Restart-Service sshd`. Set
the variables, watch `dotnet` still not be found, conclude the install failed —
it did not. `provision.ps1` restarts sshd as its last step for this reason; if
you set a machine variable by hand afterwards, restart it again.

## Two things that shape `remote.ps1`

**The transfer is a file, not a pipe.** The obvious `tar -czf - . | ssh "tar
-xzf -"` does not work from PowerShell: a native-to-native pipeline is text,
not bytes, and PowerShell re-encodes it, corrupting the archive. So the tree is
packed to a temp `.tgz`, handed to `scp`, and unpacked at the far end. The
happy side effect is that a half-finished transfer can never be unpacked.

**The workspace is replaced, not updated.** `tar` has no `--delete`, so
unpacking over the previous tree would leave a file you deleted here still
sitting there and still being compiled. `remote.ps1` removes the directory and
recreates it. Nothing under it needs to survive — the NuGet cache lives in
`C:\ci-cache\nuget`.

Paths on the remote side deliberately contain no spaces, so no remote command
needs quoting: a quoted string has to survive PowerShell, then ssh, then
cmd.exe, and the layers do not agree. The overrides are checked against that
rule before use — they are interpolated into command lines that include
`rmdir /s /q`, and a space or an `&` in one of them is not a parse error so
much as a demolition order. The one place that genuinely needs PowerShell at
the far end (registering the provisioning task) sidesteps the problem entirely
by sending the script base64-encoded to `powershell -EncodedCommand`.

**One run at a time.** The workspace is single and shared, so a second run
starting mid-build would delete the tree the first is compiling. `remote.ps1`
claims `C:\ci-workspaces\ezvpn-windows.lock` with `mkdir` (which fails,
atomically, if it exists) and drops it in a `finally`. If a run is killed hard
enough to skip that, the next one says so and prints the `ssh ... rmdir` to
clear it.

## Where this is not the real runner

Worth knowing before trusting a green run:

- **The OS is a release behind.** `windows-latest` is Windows Server 2025; this
  VM is Server 2022, build 20348. GitHub moves the label, and the two silently
  diverge when it does. Rebuilding the VM on Server 2025 is the way to close
  the gap; pinning the workflow to `windows-2022` is the wrong one, because it
  makes CI test an OS older than the one users get.
- **The SDK floats, and differs.** The runner installs whatever `8.0.x` /
  `10.0.x` resolve to on the day; this VM has whatever `-Channel 10.0` gave it
  on install day. Both drift, independently.
- **It is a bare Server install.** `windows-latest` carries an enormous
  preinstalled toolbox. This VM has the .NET SDK, plus rustup and MSVC left by
  wrustic, and nothing else — a build step that quietly depends on some other
  preinstalled tool would pass on GitHub and fail here.
- **It runs as Administrator.** The app itself is elevation-dependent, so
  anything that ever grows a privilege check sees something different here than
  on the runner.
- **Nothing here launches the app.** Both this and GitHub CI stop at the MSI:
  there is no wintun adapter, no iroh server and no interactive desktop
  session, so connecting a tunnel is still a manual test on a real machine.
