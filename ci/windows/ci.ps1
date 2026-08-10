# The steps from .github/workflows/windows-ci.yml, in the same order, with the
# same flags. When this file and that workflow disagree, the workflow is right
# and this is stale — it exists to say what CI will say before CI is asked.
#
# This runs natively on whatever Windows machine it is invoked on: the CI VM
# (see ci\windows\remote.ps1) or a dev box. It installs nothing and changes no
# machine state, so running it locally is safe.
#
# Windows PowerShell 5.1 does not fail on a non-zero exit from a native
# command, whatever $ErrorActionPreference says, so every step checks
# $LASTEXITCODE by hand.
$ErrorActionPreference = 'Stop'

# Invoked over ssh the working directory is the login user's home, not the
# checkout, so anchor to the repo root this script sits in.
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $Root

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    Write-Host ''
    Write-Host "== $Name =="
    Write-Host "   dotnet $($Arguments -join ' ')"

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host "FAILED: $Name (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

Write-Host "== toolchain =="
& dotnet --version
& dotnet --list-sdks
& dotnet --list-runtimes
if ($env:NUGET_PACKAGES) { Write-Host "   NUGET_PACKAGES=$env:NUGET_PACKAGES" }

# The native ezvpn.dll / wintun.dll are runtime-only (P/Invoke), so the
# solution compiles and the Core tests run without them.
Invoke-Step 'Build solution' @('build', 'ezvpn-windows.slnx', '-c', 'Release')

# net8.0 test project: this is the step that needs the .NET 8 runtime, not just
# the 10 SDK.
Invoke-Step 'Test' @('test', 'tests\Ezvpn.Core.Tests\Ezvpn.Core.Tests.csproj', '-c', 'Release', '--no-build')

# Publish is where native.targets *requires* both DLLs (pinned ezvpn release +
# wintun.net, SHA256-verified), so it is the step that catches a bad pin or a
# broken download — a plain build only warns. It also needs outbound network.
Invoke-Step 'Publish app (self-contained)' `
    @('publish', 'src\Ezvpn.App\Ezvpn.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', '-o', 'publish')

foreach ($dll in 'ezvpn.dll', 'wintun.dll') {
    if (-not (Test-Path (Join-Path $Root "publish\$dll"))) {
        Write-Host ''
        Write-Host "FAILED: $dll missing from publish"
        exit 1
    }
}

# Must stay in step with release.yml and the MSI step of windows-ci.yml, or
# this stops building what users download. ProductVersion is left at the
# project default: nothing is released from here, so the upgrade-comparison
# value does not matter.
Invoke-Step 'Build MSI' `
    @('build', 'installer\Ezvpn.Installer.wixproj', '-c', 'Release', "-p:PublishDir=$Root\publish")

Write-Host ''
Write-Host 'all steps passed'
