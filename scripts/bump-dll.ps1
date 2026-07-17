<#
.SYNOPSIS
  Pin native\native.targets to an ezvpn release's ezvpn-windows.dll.zip asset.

.DESCRIPTION
  The Windows analog of ezvpn-apple's scripts/bump-xcframework.sh. Downloads the
  release's ezvpn-windows.dll.zip, computes its SHA256, and rewrites the
  EzvpnReleaseTag + EzvpnDllZipSha256 in native\native.targets so the app build
  downloads that exact DLL by default (no local core build needed). wintun.dll is
  pinned separately in native.targets and is not touched here.

.PARAMETER Tag
  Release tag to pin (e.g. v0.0.21). Defaults to the latest ezvpn release
  (requires the gh CLI to resolve).

.EXAMPLE
  ./scripts/bump-dll.ps1 v0.0.21
  ./scripts/bump-dll.ps1            # latest release
#>
[CmdletBinding()]
param(
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$Repo = 'flexaccessdev/ezvpn'
$Asset = 'ezvpn-windows.dll.zip'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Targets = Join-Path $ScriptDir '..\native\native.targets'

if (-not (Test-Path $Targets)) { throw "native.targets not found at $Targets" }

if (-not $Tag) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "no tag given and gh CLI not installed to resolve the latest release"
    }
    $Tag = (gh release view --repo $Repo --json tagName --jq .tagName).Trim()
    if (-not $Tag) { throw "could not resolve the latest release tag" }
}

$Url = "https://github.com/$Repo/releases/download/$Tag/$Asset"
$Tmp = Join-Path ([System.IO.Path]::GetTempPath()) "ezvpn-bump-$([System.IO.Path]::GetRandomFileName()).zip"

Write-Host "Downloading $Url ..."
$ProgressPreference = 'SilentlyContinue'
try {
    Invoke-WebRequest -Uri $Url -OutFile $Tmp -ErrorAction Stop
    $Sha = (Get-FileHash $Tmp -Algorithm SHA256).Hash.ToLower()
} finally {
    Remove-Item $Tmp -Force -ErrorAction SilentlyContinue
}

$content = Get-Content $Targets -Raw
$content = $content -replace '(?s)(<EzvpnReleaseTag\b[^>]*>).*?(</EzvpnReleaseTag>)', "`${1}$Tag`${2}"
$content = $content -replace '<EzvpnDllZipSha256>[^<]*</EzvpnDllZipSha256>', "<EzvpnDllZipSha256>$Sha</EzvpnDllZipSha256>"

if ($content -notmatch [regex]::Escape(">$Tag</EzvpnReleaseTag>")) {
    throw "failed to rewrite EzvpnReleaseTag in $Targets"
}
if ($content -notmatch [regex]::Escape("<EzvpnDllZipSha256>$Sha</EzvpnDllZipSha256>")) {
    throw "failed to rewrite EzvpnDllZipSha256 in $Targets"
}

Set-Content -Path $Targets -Value $content -NoNewline

Write-Host "Pinned native.targets:"
Write-Host "  tag:      $Tag"
Write-Host "  checksum: $Sha"
Write-Host "  url:      $Url"
