# Prepares locked dependencies and the common native runtime with its default caches.
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $WindowsRoot
try {
    & $WindowsNode (Join-Path $WindowsRoot 'scripts\verify-dependency-lock.mjs')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $WindowsNpm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $WindowsNode (Join-Path $PSScriptRoot 'ensure-runtime-native.mjs')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally { Pop-Location }
