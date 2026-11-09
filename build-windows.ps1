param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$FrameworkDependent,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts\windows\build.ps1') @PSBoundParameters
exit $LASTEXITCODE
