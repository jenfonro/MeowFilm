param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paths.ps1')

function Tool([string]$Name) {
    $Command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $Command) { throw "Audio-kernel build requires $Name on PATH." }
    return $Command.Source
}
function Run([string]$Executable, [string[]]$Arguments) {
    $Saved = $ErrorActionPreference
    try { $ErrorActionPreference = 'Continue'; & $Executable @Arguments; $Code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $Saved }
    if ($Code -ne 0) { Write-Output "audio_kernel_tool_exit=$Code"; exit $Code }
}
function Text([string]$Executable, [string[]]$Arguments) {
    $Result = & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Audio-kernel tool failed: $Executable" }
    return (($Result -join "`n").Trim())
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Save([string]$Path, [string]$Value) {
    if (-not (Test-Path -LiteralPath $Path) -or [IO.File]::ReadAllText($Path) -cne $Value) {
        [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
    }
}
function Prepare([string]$Node) {
    $Names = @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY')
    $Prior = @{}
    foreach ($Name in $Names) { $Prior[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process') }
    $Saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $env:HTTP_PROXY = 'http://127.0.0.1:7890'; $env:HTTPS_PROXY = $env:HTTP_PROXY
        $env:ALL_PROXY = $env:HTTP_PROXY; $env:NO_PROXY = ''
        $Result = & $Node --use-env-proxy (Join-Path $PSScriptRoot 'prepare-audio-kernel.mjs')
        $Code = $LASTEXITCODE
        if ($Code -eq 75) {
            Write-Host 'Audio dependency proxy unavailable; trying a direct download.'
            foreach ($Name in $Names) { [Environment]::SetEnvironmentVariable($Name, $null, 'Process') }
            $Result = & $Node (Join-Path $PSScriptRoot 'prepare-audio-kernel.mjs')
            $Code = $LASTEXITCODE
        }
        if ($Code -ne 0) { exit $Code }
        return ($Result | ConvertFrom-Json)
    } finally {
        $ErrorActionPreference = $Saved
        foreach ($Name in $Names) { [Environment]::SetEnvironmentVariable($Name, $Prior[$Name], 'Process') }
    }
}

$Node = Tool 'node.exe'; $Gcc = Tool 'gcc.exe'; $Nasm = Tool 'nasm.exe'; $Objdump = Tool 'objdump.exe'
$Git = Tool 'git.exe'
$Make = Get-Command gmake.exe,mingw32-make.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $Make) { throw 'Audio FFmpeg requires the installed MinGW GNU make.' }
$Bash = Join-Path (Split-Path (Split-Path $Git)) 'bin\bash.exe'
if (-not (Test-Path -LiteralPath $Bash)) { throw 'Audio FFmpeg requires the installed Git Bash.' }
$Prepared = Prepare $Node
$Lock = $Prepared.lock; $Cache = $Prepared.cache
$null = Assert-WindowsPath -Owner $WindowsRoot -Path $Cache
$null = Assert-WindowsPath -Owner 'E:\Developer\cache' -Path $Prepared.shared
$Triple = Text $Gcc @('-dumpmachine'); $Version = Text $Gcc @('-dumpfullversion')
if ($Triple -cne $Lock.toolchain.target -or $Version -cne $Lock.toolchain.gccVersion) {
    throw 'Audio FFmpeg requires the locked GCC 13.2.0 x86_64-w64-mingw32 toolchain.'
}
$RuntimeHeader = [IO.Path]::GetFullPath((Join-Path (Split-Path $Gcc) '..\x86_64-w64-mingw32\include\_mingw_mac.h'))
$RuntimeText = [IO.File]::ReadAllText($RuntimeHeader)
$RuntimeVersion = (@('MAJOR','MINOR','BUGFIX') | ForEach-Object {
    if ($RuntimeText -notmatch "(?m)^#define __MINGW64_VERSION_$_\s+(\d+)\s*$") { throw 'Missing MinGW runtime version.' }
    $Matches[1]
}) -join '.'
if ($RuntimeVersion -cne $Lock.toolchain.mingwRuntimeVersion) { throw 'Audio FFmpeg MinGW runtime version mismatch.' }
$Inputs = [ordered]@{
    schema = 1; architecture = $Lock.architecture; lock = $Lock; prepared = $Prepared.prepared
    downloadRecipe = $Prepared.downloadRecipe; target = $Triple; gccVersion = $Version
    # Third-party FFmpeg uses one optimized configuration for both managed Debug and Release.
    nativeConfiguration = 'Release'; tools = [ordered]@{}; runtimeLibraries = [ordered]@{}; recipes = [ordered]@{}
}
foreach ($Pair in @(@('gcc',$Gcc),@('nasm',$Nasm),@('objdump',$Objdump),@('make',$Make.Source),@('bash',$Bash))) {
    $Inputs.tools[$Pair[0]] = [ordered]@{ path = $Pair[1]; sha256 = Hash $Pair[1] }
}
foreach ($Name in @('cc1','collect2','ld','as')) {
    $Path = [IO.Path]::GetFullPath((Text $Gcc @("-print-prog-name=$Name")))
    $Inputs.tools[$Name] = [ordered]@{ path = $Path; sha256 = Hash $Path }
}
foreach ($Name in @('libgcc.a','libgcc_eh.a','libmingw32.a','libmingwex.a','libmoldname.a','libucrt.a',
    'libwinpthread.a','libkernel32.a','libadvapi32.a','libbcrypt.a','libole32.a','libuser32.a','libpsapi.a','libshell32.a','libshlwapi.a')) {
    $Path = [IO.Path]::GetFullPath((Text $Gcc @("-print-file-name=$Name")))
    $Inputs.runtimeLibraries[$Name] = [ordered]@{ path = $Path; sha256 = Hash $Path }
}
$Inputs.runtimeHeader = Hash $RuntimeHeader
foreach ($Name in @('prepare-audio-kernel.mjs','build-audio-kernel.ps1','build-audio-kernel-ffmpeg.sh')) {
    $Inputs.recipes[$Name] = Hash (Join-Path $PSScriptRoot $Name)
}
[IO.Directory]::CreateDirectory($Cache) | Out-Null
$InputPath = Join-Path $Cache 'inputs.json'
Save $InputPath (($Inputs | ConvertTo-Json -Depth 16 -Compress) + "`n")
$Identity = Hash $InputPath
$Shared = Join-Path $Prepared.shared $Identity
[IO.Directory]::CreateDirectory($Prepared.shared) | Out-Null
$LockPath = $Shared + '.lock'
$Claim = $null; $Deadline = [DateTime]::UtcNow.AddMinutes(30)
while ($null -eq $Claim) {
    try { $Claim = [IO.File]::Open($LockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) }
    catch [IO.IOException] {
        if (-not (Test-Path -LiteralPath $LockPath) -or [DateTime]::UtcNow -ge $Deadline) { throw }
        Start-Sleep -Seconds 2
    }
}
$ClaimText = [Text.Encoding]::UTF8.GetBytes("pid=$PID`nworkspace=$WindowsRoot`n")
$Claim.Write($ClaimText, 0, $ClaimText.Length); $Claim.Flush()
$StageCreated = $false

try {
    $Dlls = @($Lock.libraries | ForEach-Object { "$($_.name)-$($_.major).dll" })
    $Stage = $Shared + ".stage-$PID"
    if (-not (Test-Path -LiteralPath $Shared)) {
        $Build = Join-Path $Cache "build\$Identity"
        $Prefix = Join-Path $Cache "install\$Identity"
        [IO.Directory]::CreateDirectory($Build) | Out-Null
        $Flags = Join-Path $Build 'configure-flags.txt'
        Save $Flags (($Lock.configure -join "`n") + "`n")
        $Jobs = [Math]::Max(1, [Math]::Min(2, [Environment]::ProcessorCount))
        Run $Bash @((Join-Path $PSScriptRoot 'build-audio-kernel-ffmpeg.sh'),$Prepared.source,$Build,$Prefix,
            $Gcc,$Make.Source,$Nasm,$Flags,$Identity,"$Jobs")
        if (Test-Path -LiteralPath $Stage) { throw 'Audio artifact staging directory is already occupied.' }
        [IO.Directory]::CreateDirectory($Stage) | Out-Null
        $StageCreated = $true
        foreach ($Dll in $Dlls) { Copy-Item -LiteralPath (Join-Path $Prefix "bin\$Dll") -Destination $Stage }
        $Artifact = $Stage
    } else { $Artifact = $Shared }
    $null = Assert-WindowsPath -Owner $Prepared.shared -Path $Artifact
    if ((@(Get-ChildItem -LiteralPath $Artifact -File -Filter '*.dll').Name | Sort-Object) -join ',' -cne
        (($Dlls | Sort-Object) -join ',')) { throw 'Audio artifact DLL inventory mismatch.' }

    if (-not ('MeowAudioNativeAudit' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MeowAudioNativeAudit {
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32")]
    public static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint VersionFunction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr TextFunction();
    private static IntPtr Export(IntPtr module, string name) {
        var result = GetProcAddress(module, name);
        if (result == IntPtr.Zero) throw new EntryPointNotFoundException(name);
        return result;
    }
    public static uint Version(IntPtr module, string name) {
        return ((VersionFunction)Marshal.GetDelegateForFunctionPointer(Export(module, name), typeof(VersionFunction)))();
    }
    public static string Text(IntPtr module, string name) {
        return Marshal.PtrToStringAnsi(((TextFunction)Marshal.GetDelegateForFunctionPointer(Export(module, name), typeof(TextFunction)))());
    }
}
'@
    }
    $Files = [ordered]@{}; $Handles = [Collections.Generic.List[IntPtr]]::new()
    $NativeConfiguration = $null
    try {
        foreach ($Library in $Lock.libraries) {
            $Dll = "$($Library.name)-$($Library.major).dll"; $Path = Join-Path $Artifact $Dll
            $null = Assert-WindowsPath -Owner $Artifact -Path $Path
            $Pe = Text $Objdump @('-p',$Path)
            if ($Pe -notmatch 'file format pei-x86-64') { throw "Audio DLL is not Windows x64: $Dll" }
            $Imports = @([regex]::Matches($Pe, 'DLL Name:\s+([^\r\n]+)') |
                ForEach-Object { $_.Groups[1].Value.Trim().ToLowerInvariant() } | Sort-Object -Unique)
            foreach ($Import in $Imports) {
                if ($Import -notin $Dlls -and $Import -notin $Lock.systemImports -and
                    $Import -notmatch '^api-ms-win-crt-[a-z0-9-]+\.dll$') { throw "Unshipped audio DLL dependency: $Import" }
            }
            $Handle = [MeowAudioNativeAudit]::LoadLibraryEx($Path, [IntPtr]::Zero, 0x900)
            if ($Handle -eq [IntPtr]::Zero) { throw "Unable to load verified audio DLL: $Dll" }
            $Handles.Add($Handle)
            $ExpectedVersion = ($Library.major -shl 16) -bor ($Library.minor -shl 8) -bor $Library.micro
            if ([MeowAudioNativeAudit]::Version($Handle, "$($Library.name)_version") -ne $ExpectedVersion -or
                [MeowAudioNativeAudit]::Text($Handle, "$($Library.name)_license") -cne $Lock.toolchain.license) {
                throw "Audio DLL ABI or LGPL license mismatch: $Dll"
            }
            $LibraryConfiguration = [MeowAudioNativeAudit]::Text($Handle, "$($Library.name)_configuration")
            if ($null -eq $NativeConfiguration) {
                $NativeConfiguration = $LibraryConfiguration
                if ([MeowAudioNativeAudit]::Text($Handle, 'av_version_info') -cne $Lock.source.version) {
                    throw 'Audio DLL FFmpeg release mismatch.'
                }
            } elseif ($LibraryConfiguration -cne $NativeConfiguration) { throw 'Mixed audio DLL build configurations.' }
            $Files[$Dll] = [ordered]@{ sha256 = Hash $Path; version = $ExpectedVersion; imports = $Imports }
        }
    } finally {
        for ($Index = $Handles.Count - 1; $Index -ge 0; $Index--) { $null = [MeowAudioNativeAudit]::FreeLibrary($Handles[$Index]) }
    }
    $ConfigurationHash = [Security.Cryptography.SHA256]::Create()
    try { $ConfigurationSha256 = ([BitConverter]::ToString($ConfigurationHash.ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($NativeConfiguration)))).Replace('-','').ToLowerInvariant() }
    finally { $ConfigurationHash.Dispose() }
    $ReceiptName = 'build-receipt.json'
    if ($Artifact -ceq $Stage) {
        $Receipt = [ordered]@{
            schema = 1; identity = $Identity; architecture = $Lock.architecture; source = $Lock.source
            binding = $Lock.binding; inputs = $Inputs; configuration = $NativeConfiguration
            configurationSha256 = $ConfigurationSha256; files = $Files
        }
        Save (Join-Path $Stage $ReceiptName) (($Receipt | ConvertTo-Json -Depth 20) + "`n")
        [IO.Directory]::Move($Stage, $Shared)
    } else {
        $Receipt = [IO.File]::ReadAllText((Join-Path $Shared $ReceiptName)) | ConvertFrom-Json
        if ($Receipt.identity -cne $Identity -or $Receipt.configurationSha256 -cne $ConfigurationSha256 -or
            @($Receipt.files.PSObject.Properties).Count -ne $Dlls.Count) { throw 'Audio shared artifact receipt mismatch.' }
        foreach ($Dll in $Dlls) {
            if ($Receipt.files.$Dll.sha256 -cne $Files[$Dll].sha256 -or
                $Receipt.files.$Dll.version -ne $Files[$Dll].version) { throw "Audio shared artifact changed: $Dll" }
        }
    }
    $Output = Join-Path $Cache 'output'
    [IO.Directory]::CreateDirectory($Output) | Out-Null
    foreach ($Name in @($Dlls) + @($ReceiptName)) {
        $SourcePath = Join-Path $Shared $Name; $Destination = Join-Path $Output $Name
        if (-not (Test-Path -LiteralPath $Destination) -or (Hash $SourcePath) -cne (Hash $Destination)) {
            Copy-Item -LiteralPath $SourcePath -Destination $Destination -Force
        }
        if ((Hash $SourcePath) -cne (Hash $Destination)) { throw 'Audio artifact copy verification failed.' }
    }
    $Rows = @($Lock.libraries | ForEach-Object {
        $Dll = "$($_.name)-$($_.major).dll"
        "        new(`"$($_.name)`", `"$Dll`", $($Files[$Dll].version)u, `"$($Files[$Dll].sha256)`"),"
    }) -join "`n"
    Save (Join-Path $Output 'AudioKernelBuild.g.cs') ("namespace MeowFilm.Media;`ninternal static class AudioKernelBuild`n{`n" +
        "    internal const string Identity = `"$Identity`";`n" +
        "    internal const string Version = `"$($Lock.source.version)`";`n" +
        "    internal const string ReceiptSha256 = `"$(Hash (Join-Path $Output $ReceiptName))`";`n" +
        "    internal const string ConfigurationSha256 = `"$ConfigurationSha256`";`n" +
        "    internal const string License = `"$($Lock.toolchain.license)`";`n" +
        "    internal readonly record struct Library(string Name, string File, uint Version, string Sha256);`n" +
        "    internal static readonly Library[] Libraries =`n    [`n$Rows`n    ];`n}`n")
    Write-Output "audio_kernel_ready=$Identity"
    Write-Output 'audio_kernel_exit=0'
} finally {
    try {
        if ($StageCreated -and (Test-Path -LiteralPath $Stage)) { Remove-Item -LiteralPath $Stage -Recurse }
    } finally {
        $Claim.Dispose()
        Remove-Item -LiteralPath $LockPath
    }
}
