$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paths.ps1')

# Build and test tools come from the developer's PATH; no version is pinned here.
function Resolve-WindowsTool([string]$Name) {
    $Command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $Command) { throw "Windows development requires $Name on PATH." }
    return $Command.Source
}
$WindowsDotnet = Resolve-WindowsTool 'dotnet.exe'
$WindowsNode = Resolve-WindowsTool 'node.exe'
$WindowsNpm = Resolve-WindowsTool 'npm.cmd'
$WindowsGit = Resolve-WindowsTool 'git.exe'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)

function Invoke-WindowsLoggedTool {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$Log,
        [string]$WorkingDirectory = $WindowsRoot
    )
    $null = Assert-WindowsPath -Owner $WindowsTmpRoot -Path $Log
    $null = Assert-WindowsPath -Owner $WindowsRoot -Path $WorkingDirectory -AllowRoot
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Logged executable is missing.' }
    $SavedPreference = $ErrorActionPreference
    Push-Location $WorkingDirectory
    try {
        # Windows PowerShell 5 turns redirected native stderr into error records.
        # A diagnostic must not terminate a test group before it can summarize.
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments 2>&1 | Tee-Object -FilePath $Log -ErrorAction Stop | Out-Host
        $ExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $SavedPreference
        Pop-Location
    }
    return [int]$ExitCode
}
