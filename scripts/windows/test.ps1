param([string[]]$Group, [string]$Case, [switch]$All)
$ErrorActionPreference = 'Stop'
if (([int][bool]$Group + [int][bool]$Case + [int][bool]$All) -ne 1) {
    throw 'Choose -Group <names>, -Case <exact name>, or -All.'
}
. (Join-Path $PSScriptRoot 'environment.ps1')
$Run = New-WindowsDirectory (Join-Path $WindowsTestResults 'windows-native')
foreach ($Name in @('bundles', 'data')) {
    $Path = Assert-WindowsPath -Owner $Run -Path (Join-Path $Run $Name)
    if (Test-Path -LiteralPath $Path) {
        # Windows PowerShell cannot reliably recurse long fixture paths.
        & $WindowsNode -e "require('node:fs').rmSync(process.argv[1], { recursive: true, force: true })" $Path
        if ($LASTEXITCODE -ne 0) { throw 'Native test fixture cleanup failed.' }
    }
    New-WindowsDirectory $Path | Out-Null
}
$env:MEOWFILM_TEST_RUNTIME_ROOT = Join-Path $Run 'bundles'
$env:MEOWFILM_TEST_NODE = $WindowsNode
$env:MEOWFILM_TEST_DATA_ROOT = Join-Path $Run 'data'
$env:MEOWFILM_NATIVE_RECOMMENDATIONS_DATA = Join-Path $env:MEOWFILM_TEST_DATA_ROOT 'native-recommendations'
$Arguments = @()
if ($Group) { $Arguments = @('--group') + $Group }
elseif ($Case) { $Arguments = @('--case', $Case) }
$Result = [ordered]@{ scope = if ($All) { 'all' } else { $Arguments -join ' ' }; status = 'started'; appAcceptance = $false }
Push-Location $WindowsRoot
try {
    $Exit = Invoke-WindowsLoggedTool -Executable $WindowsNode -Arguments @(
        (Join-Path $PSScriptRoot 'prepare-test-bundles.mjs'), $env:MEOWFILM_TEST_RUNTIME_ROOT) -Log (Join-Path $Run 'bundles.log')
    if ($Exit -ne 0) { throw 'Runtime test fixture packaging failed.' }
    $Project = Join-Path $WindowsAppRoot 'tests\MeowFilm.Windows.Tests'
    $Exit = Invoke-WindowsLoggedTool -Executable $WindowsDotnet -WorkingDirectory $WindowsAppRoot `
        -Arguments @('restore', $Project, '--locked-mode', '--configfile', $WindowsNuGetConfig) -Log (Join-Path $Run 'restore.log')
    if ($Exit -ne 0) { throw 'Windows test restore failed.' }
    $Exit = Invoke-WindowsLoggedTool -Executable $WindowsDotnet -WorkingDirectory $WindowsAppRoot `
        -Arguments @('build', $Project, '--configuration', 'Release', '--no-restore', '--nologo', '-nodeReuse:false') -Log (Join-Path $Run 'build.log')
    if ($Exit -ne 0) { throw 'Windows test compilation failed.' }
    $TestAssembly = Join-Path $Project 'bin\Release\net10.0-windows10.0.19041.0\MeowFilm.Windows.Tests.dll'
    $Exit = Invoke-WindowsLoggedTool -Executable $WindowsDotnet -Arguments (@($TestAssembly) + $Arguments) -Log (Join-Path $Run 'native.log')
    $Result['exitCode'] = $Exit
    $Summary = @(Select-String -LiteralPath (Join-Path $Run 'native.log') -Pattern '^TOTAL (\d+); PASSED (\d+); FAILED (\d+); SKIPPED (\d+)$')
    if ($Summary.Count -ne 1) { throw 'Missing or duplicate native test summary.' }
    $Result['tests'] = [int]$Summary[0].Matches[0].Groups[1].Value
    $Result['passed'] = [int]$Summary[0].Matches[0].Groups[2].Value
    $Result['failed'] = [int]$Summary[0].Matches[0].Groups[3].Value
    $Result['skipped'] = [int]$Summary[0].Matches[0].Groups[4].Value
    if ($Exit -ne 0 -or $Result.tests -lt 1 -or $Result.passed -ne $Result.tests -or $Result.failed -ne 0 -or $Result.skipped -ne 0) {
        throw 'Selected native tests failed or were incomplete.'
    }
    $Result.status = 'passed'
}
catch { $Result.status = 'failed'; throw }
finally {
    $Result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'result.json') -Encoding utf8
    Pop-Location
}
