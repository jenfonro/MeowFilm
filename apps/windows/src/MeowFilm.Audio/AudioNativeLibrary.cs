using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FFmpeg.AutoGen;

namespace MeowFilm.Media;

/// <summary>Verified, process-lifetime audio FFmpeg mappings, independent of the video plugin.</summary>
internal static class AudioNativeLibrary
{
    private static readonly object Gate = new();
    private static Dictionary<string, IntPtr>? _libraries;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VersionFunction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TextFunction();

    internal static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_libraries is not null) return;
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            var libraries = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            try
            {
                string root = Path.Combine(AppContext.BaseDirectory, "audio-kernel", "win-x64");
                VerifyFile(Path.Combine(root, "build-receipt.json"), AudioKernelBuild.ReceiptSha256, false);
                foreach (var library in AudioKernelBuild.Libraries)
                    VerifyFile(Path.Combine(root, library.File), library.Sha256, true);
                VerifyMappings(root);
                foreach (var library in AudioKernelBuild.Libraries)
                {
                    var handle = NativeLibrary.Load(Path.Combine(root, library.File), typeof(AudioNativeLibrary).Assembly,
                        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
                    libraries.Add(library.Name, handle);
                    uint version = Marshal.GetDelegateForFunctionPointer<VersionFunction>(
                        NativeLibrary.GetExport(handle, library.Name + "_version"))();
                    string? license = ReadText(handle, library.Name + "_license");
                    string? configuration = ReadText(handle, library.Name + "_configuration");
                    if (version != library.Version || license != AudioKernelBuild.License || configuration is null ||
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configuration))) !=
                            AudioKernelBuild.ConfigurationSha256)
                        throw new PlaybackException(PlaybackError.NativeUnavailable);
                }
                VerifyMappings(root);
                if (ReadText(libraries["avutil"], "av_version_info") != AudioKernelBuild.Version)
                    throw new PlaybackException(PlaybackError.NativeUnavailable);

                // AutoGen's default resolver also searches swscale/avdevice and ambient DLL paths.
                // Resolve only exports from this verified audio closure instead.
                ffmpeg.RootPath = root;
                foreach (var library in AudioKernelBuild.Libraries)
                    if (!ffmpeg.LibraryVersionMap.TryGetValue(library.Name, out int major) ||
                        major != (int)(library.Version >> 16))
                        throw new PlaybackException(PlaybackError.NativeUnavailable);
                DynamicallyLoadedBindings.FunctionResolver = new VerifiedResolver(libraries);
                DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                DynamicallyLoadedBindings.Initialize();
                _libraries = libraries;
            }
            catch
            {
                foreach (var handle in libraries.Values.Reverse()) NativeLibrary.Free(handle);
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            }
        }
    }

    private static string? ReadText(IntPtr library, string export) =>
        Marshal.PtrToStringUTF8(Marshal.GetDelegateForFunctionPointer<TextFunction>(
            NativeLibrary.GetExport(library, export))());

    private static void VerifyFile(string path, string expected, bool image)
    {
        using var stream = File.OpenRead(path);
        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != expected)
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        if (!image) return;
        Span<byte> header = stackalloc byte[64];
        stream.Position = 0;
        stream.ReadExactly(header);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (header[0] != 'M' || header[1] != 'Z' || offset < 64 || offset > stream.Length - 26)
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        stream.Position = offset;
        stream.ReadExactly(header[..26]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x4550 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 0x8664 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[24..]) != 0x20b)
            throw new PlaybackException(PlaybackError.NativeUnavailable);
    }

    private static void VerifyMappings(string root)
    {
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            var library = Array.Find(AudioKernelBuild.Libraries,
                item => item.File.Equals(module.ModuleName, StringComparison.OrdinalIgnoreCase));
            if (library.File is not null && !Path.GetFullPath(module.FileName).Equals(
                Path.Combine(root, library.File), StringComparison.OrdinalIgnoreCase))
                throw new PlaybackException(PlaybackError.NativeUnavailable);
        }
    }

    private sealed class VerifiedResolver(IReadOnlyDictionary<string, IntPtr> libraries) : IFunctionResolver
    {
        public T GetFunctionDelegate<T>(string libraryName, string functionName, bool throwOnError = true)
        {
            if (libraries.TryGetValue(libraryName, out var library) &&
                NativeLibrary.TryGetExport(library, functionName, out var address))
                return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            if (throwOnError) throw new EntryPointNotFoundException(functionName);
            return default!;
        }
    }
}
