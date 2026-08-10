# Run the Windows CI steps against *this* working tree on the CI VM.
#
#   ./ci/windows/remote.ps1              # build, test, publish, MSI
#   ./ci/windows/remote.ps1 shell        # interactive shell on the VM, in the tree
#   ./ci/windows/remote.ps1 doctor       # report on the VM, change nothing
#   ./ci/windows/remote.ps1 clean        # drop the VM's NuGet package cache
#   ./ci/windows/remote.ps1 provision    # (re)run provision.ps1 on the VM
#
# The far end is a Hyper-V VM running Windows Server 2022 with the .NET SDK
# installed machine-wide; ci\windows\ci.ps1 is the half that runs over there.
# See docs/windows-vm-ci.md for how the VM was built.
#
# This half runs under PowerShell 7 on any OS (it is normally invoked from the
# macOS dev box) and shells out to ssh/scp/tar.
#
# The tree is copied rather than fetched from git on purpose — the reason to
# run this instead of pushing a branch is to test what you have in front of
# you, uncommitted changes included.
#
# Overrides:
#   EZVPN_WINCI_HOST        ssh target        (default: the 'windows-ci-build' ssh alias)
#   EZVPN_WINCI_REMOTE_DIR  landing directory (default: C:\ci-workspaces\ezvpn-windows)
#   EZVPN_WINCI_NUGET_DIR   cache dropped by `clean` (default: C:\ci-cache\nuget)
param(
    [ValidateSet('ci', 'shell', 'doctor', 'clean', 'provision')]
    [string] $Command = 'ci'
)

$ErrorActionPreference = 'Stop'

# The remote command lines below are assembled by hand and handed to cmd.exe on
# an Administrator session, and two of them are `rmdir /s /q`. A directory
# override carrying a space, a quote or an `&` would not merely fail to parse.
# Take only a plain drive-letter path.
function Assert-RemotePath {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Name)

    if ($Path -notmatch '^[A-Za-z]:\\[A-Za-z0-9_.\\-]+$' -or $Path -match '\.\.' -or $Path.EndsWith('\')) {
        throw "$Name must be a drive-letter path of letters, digits, _ . - and \ with no trailing separator; got: $Path"
    }
}

# Not $Host — that name is taken by an automatic variable.
$Target    = if ($env:EZVPN_WINCI_HOST)       { $env:EZVPN_WINCI_HOST }       else { 'windows-ci-build' }
$RemoteDir = if ($env:EZVPN_WINCI_REMOTE_DIR) { $env:EZVPN_WINCI_REMOTE_DIR } else { 'C:\ci-workspaces\ezvpn-windows' }
Assert-RemotePath $RemoteDir 'EZVPN_WINCI_REMOTE_DIR'
# Join-Path with '..\..' would not resolve on macOS/Linux, where \ is a
# filename character rather than a separator.
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

function Info($m) { Write-Host "[winci] $m" }

function Invoke-Remote {
    param([Parameter(Mandatory)][string] $CommandLine, [switch] $Tty, [switch] $IgnoreExitCode)

    if ($Tty) { & ssh -t $Target $CommandLine } else { & ssh $Target $CommandLine }
    if ($LASTEXITCODE -ne 0 -and -not $IgnoreExitCode) {
        throw "remote command failed (exit $LASTEXITCODE): $CommandLine"
    }
}

# For anything that genuinely needs PowerShell on the far end. A quoted script
# would have to survive PowerShell, then ssh, then cmd.exe, then PowerShell
# again — four layers that have to agree about quoting. Base64 (UTF-16LE, what
# -EncodedCommand wants) contains no quote, space or shell metacharacter, so
# none of them has an opinion about it.
function Invoke-RemotePowerShell {
    param([Parameter(Mandatory)][string] $Script, [switch] $IgnoreExitCode)

    # Without this, every cmdlet's progress record comes back over stderr as a
    # page of CLIXML, because the far end has no console to draw a bar on.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$ProgressPreference = 'SilentlyContinue'`n$Script"))
    Invoke-Remote "powershell -NoProfile -EncodedCommand $encoded" -IgnoreExitCode:$IgnoreExitCode
}

# No path here contains a space — Assert-RemotePath keeps the overrides that
# way — so nothing below needs quoting inside the remote command line, which
# keeps PowerShell, ssh and cmd.exe from having to agree on how quotes nest.
$CiScript = "$RemoteDir\ci\windows\ci.ps1"

# Prefixed because this VM is shared with wrustic's CI, which owns the
# unprefixed provision task and log.
$ProvisionTask   = 'ezvpn-windows-provision'
$ProvisionScript = 'C:\provision\ezvpn-windows-provision.ps1'
$ProvisionLog    = 'C:\provision\ezvpn-windows-provision.log'

switch ($Command) {
    'doctor' {
        Info "checking $Target"
        # Each of these is a plain cmd.exe command line; none needs quoting.
        foreach ($probe in 'dotnet --version', 'dotnet --list-sdks', 'dotnet --list-runtimes',
                           'echo DOTNET_ROOT=%DOTNET_ROOT%', 'echo NUGET_PACKAGES=%NUGET_PACKAGES%') {
            Invoke-Remote $probe -IgnoreExitCode
        }
        return
    }

    'clean' {
        $CacheDir = if ($env:EZVPN_WINCI_NUGET_DIR) { $env:EZVPN_WINCI_NUGET_DIR } else { 'C:\ci-cache\nuget' }
        Assert-RemotePath $CacheDir 'EZVPN_WINCI_NUGET_DIR'
        Info "dropping $CacheDir on $Target"
        Invoke-Remote "if exist $CacheDir rmdir /s /q $CacheDir"
        Info 'done'
        return
    }

    'provision' {
        # Deployed and started as a SYSTEM scheduled task rather than run
        # inline, so a dropped ssh connection cannot kill the SDK install half
        # way. provision.ps1 is idempotent, so re-running it is cheap.
        Info "deploying provision.ps1 to $Target"
        & scp -q (Join-Path $PSScriptRoot 'provision.ps1') "${Target}:ezvpn-windows-provision.ps1"
        if ($LASTEXITCODE -ne 0) { throw "scp failed (exit $LASTEXITCODE)" }

        Invoke-RemotePowerShell @"
New-Item -ItemType Directory -Force -Path C:\provision | Out-Null
Move-Item -Force `$env:USERPROFILE\ezvpn-windows-provision.ps1 '$ProvisionScript'
Remove-Item -Force -ErrorAction SilentlyContinue '$ProvisionLog'
`$a  = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File $ProvisionScript'
`$pr = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName '$ProvisionTask' -Action `$a -Principal `$pr -Force | Out-Null
Start-ScheduledTask -TaskName '$ProvisionTask'
"@

        Info "provisioning started; tailing $ProvisionLog"
        $shown = 0
        # Generous: a cold .NET SDK install on a 4 vCPU VM is minutes, and the
        # only cost of waiting is this script's patience.
        $deadline = (Get-Date).AddMinutes(30)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 10
            # The log does not exist for the first second or two, and `type` on
            # a missing file is an error, not a crash — ignore both.
            $lines = @(& ssh $Target "type $ProvisionLog" 2>$null)
            if ($lines.Count -gt $shown) {
                $lines[$shown..($lines.Count - 1)] | ForEach-Object { Write-Host "  $_" }
                $shown = $lines.Count
            }
            $last = if ($lines.Count) { $lines[-1] } else { '' }
            if ($last -match 'DONE-OK')   { Info 'provisioning finished'; return }
            if ($last -match 'DONE-FAIL') { throw "provisioning failed — see $ProvisionLog on $Target" }
        }
        throw "provisioning did not finish within 30 minutes — check $ProvisionLog on $Target"
    }
}

# Stage the whole tree into one archive before anything is sent. Piping tar
# straight into ssh is not an option from PowerShell: a native-to-native
# pipeline is text, not bytes, and it re-encodes — which corrupts the stream.
# Writing a file and handing it to scp keeps the transfer binary-clean, and has
# the side benefit that a half-finished transfer can never be unpacked.
$Archive = Join-Path ([IO.Path]::GetTempPath()) "ezvpn-windows-winci-$PID.tgz"
# Per-run, so two invocations cannot land on each other's upload in the shared
# login home directory.
$RemoteArchive = "ezvpn-windows-winci-src-$PID.tgz"

# The workspace is single, fixed and shared by design. That also means a second
# run starting mid-build would `rmdir /s /q` the tree the first one is
# compiling. Claim the workspace first: mkdir on an existing directory fails,
# and fails atomically, which is all a lock has to do.
$Lock = "${RemoteDir}.lock"
& ssh $Target "mkdir $Lock"
if ($LASTEXITCODE -ne 0) {
    throw "$Target is busy: $Lock already exists. Either another run holds the workspace, or one died holding it — clear it with: ssh $Target rmdir $Lock"
}

try {
    Info "packing $(Split-Path -Leaf $ProjectRoot)"
    # macOS tar stores each file's extended attributes as a second `._name`
    # AppleDouble member. Unpacked on Windows those are ordinary files sitting
    # next to the real ones — and `._Foo.cs` matches the default **\*.cs glob,
    # so the build fails with "is a binary file instead of a text file" on
    # files that do not exist in the repo. The variable is macOS-specific and
    # ignored everywhere else.
    $env:COPYFILE_DISABLE = '1'
    # Everything excluded here is either rebuilt on the VM (bin, obj, publish),
    # re-downloaded there (native\*.dll, staged by native.targets), or useless
    # to it (.git — there is no git on the VM and nothing needs one).
    & tar -C $ProjectRoot `
        --exclude=./.git --exclude=./publish --exclude=./tmp `
        --exclude='*/bin' --exclude='*/obj' --exclude='*/.vs' `
        --exclude='./native/*.dll' --exclude='*.msi' `
        -czf $Archive .
    if ($LASTEXITCODE -ne 0) { throw "tar failed (exit $LASTEXITCODE)" }

    Info "copying to ${Target}:${RemoteDir}"
    # A relative destination lands in the login user's home directory, which
    # sidesteps scp's habit of reading the colon in C:\... as a host separator.
    & scp -q $Archive "${Target}:$RemoteArchive"
    if ($LASTEXITCODE -ne 0) { throw "scp failed (exit $LASTEXITCODE)" }

    # Replace the workspace outright. tar has no --delete, so unpacking over
    # the old tree would leave a file deleted here still sitting there, still
    # getting compiled. The NuGet package cache lives outside the workspace
    # (NUGET_PACKAGES on the VM), so restore stays warm across this.
    Invoke-Remote "if exist $RemoteDir rmdir /s /q $RemoteDir"
    Invoke-Remote "mkdir $RemoteDir"
    Invoke-Remote "tar -xzf %USERPROFILE%\$RemoteArchive -C $RemoteDir"

    if ($Command -eq 'shell') {
        Info "opening a shell on $Target at $RemoteDir"
        Invoke-Remote "cd /d $RemoteDir && cmd" -Tty
        return
    }

    Info "running ci.ps1 on $Target"
    Invoke-Remote "powershell -NoProfile -ExecutionPolicy Bypass -File $CiScript"
}
finally {
    Remove-Item -LiteralPath $Archive -Force -ErrorAction SilentlyContinue
    # Don't leave a copy of the source tree in the login home dir, and never
    # leave the lock behind — a stale one blocks every later run.
    try { & ssh $Target "del %USERPROFILE%\$RemoteArchive & rmdir $Lock" 2>&1 | Out-Null } catch { }
}
