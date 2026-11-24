using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace MeowFilm.Media;

/// <summary>Process-lifetime audio FFmpeg mappings, independent of the video plugin.</summary>
internal static unsafe class AudioNativeLibrary
{
    private static readonly object Gate = new();
    private static readonly av_log_set_callback_callback DiscardNativeLog = (_, _, _, _) => { };
    private static Dictionary<string, IntPtr>? _libraries;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VersionFunction();

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
                foreach (var library in AudioKernelBuild.Libraries)
                {
                    var handle = NativeLibrary.Load(Path.Combine(root, library.File), typeof(AudioNativeLibrary).Assembly,
                        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
                    libraries.Add(library.Name, handle);
                    uint version = Marshal.GetDelegateForFunctionPointer<VersionFunction>(
                        NativeLibrary.GetExport(handle, library.Name + "_version"))();
                    if (!ffmpeg.LibraryVersionMap.TryGetValue(library.Name, out int major) ||
                        major != (int)(version >> 16))
                        throw new PlaybackException(PlaybackError.NativeUnavailable);
                }

                // Resolve exports from this audio closure instead of ambient PATH DLLs.
                ffmpeg.RootPath = root;
                DynamicallyLoadedBindings.FunctionResolver = new VerifiedResolver(libraries);
                DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                DynamicallyLoadedBindings.Initialize();
                ffmpeg.av_log_set_callback(DiscardNativeLog);
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
                _libraries = libraries;
            }
            catch
            {
                foreach (var handle in libraries.Values.Reverse()) NativeLibrary.Free(handle);
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            }
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
