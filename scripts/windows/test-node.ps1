param([string[]]$Path, [switch]$All)
$ErrorActionPreference = 'Stop'
if (([int][bool]$Path + [int][bool]$All) -ne 1) { throw 'Choose -Path <test files> or -All.' }
. (Join-Path $PSScriptRoot 'environment.ps1')
$Run = New-WindowsDirectory (Join-Path $WindowsTestResults 'windows-node')
if (-not $All) {
    $Files = @($Path | ForEach-Object {
        $Target = if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $WindowsRoot $_ }
        $File = Assert-WindowsPath -Owner $WindowsRoot -Path ([IO.Path]::GetFullPath($Target))
        if ($File -notmatch '\.test\.[cm]?js$' -or -not (Test-Path -LiteralPath $File -PathType Leaf)) {
            throw "Not a test file: $_"
        }
        $File
    })
}
$Exit = Invoke-WindowsLoggedTool -Executable $WindowsNode `
    -Arguments @((Join-Path $PSScriptRoot 'ensure-runtime-native.mjs')) -Log (Join-Path $Run 'runtime-native.log')
if ($Exit -ne 0) { exit $Exit }
if (-not $All) {
    $Exit = Invoke-WindowsLoggedTool -Executable $WindowsNode `
        -Arguments (@((Join-Path $WindowsRoot 'scripts\run-tests.mjs')) + $Files) -Log (Join-Path $Run 'selected.log')
    if ($Exit -ne 0) { exit $Exit }
    return
}
$FailedExit = 0
foreach ($Stage in @('verify-dependency-lock', 'test-packages', 'test-root', 'verify-boundaries')) {
    $Arguments = @((Join-Path $WindowsRoot "scripts\$Stage.mjs"))
    if ($Stage -eq 'test-root') { $Arguments += @('--platform', 'windows') }
    $Exit = Invoke-WindowsLoggedTool -Executable $WindowsNode -Arguments $Arguments `
        -Log (Join-Path $Run ($Stage + '.log'))
    if ($Exit -ne 0 -and $FailedExit -eq 0) { $FailedExit = $Exit }
    if ($Stage -eq 'verify-dependency-lock' -and $FailedExit -ne 0) { exit $FailedExit }
}
if ($FailedExit -ne 0) { exit $FailedExit }
