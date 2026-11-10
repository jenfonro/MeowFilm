param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paths.ps1')

function Tool([string]$Name) {
    $Command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $Command) { throw "Private video-kernel build requires $Name." }
    return $Command.Source
}
function Run([string]$Executable, [string[]]$Arguments) {
    $Saved = $ErrorActionPreference
    try { $ErrorActionPreference = 'Continue'; & $Executable @Arguments; $Code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $Saved }
    if ($Code -ne 0) { Write-Output "video_kernel_tool_exit=$Code"; exit $Code }
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Save([string]$Path, [string]$Text) {
    if (-not (Test-Path -LiteralPath $Path) -or [IO.File]::ReadAllText($Path) -cne $Text) {
        [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
    }
}

$Node = Tool 'node.exe'; $Gcc = Tool 'gcc.exe'; $CMake = Tool 'cmake.exe'; $Ninja = Tool 'ninja.exe'
$Perl = Tool 'perl.exe'; $Git = Tool 'git.exe'; $Objdump = Tool 'objdump.exe'
$Make = Get-Command gmake.exe,mingw32-make.exe,make.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $Make) { throw 'Private FFmpeg build requires GNU make on PATH.' }
$Bash = Join-Path (Split-Path (Split-Path $Git)) 'bin\bash.exe'
if (-not (Test-Path -LiteralPath $Bash)) { throw 'The installed Git Bash is required for the locked FFmpeg configure.' }
$PkgConfig = Join-Path (Split-Path $Perl) 'pkg-config'
if (-not (Test-Path -LiteralPath $PkgConfig)) { throw 'The installed Perl pkg-config implementation is required.' }
$SevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
if (-not (Test-Path -LiteralPath $SevenZip)) { $SevenZip = Tool '7z.exe' }
$Triple = & $Gcc -dumpmachine
if ($LASTEXITCODE -ne 0 -or $Triple.Trim() -ne 'x86_64-w64-mingw32') { throw 'The VLC 3.0.24 SDK requires x86_64-w64-mingw32.' }
$Winpthread = ((& $Gcc '-print-file-name=libwinpthread.a') -join '').Trim()
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Winpthread -PathType Leaf)) {
    throw 'The private demux build requires the compiler toolchain static libwinpthread.a.'
}
$PreparedJson = & $Node (Join-Path $PSScriptRoot 'prepare-video-kernel.mjs') $SevenZip
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$Prepared = $PreparedJson | ConvertFrom-Json
$Source = $Prepared.source; $Cache = $Prepared.cache
$Build = Join-Path $Cache 'build'
$XmlPrefix = Join-Path $Cache 'install\libxml2'
$FFmpegPrefix = Join-Path $Cache 'install\ffmpeg'
$Output = Join-Path $Cache 'output'
[IO.Directory]::CreateDirectory($Output) | Out-Null
[IO.Directory]::CreateDirectory($Build) | Out-Null
$Inputs = [ordered]@{
    architecture = 'win-x64'; configuration = $Configuration; abi = $Prepared.lock.vlcPluginAbi
    prepared = $Prepared.prepared
    gcc = Hash $Gcc; gccVersion = ((& $Gcc --version) -join "`n"); target = $Triple.Trim()
    winpthread = [ordered]@{ path = $Winpthread; sha256 = Hash $Winpthread }
    cmake = Hash $CMake; ninja = Hash $Ninja; make = Hash $Make.Source; bash = Hash $Bash
    ffmpegConfigure = $Prepared.lock.ffmpegConfigure; xmlConfigure = $Prepared.lock.libxml2Cmake
    files = [ordered]@{}
}
foreach ($File in (Get-ChildItem -LiteralPath $Source -File | Where-Object { $_.Name -ne 'README.md' } | Sort-Object Name)) {
    $Inputs.files[$File.Name] = Hash $File.FullName
}
foreach ($Name in @('prepare-video-kernel.mjs','build-video-kernel.ps1','build-video-kernel-ffmpeg.sh')) {
    $Inputs.files[$Name] = Hash (Join-Path $PSScriptRoot $Name)
}
$InputPath = Join-Path $Build 'inputs.json'
Save $InputPath (($Inputs | ConvertTo-Json -Depth 12 -Compress) + "`n")
$Identity = Hash $InputPath
$Library = Join-Path $Output 'libmeowfilm_ffmpeg_plugin.dll'
$Managed = Join-Path $Output 'VideoKernelBuild.g.cs'
$Receipt = Join-Path $Output 'build-receipt.json'
if ((Test-Path -LiteralPath $Receipt) -and (Test-Path -LiteralPath $Library) -and (Test-Path -LiteralPath $Managed)) {
    $Prior = [IO.File]::ReadAllText($Receipt) | ConvertFrom-Json
    if ($Prior.identity -ceq $Identity -and $Prior.dllSha256 -ceq (Hash $Library) -and
        $Prior.managedSha256 -ceq (Hash $Managed)) {
        Write-Output "video_kernel_reused=$Identity"; exit 0
    }
}
$Jobs = [Math]::Max(1, [Math]::Min(2, [Environment]::ProcessorCount))
$XmlBuild = Join-Path $Build 'libxml2'
Run $CMake (@('-S',$Prepared.xml,'-B',$XmlBuild,'-G','Ninja',
    "-DCMAKE_MAKE_PROGRAM=$Ninja", "-DCMAKE_C_COMPILER=$Gcc", "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DCMAKE_INSTALL_PREFIX=$XmlPrefix", '-DCMAKE_INSTALL_LIBDIR=lib') + $Prepared.lock.libxml2Cmake)
Run $CMake @('--build',$XmlBuild,'--parallel',"$Jobs")
Run $CMake @('--install',$XmlBuild)
$Flags = Join-Path $Build 'ffmpeg-flags.txt'
Save $Flags (($Prepared.lock.ffmpegConfigure -join "`n") + "`n")
$FFmpegIdentityPath = Join-Path $Build 'ffmpeg-inputs.json'
Save $FFmpegIdentityPath (([ordered]@{ source = $Prepared.prepared.ffmpeg; flags = Hash $Flags;
    gcc = $Inputs.gcc; xml = $Prepared.prepared.xml; xmlConfigure = $Prepared.lock.libxml2Cmake;
    recipe = $Inputs.files['build-video-kernel-ffmpeg.sh']; configuration = $Configuration } | ConvertTo-Json -Compress) + "`n")
Run $Bash @((Join-Path $PSScriptRoot 'build-video-kernel-ffmpeg.sh'), $Prepared.ffmpeg,
    (Join-Path $Build 'ffmpeg'), $FFmpegPrefix, $XmlPrefix, $Gcc, $Make.Source, $Perl, $PkgConfig,
    $Flags, (Hash $FFmpegIdentityPath), "$Jobs")
$PluginBuild = Join-Path $Build 'plugin'
Run $CMake @('-S',$Source,'-B',$PluginBuild,'-G','Ninja', "-DCMAKE_MAKE_PROGRAM=$Ninja",
    "-DCMAKE_C_COMPILER=$Gcc", "-DCMAKE_BUILD_TYPE=$Configuration", "-DVLC_SDK=$($Prepared.sdk)",
    "-DFFMPEG_PREFIX=$FFmpegPrefix", "-DXML_PREFIX=$XmlPrefix", "-DWINPTHREAD_LIBRARY=$Winpthread",
    "-DBUILD_IDENTITY=$Identity")
Run $CMake @('--build',$PluginBuild,'--parallel',"$Jobs",'--','-k','0')
$BuiltLibrary = Join-Path $PluginBuild 'libmeowfilm_ffmpeg_plugin.dll'
$Pe = & $Objdump -p $BuiltLibrary
if ($LASTEXITCODE -ne 0 -or ($Pe -join "`n") -notmatch 'pei-x86-64') { throw 'Native plugin is not a valid Windows x64 image.' }
$Imports = @($Pe | Select-String 'DLL Name:\s+(.+)' | ForEach-Object { $_.Matches[0].Groups[1].Value.Trim().ToLowerInvariant() })
foreach ($Import in $Imports) {
    if ($Import -notmatch '^(libvlccore|kernel32|user32|advapi32|bcrypt|ole32|ws2_32|shlwapi|msvcrt|ucrtbase)\.dll$' -and
        $Import -notmatch '^api-ms-win-crt-[a-z0-9-]+\.dll$') { throw "Unshipped native plugin dependency: $Import" }
}
Copy-Item -LiteralPath $BuiltLibrary -Destination $Library -Force
$DllHash = Hash $Library
Save $Managed ("namespace MeowFilm.Media;`ninternal static class VideoKernelBuild`n{`n" +
    "    internal const string Identity = `"$Identity`";`n" +
    "    internal const string DllSha256 = `"$DllHash`";`n" +
    "    internal const string VlcCoreSha256 = `"$($Prepared.lock.vlcCoreSha256)`";`n}`n")
$Result = [ordered]@{ version = 1; identity = $Identity; architecture = 'win-x64'; configuration = $Configuration;
    vlcPluginAbi = $Prepared.lock.vlcPluginAbi; sources = $Prepared.lock.sources; inputs = $Inputs;
    dllSha256 = $DllHash; managedSha256 = Hash $Managed; vlcCoreSha256 = $Prepared.lock.vlcCoreSha256;
    imports = $Imports; ffmpegPrefix = $FFmpegPrefix; xmlPrefix = $XmlPrefix; objectRoot = $PluginBuild;
    linkMapSha256 = Hash (Join-Path $PluginBuild 'video-kernel.map');
    staticLibraries = [ordered]@{} }
foreach ($Name in @('avformat','avcodec','avutil')) {
    $Result.staticLibraries["lib$Name.a"] = Hash (Join-Path $FFmpegPrefix "lib\lib$Name.a")
}
$Result.staticLibraries['libxml2.a'] = Hash (Join-Path $XmlPrefix 'lib\libxml2.a')
$Result.staticLibraries['libwinpthread.a'] = $Inputs.winpthread.sha256
Save $Receipt (($Result | ConvertTo-Json -Depth 14) + "`n")
Write-Output "video_kernel_built=$Identity"
Write-Output 'video_kernel_exit=0'
