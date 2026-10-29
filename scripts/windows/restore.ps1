. (Join-Path $PSScriptRoot 'environment.ps1')
$Run = New-WindowsDirectory (Join-Path $WindowsTestResults 'windows-restore')
$Results = @()
Push-Location $WindowsAppRoot
try {
    $Projects = @(Get-ChildItem -LiteralPath $WindowsAppRoot -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' } | ForEach-Object FullName | Sort-Object)
    if ($Projects.Count -eq 0) { throw 'Empty native project inventory.' }
    foreach ($Project in $Projects) {
        $null = Assert-WindowsPath -Owner $WindowsAppRoot -Path $Project
        $Log = Join-Path $Run (([IO.Path]::GetFileNameWithoutExtension($Project)) + '.log')
        # Do not import stale package props/targets while recalculating assets.
        # Current projects declare their restore graph without package imports.
        $ExitCode = Invoke-WindowsLoggedTool -Executable $WindowsDotnet -WorkingDirectory $WindowsAppRoot `
            -Arguments @('restore', $Project, '--locked-mode', '--force', '--configfile', $WindowsNuGetConfig,
                '-p:ExcludeRestorePackageImports=true', '--nologo', '-nodeReuse:false') -Log $Log
        $Results += [ordered]@{ project = $Project; exitCode = $ExitCode; log = $Log }
        # Independent projects still restore after a failure. No repair or
        # input mutation is performed between projects.
    }
    $Failures = @($Results | Where-Object { $_['exitCode'] -ne 0 })
    [ordered]@{ projects = $Results; failed = $Failures.Count } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Run 'result.json') -Encoding utf8
    if ($Failures.Count -ne 0) { throw "Windows restore failed. Logs: $Run" }
    Write-Output "Windows locked-restore evidence: $Run"
}
finally { Pop-Location }
