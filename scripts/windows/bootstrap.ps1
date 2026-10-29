# Installs the locked Node workspace dependencies with the developer's npm.
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $WindowsRoot
try {
    & $WindowsNode (Join-Path $WindowsRoot 'scripts\verify-dependency-lock.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Dependency lock validation failed.' }
    & $WindowsNpm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
}
finally { Pop-Location }
