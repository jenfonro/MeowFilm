function Resolve-WindowsInstallerCompiler {
    $Command = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($Command) { return $Command.Source }
    foreach ($Directory in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if ($Directory) {
            $Path = Join-Path $Directory 'Inno Setup 6\ISCC.exe'
            if (Test-Path -LiteralPath $Path -PathType Leaf) { return $Path }
        }
    }
    throw 'Inno Setup 6 is required only for -Installer. Install it or put ISCC.exe on PATH.'
}

function New-WindowsInstaller {
    param(
        [Parameter(Mandatory)][string]$Payload,
        [Parameter(Mandatory)][string]$Compiler
    )
    $ErrorActionPreference = 'Stop'
    $Payload = Assert-WindowsPath -Owner $WindowsTmpRoot -Path $Payload
    $Stage = Assert-WindowsPath -Owner $WindowsTmpRoot -Path (Join-Path $WindowsTmpRoot 'windows-installer')
    if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
    New-WindowsDirectory $Stage | Out-Null
    & $WindowsNode (Join-Path $PSScriptRoot 'prepare-installer.mjs') --payload $Payload --output $Stage | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Installer payload validation failed (exit $LASTEXITCODE); the previous installer was retained." }
    $Icon = Join-Path $WindowsAppRoot 'app\Assets\AppIcon.ico'
    if (-not (Test-Path -LiteralPath $Icon -PathType Leaf)) { throw 'Windows installer icon is missing: apps/windows/app/Assets/AppIcon.ico.' }
    $Version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Payload 'meowfilm.exe'))
    if (-not $Version.ProductVersion -or -not $Version.FileVersion) { throw 'Published meowfilm.exe has no product/file version.' }
    $NumericFileVersion = "$($Version.FileMajorPart).$($Version.FileMinorPart).$($Version.FileBuildPart).$($Version.FilePrivatePart)"
    $NumericProductVersion = "$($Version.ProductMajorPart).$($Version.ProductMinorPart).$($Version.ProductBuildPart).$($Version.ProductPrivatePart)"
    & $Compiler /Qp "/DPayloadDir=$Payload" "/DStagingDir=$Stage" "/DAppIcon=$Icon" "/DProductNumericVersion=$NumericProductVersion" `
        "/O$Stage" /Fmeowfilm-setup (Join-Path $PSScriptRoot 'installer.iss') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit $LASTEXITCODE); the previous installer was retained." }
    $Candidate = Assert-WindowsPath -Owner $Stage -Path (Join-Path $Stage 'meowfilm-setup.exe')
    if (-not (Test-Path -LiteralPath $Candidate -PathType Leaf) -or (Get-Item -LiteralPath $Candidate).Length -eq 0) {
        throw 'Inno Setup did not produce meowfilm-setup.exe; the previous installer was retained.'
    }
    $InstallerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Candidate)
    foreach ($Property in @('ProductMajorPart', 'ProductMinorPart', 'ProductBuildPart', 'ProductPrivatePart',
        'FileMajorPart', 'FileMinorPart', 'FileBuildPart', 'FilePrivatePart')) {
        if ($InstallerVersion.$Property -ne $Version.$Property) {
            throw "Compiled installer $Property differs from the published EXE; the previous installer was retained."
        }
    }
    $TextVersions = @{ ProductVersion = $NumericProductVersion; FileVersion = $NumericFileVersion }
    foreach ($Property in $TextVersions.Keys) {
        if (([string]$InstallerVersion.$Property).Trim() -ne $TextVersions[$Property]) {
            throw "Compiled installer $Property differs from the published EXE numeric version; the previous installer was retained."
        }
    }
    return $Candidate
}

function Publish-WindowsInstaller {
    param([Parameter(Mandatory)][string]$Candidate)
    $ErrorActionPreference = 'Stop'
    $Candidate = Assert-WindowsPath -Owner (Join-Path $WindowsTmpRoot 'windows-installer') -Path $Candidate
    if (-not (Test-Path -LiteralPath $Candidate -PathType Leaf) -or (Get-Item -LiteralPath $Candidate).Length -eq 0) {
        throw 'The completed installer candidate is missing; the previous installer was retained.'
    }
    $Directory = New-WindowsDirectory (Join-Path $WindowsRoot 'build\windows-installer') -Owner $WindowsRoot
    $Output = Assert-WindowsPath -Owner $Directory -Path (Join-Path $Directory 'meowfilm-setup.exe')
    if (Test-Path -LiteralPath $Output) { [IO.File]::Replace($Candidate, $Output, [NullString]::Value) }
    else { [IO.File]::Move($Candidate, $Output) }
}
