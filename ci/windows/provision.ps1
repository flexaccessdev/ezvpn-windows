# Turn a bare Windows Server install into the ezvpn-windows CI box.
#
# Runs *on the VM*, elevated. `ci\windows\remote.ps1 provision` deploys it to
# C:\provision and starts it as a SYSTEM scheduled task rather than running it
# inline over ssh, so a dropped connection cannot kill an installer half way.
# By hand that is:
#
#   $a  = New-ScheduledTaskAction -Execute 'powershell.exe' `
#           -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\provision\ezvpn-windows-provision.ps1'
#   $pr = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
#   Register-ScheduledTask -TaskName 'ezvpn-windows-provision' -Action $a -Principal $pr -Force
#   Start-ScheduledTask -TaskName 'ezvpn-windows-provision'
#
# Every step is guarded, so re-running it after adding a step is a no-op for
# everything already installed. Progress goes to
# C:\provision\ezvpn-windows-provision.log; the last line is DONE-OK or
# DONE-FAIL. (The log and task name are prefixed because this VM is shared with
# wrustic's provisioner, which owns C:\provision\provision.log.)
#
# See docs/windows-vm-ci.md.
$ErrorActionPreference = 'Stop'

# Everything is machine-scoped, never per-profile. This script runs as SYSTEM
# and ssh logs in as Administrator; a per-profile install would land in
# SYSTEM's profile and be invisible to every CI run.
$DotnetRoot = 'C:\dotnet'
$Log        = 'C:\provision\ezvpn-windows-provision.log'

function Log($m) { "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m | Tee-Object -FilePath $Log -Append }

function Add-MachinePath($dir) {
    $p = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if ($p -notlike "*$dir*") {
        [Environment]::SetEnvironmentVariable('Path', "$p;$dir", 'Machine')
        Log "added $dir to machine PATH"
    }
    # The running process needs it too — later steps in this script use it.
    if ($env:Path -notlike "*$dir*") { $env:Path = "$env:Path;$dir" }
}

function Set-MachineEnv($name, $value) {
    if ([Environment]::GetEnvironmentVariable($name, 'Machine') -ne $value) {
        [Environment]::SetEnvironmentVariable($name, $value, 'Machine')
        Log "set $name=$value (machine)"
    }
    Set-Item -Path "env:$name" -Value $value
}

function Test-DotnetComponent($relativeGlob) {
    (Test-Path (Join-Path $DotnetRoot $relativeGlob))
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Force -Path C:\provision | Out-Null

    # --- dotnet-install.ps1 ------------------------------------------------
    # Microsoft's own installer script. Used instead of the standalone
    # installer .exe because it is side-by-side friendly, needs no MSI, and
    # takes an explicit -InstallDir so nothing lands in a user profile.
    $Installer = 'C:\provision\dotnet-install.ps1'
    Log 'downloading dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $Installer -UseBasicParsing

    # --- .NET 10 SDK -------------------------------------------------------
    # Builds everything: Ezvpn.Core (net8.0), Ezvpn.App (net10.0-windows…,
    # WinUI 3) and the WiX 5 installer project. WinUI needs no VS workload —
    # the Windows App SDK and the Windows SDK ref pack both come from NuGet.
    # dotnet-install.ps1 is a PowerShell script, not an .exe: it reports failure
    # by throwing (which $ErrorActionPreference='Stop' turns into ours) and
    # leaves $LASTEXITCODE alone, so checking that would be checking whatever
    # native command ran last. Confirm the install by looking for it instead.
    if (Test-DotnetComponent 'sdk\10.*') {
        Log '.NET 10 SDK already present, skipping'
    } else {
        Log 'installing .NET 10 SDK (long)'
        & $Installer -Channel '10.0' -InstallDir $DotnetRoot -NoPath
        if (-not (Test-DotnetComponent 'sdk\10.*')) { throw "dotnet-install left no 10.x SDK under $DotnetRoot" }
    }

    # --- .NET 8 runtime ----------------------------------------------------
    # The Core tests target net8.0, so `dotnet test` needs the 8 runtime to
    # *run* them — the 10 SDK alone builds them and then refuses to launch.
    # windows-latest preinstalls it, which is exactly the shape of a
    # works-on-CI-only difference, so install it here too.
    if (Test-DotnetComponent 'shared\Microsoft.NETCore.App\8.*') {
        Log '.NET 8 runtime already present, skipping'
    } else {
        Log 'installing .NET 8 runtime'
        & $Installer -Channel '8.0' -Runtime 'dotnet' -InstallDir $DotnetRoot -NoPath
        if (-not (Test-DotnetComponent 'shared\Microsoft.NETCore.App\8.*')) { throw "dotnet-install left no 8.x runtime under $DotnetRoot" }
    }

    # --- Machine-wide dotnet environment -----------------------------------
    Add-MachinePath $DotnetRoot
    Set-MachineEnv 'DOTNET_ROOT' $DotnetRoot
    Set-MachineEnv 'DOTNET_CLI_TELEMETRY_OPTOUT' '1'
    Set-MachineEnv 'DOTNET_NOLOGO' '1'

    # NuGet's package cache outside the workspace: remote.ps1 replaces the
    # workspace directory outright on every run, so anything cached inside it
    # is thrown away. This is what makes a warm run restore from disk instead
    # of from nuget.org. (bin/obj still go with the workspace, so every run is
    # a full compile — it is the restore that is worth keeping.)
    New-Item -ItemType Directory -Force -Path 'C:\ci-cache\nuget', 'C:\ci-workspaces' | Out-Null
    Set-MachineEnv 'NUGET_PACKAGES' 'C:\ci-cache\nuget'

    Log "dotnet: $(& $DotnetRoot\dotnet.exe --version)"

    # sshd captured its environment when the service started, and children
    # inherit that block — so machine PATH changes are invisible over ssh until
    # it restarts. Nothing above takes effect for CI runs without this.
    Log 'restarting sshd so ssh sessions see the new machine environment'
    Restart-Service sshd

    Log 'DONE-OK'
} catch {
    Log "DONE-FAIL $($_.Exception.Message)"
    # The log is for a human; the exit code is for the scheduler. Without it
    # the task records success and `Get-ScheduledTaskInfo` says 0 on a failed
    # provision.
    exit 1
}
