param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paths.ps1')
$Source = Join-Path $WindowsRoot 'native\JavaScript'
$Output = Join-Path $Source 'build'
& node (Join-Path $WindowsRoot 'scripts\verify-native-javascript.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Native JavaScript locked-source verification failed.' }
$CMake = Get-Command cmake -ErrorAction SilentlyContinue
if (-not $CMake) { throw 'Native JavaScript requires CMake 3.20+ and a Windows C compiler on PATH.' }
$Arguments = @('-S', $Source, '-B', $Output, "-DCMAKE_BUILD_TYPE=$Configuration")
# Respect CMake's normal toolchain/generator environment. Otherwise use the
# default Visual Studio generator, with the application's win-x64 architecture.
if (-not $env:CMAKE_GENERATOR) { $Arguments += @('-A', 'x64') }
& $CMake.Source @Arguments
if ($LASTEXITCODE -ne 0) { throw 'Native JavaScript CMake configuration failed.' }
& $CMake.Source --build $Output --config $Configuration --target meowfilm_js
if ($LASTEXITCODE -ne 0) { throw 'Native JavaScript compilation failed.' }
$Library = Join-Path $Output "bin\$Configuration\meowfilm_js.dll"
if (-not (Test-Path -LiteralPath $Library -PathType Leaf)) { throw 'Native JavaScript DLL missing.' }
