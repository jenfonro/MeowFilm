param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateRange(1, 128)][int]$Jobs = [Math]::Max(1, [Environment]::ProcessorCount),
    [string]$PkgConfig = '',
    [switch]$BaseDependenciesOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$node = (Get-Command node.exe -ErrorAction Stop).Source
$python = (Get-Command python.exe -ErrorAction Stop).Source
$support = Join-Path $PSScriptRoot 'build-film-kernel-support.py'
$driver = Join-Path $PSScriptRoot 'build-film-kernel-driver.mjs'
$lock = Join-Path $root '.cache/windows-film-kernel/build.lock'
$mode = if ($BaseDependenciesOnly) { '--base-only' } else { '--full' }
& $python -B $support lock-run $lock $node $driver $python $Configuration $Jobs $PkgConfig $mode
if ($LASTEXITCODE -ne 0) { throw "Film kernel build failed ($LASTEXITCODE)" }
