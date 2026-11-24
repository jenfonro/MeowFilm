# Developer workspace paths only; never an application's data-directory policy.
function Assert-WindowsPath {
    param([Parameter(Mandatory)][string]$Owner, [Parameter(Mandatory)][string]$Path, [switch]$AllowRoot)
    foreach ($Value in @($Owner, $Path)) {
        if ($Value -notmatch '^[A-Za-z]:[\\/][^:]*$') {
            throw 'Windows development paths must be absolute local paths, without alternate data streams.'
        }
    }
    $Base = [IO.Path]::GetFullPath($Owner).TrimEnd([char[]]"\/")
    $Full = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]"\/")
    if (-not (($AllowRoot -and $Full.Equals($Base, [StringComparison]::OrdinalIgnoreCase)) -or
        $Full.StartsWith($Base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Development path leaves its Windows owner: $Full"
    }
    return $Full
}

function New-WindowsDirectory {
    param([Parameter(Mandatory)][string]$Path, [string]$Owner = $WindowsTmpRoot)
    $Full = Assert-WindowsPath -Owner $Owner -Path $Path
    [IO.Directory]::CreateDirectory($Full) | Out-Null
    return $Full
}

$WindowsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$WindowsAppRoot = Join-Path $WindowsRoot 'apps\windows'
$WindowsBuildRoot = Join-Path $WindowsRoot 'build\windows'
$WindowsTmpRoot = Join-Path $WindowsRoot 'tmp'
$WindowsTestResults = Join-Path $WindowsTmpRoot 'test-results'
$WindowsNuGetConfig = Join-Path $WindowsAppRoot 'NuGet.Config'
$null = Assert-WindowsPath -Owner $WindowsRoot -Path $WindowsRoot -AllowRoot
$null = Assert-WindowsPath -Owner $WindowsRoot -Path $WindowsBuildRoot
$null = Assert-WindowsPath -Owner $WindowsRoot -Path $WindowsTmpRoot
