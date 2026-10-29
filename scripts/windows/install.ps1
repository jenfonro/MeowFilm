function Install-WindowsBuild {
    $ErrorActionPreference = 'Stop'
    $Staged = Assert-WindowsPath -Owner $WindowsTmpRoot -Path (Join-Path $WindowsTmpRoot 'publish')
    $Output = Assert-WindowsPath -Owner $WindowsRoot -Path $WindowsBuildRoot
    $Previous = Assert-WindowsPath -Owner $WindowsTmpRoot -Path (Join-Path $WindowsTmpRoot 'previous-build')
    if (-not (Test-Path -LiteralPath (Join-Path $Staged 'meowfilm.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $Staged 'build-result.json') -PathType Leaf)) {
        throw 'The checked Windows staging output is missing.'
    }
    if (Test-Path -LiteralPath $Previous) { Remove-Item -LiteralPath $Previous -Recurse -Force }
    foreach ($Process in @(Get-CimInstance Win32_Process -Filter "Name='meowfilm.exe'")) {
        if (-not $Process.ExecutablePath -or
            $Process.ExecutablePath.StartsWith($Output + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close the App running from build before updating its installation.'
        }
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Output)) | Out-Null
    $HadOutput = Test-Path -LiteralPath $Output
    if ($HadOutput) { Move-Item -LiteralPath $Output -Destination $Previous }
    try { Move-Item -LiteralPath $Staged -Destination $Output }
    catch {
        if ($HadOutput) { Move-Item -LiteralPath $Previous -Destination $Output }
        throw
    }
    if ($HadOutput) {
        $Previous = Assert-WindowsPath -Owner $WindowsTmpRoot -Path $Previous
        Remove-Item -LiteralPath $Previous -Recurse -Force
    }
}
