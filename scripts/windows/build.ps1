param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$FrameworkDependent
)
. (Join-Path $PSScriptRoot 'paths.ps1')
New-WindowsDirectory $WindowsTmpRoot -Owner $WindowsRoot | Out-Null
$LockPath = Assert-WindowsPath -Owner $WindowsTmpRoot -Path (Join-Path $WindowsTmpRoot 'build.lock')
try { $BuildLock = [IO.File]::Open($LockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw 'Another Windows build is using this checkout. Wait for it to finish.' }
Push-Location $WindowsAppRoot
try {
    & (Join-Path $PSScriptRoot 'bootstrap.ps1')
    . (Join-Path $PSScriptRoot 'environment.ps1')
    . (Join-Path $PSScriptRoot 'install.ps1')
    $NodeRuntime = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'node-runtime.json') -Raw | ConvertFrom-Json
    $DataIdentity = 'aggregate' # Current wire/data identity, not a product selector.
    $Project = Join-Path $WindowsAppRoot 'app\MeowFilm.Windows.App.csproj'
    if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) { throw 'The native Windows App entry is missing.' }
    & $WindowsNode (Join-Path $PSScriptRoot 'verify-app-structure.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Single-App source/identity/resource boundary check failed.' }
    $InputHash = & $WindowsNode (Join-Path $PSScriptRoot 'input-fingerprint.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Input inventory failed.' }
    # The shipped Node runtime is product content bound to LICENSES/node-LICENSE.txt.
    if ((Get-FileHash -LiteralPath $WindowsNode -Algorithm SHA256).Hash.ToLowerInvariant() -ne $NodeRuntime.sha256) {
        throw "The bundled Node runtime is locked to the official win-x64 node.exe v$($NodeRuntime.version); put it first on PATH."
    }
    $SelfContained = (-not $FrameworkDependent).ToString().ToLowerInvariant()
    $Rid = 'win-x64'
    & $WindowsDotnet restore $Project --locked-mode --configfile $WindowsNuGetConfig --runtime $Rid "-p:SelfContained=$SelfContained" -p:TargetLatestRuntimePatch=true -p:ExcludeRestorePackageImports=true
    if ($LASTEXITCODE -ne 0) { throw 'Locked Windows App restore failed.' }
    $Output = Assert-WindowsPath -Owner $WindowsTmpRoot -Path (Join-Path $WindowsTmpRoot 'publish')
    if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
    New-WindowsDirectory $Output | Out-Null
    # Use MSBuild's resolved publish closure, not a recursive copy of a reused
    # bin directory that can retain assemblies from a previously enabled module.
    # Only a complete checked staging tree replaces the fixed local installation.
    & $WindowsDotnet publish $Project --configuration $Configuration --no-restore --runtime $Rid `
        "--self-contained" $SelfContained -p:TargetLatestRuntimePatch=true `
        -p:VlcWindowsX64Enabled=true -p:VlcWindowsX86Enabled=false -p:VlcWindowsArm64Enabled=false `
        --output $Output --nologo -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Windows compilation/local packaging failed.' }
    $Runtime = Join-Path $Output 'runtime'
    & $WindowsNode (Join-Path $PSScriptRoot 'package-runtime.mjs') --product $DataIdentity --output $Runtime
    if ($LASTEXITCODE -ne 0) { throw 'Runtime packaging failed.' }
    Copy-Item -LiteralPath $WindowsNode -Destination (Join-Path $Runtime 'node.exe')
    $NodeLicense = Join-Path $WindowsRoot 'LICENSES\node-LICENSE.txt'
    if ((Get-FileHash -LiteralPath $NodeLicense -Algorithm SHA256).Hash.ToLowerInvariant() -ne $NodeRuntime.licenseSha256) {
        throw 'Node license hash mismatch.'
    }
    Copy-Item -LiteralPath $NodeLicense -Destination (Join-Path $Runtime 'node-LICENSE.txt')
    $Licenses = Join-Path $Output 'licenses'
    & $WindowsNode (Join-Path $PSScriptRoot 'package-licenses.mjs') --output $Licenses
    if ($LASTEXITCODE -ne 0) { throw 'License packaging failed.' }
    $LicensesHash = (Get-FileHash -LiteralPath (Join-Path $Licenses 'licenses-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $ManifestPath = Join-Path $Runtime 'runtime-manifest.json'
    $Manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $Manifest.files = @($Manifest.files) + @([pscustomobject]@{
        path = 'node.exe'; bytes = (Get-Item -LiteralPath $WindowsNode).Length; sha256 = $NodeRuntime.sha256
    }) + @([pscustomobject]@{
        path = 'node-LICENSE.txt'; bytes = (Get-Item -LiteralPath $NodeLicense).Length; sha256 = $NodeRuntime.licenseSha256
    })
    $Manifest | Add-Member -NotePropertyName node -NotePropertyValue $NodeRuntime
    [System.IO.File]::WriteAllText($ManifestPath, ($Manifest | ConvertTo-Json -Depth 50) + "`n", [System.Text.UTF8Encoding]::new($false))
    & $WindowsNode (Join-Path $PSScriptRoot 'verify-projects.mjs') --build $Output --identity $DataIdentity $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Published native dependency closure verification failed.' }
    if (-not $FrameworkDependent -and
        (-not (Test-Path -LiteralPath (Join-Path $Output 'coreclr.dll')) -or
         -not (Test-Path -LiteralPath (Join-Path $Output 'PresentationFramework.dll')))) {
        throw 'Self-contained desktop runtime is missing.'
    }
    $RuntimeConfig = Get-Content -LiteralPath (Join-Path $Output 'meowfilm.runtimeconfig.json') -Raw | ConvertFrom-Json
    $Frameworks = if ($FrameworkDependent) { $RuntimeConfig.runtimeOptions.frameworks } else { $RuntimeConfig.runtimeOptions.includedFrameworks }
    $RuntimeVersion = @($Frameworks)[0].version
    if (@($Frameworks).Count -lt 2 -or
        @($Frameworks | Where-Object { $_.version -ne $RuntimeVersion }).Count -ne 0) {
        throw 'Published desktop runtime frameworks are incomplete or use different versions.'
    }
    $FinalHash = & $WindowsNode (Join-Path $PSScriptRoot 'input-fingerprint.mjs')
    if ($LASTEXITCODE -ne 0 -or $FinalHash -ne $InputHash) { throw 'Code inputs changed during this build; output is not a stable candidate.' }
    $Result = [ordered]@{
        schemaVersion = 1; application = 'meowfilm'; dataIdentity = $DataIdentity; configuration = $Configuration
        sdk = (& $WindowsDotnet --version); node = $NodeRuntime.version
        codeInputSha256 = $InputHash
        gitHead = (& $WindowsGit -C $WindowsRoot rev-parse HEAD)
        createdAt = (Get-Date -Format o)
        output = $WindowsBuildRoot
        status = 'built-not-accepted'
        selfContained = (-not $FrameworkDependent)
        runtimeVersion = $RuntimeVersion
        runtimeIdentifier = $Rid
        licensesManifestSha256 = $LicensesHash
        redistributionPermissionVerified = $false
    }
    $Result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'build-result.json') -Encoding utf8
    Install-WindowsBuild
    $Result | ConvertTo-Json -Depth 5
}
finally { Pop-Location; $BuildLock.Dispose() }
