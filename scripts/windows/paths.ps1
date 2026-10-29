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
    # Get attributes rather than Test-Path: a dangling link is still a reparse
    # point and must not become a writable alias after its target is recreated.
    $Current = [IO.Path]::GetPathRoot($Full)
    foreach ($Part in $Full.Substring($Current.Length).Split([char[]]"\/", [StringSplitOptions]::RemoveEmptyEntries)) {
        $Current = Join-Path $Current $Part
        try { $Attributes = [IO.File]::GetAttributes($Current) }
        catch [IO.FileNotFoundException] { break }
        catch [IO.DirectoryNotFoundException] { break }
        if ($Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Development path traverses a link or junction: $Current"
        }
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
