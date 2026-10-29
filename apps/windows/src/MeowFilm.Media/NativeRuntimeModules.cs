using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace MeowFilm.Media;

/// <summary>
/// Process-lifetime DLL mappings, not LibVLC instances, players or decoder sessions.
/// The pinned 3.0.24 Windows package leaks native TLS slots when these statically linked
/// runtimes are repeatedly loaded/unloaded by VLC's plugin-bank scan. Keep one loader
/// reference per affected, installed module; normal engine/session disposal is unchanged.
/// </summary>
internal static class NativeRuntimeModules
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IntPtr> Retained = new(StringComparer.OrdinalIgnoreCase);
    private static string? _runtimeRoot;
    private static bool _ready;
    private static readonly string[] RelativePaths =
    [
        "access_output/libaccess_output_livehttp_plugin.dll",
        "access_output/libaccess_output_rist_plugin.dll",
        "access_output/libaccess_output_srt_plugin.dll",
        "access/libaccess_srt_plugin.dll",
        "access/libcdda_plugin.dll",
        "access/libdcp_plugin.dll",
        "access/librist_plugin.dll",
        "access/librtp_plugin.dll",
        "access/libsftp_plugin.dll",
        "demux/libadaptive_plugin.dll",
        "misc/libgnutls_plugin.dll",
        "spu/libremoteosd_plugin.dll"
    ];

    // One native runtime root per process. Serialize validation AND Core.Initialize so a
    // second coordinator cannot load another core before discovering that the first is bound.
    // This is trusted host configuration, never a media URI or an environment plugin path.
    internal static void EnsureLoaded(string? nativeLibraryPath)
    {
        lock (Gate)
        {
            string? requested = nativeLibraryPath is null ? null : NormalizeRoot(nativeLibraryPath);
            var loaded = ReadLoadedRuntime();
            string? bound = _runtimeRoot ?? loaded.Root;
            if (bound is not null && requested is not null && !SameRoot(bound, requested))
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            if (_runtimeRoot is not null &&
                (loaded.Root is null || !SameRoot(_runtimeRoot, loaded.Root)))
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            if (_ready)
            {
                if (!loaded.Complete) throw new PlaybackException(PlaybackError.NativeUnavailable);
                return;
            }

            // Reuse an already loaded/partially initialized root, not a fresh default search.
            Core.Initialize(bound ?? requested);
            loaded = ReadLoadedRuntime();
            if (!loaded.Complete || loaded.Root is null ||
                bound is not null && !SameRoot(bound, loaded.Root) ||
                requested is not null && !SameRoot(requested, loaded.Root))
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            // Bind even if a later plugin load fails. A retry may finish the same root only.
            _runtimeRoot = loaded.Root;
            foreach (string relative in RelativePaths)
            {
                string file = Path.Combine(_runtimeRoot, "plugins", relative);
                // A deliberately trimmed host distribution need not contain an unused module.
                // The supported/tested full fixed package has all twelve.
                if (!File.Exists(file) || Retained.ContainsKey(file)) continue;
                // Never Free these references on per-coordinator Dispose. They are bounded
                // loader state, reclaimed by process exit, with no player/engine owner.
                // Keep successful references even if a later load fails; retries cannot leak
                // another reference to those modules. Do not suppress native crypto init.
                Retained.Add(file, NativeLibrary.Load(file));
            }
            _ready = true;
        }
    }

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SameRoot(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static (string? Root, bool Complete) ReadLoadedRuntime()
    {
        // GetModuleHandle(basename) is ambiguous if a foreign host loaded multiple copies.
        // Read back both actual DLL paths and reject duplicates/mixed roots instead.
        using var process = Process.GetCurrentProcess();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? root = null;
        foreach (ProcessModule module in process.Modules)
        {
            string name = module.ModuleName;
            if (!string.Equals(name, "libvlc.dll", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "libvlccore.dll", StringComparison.OrdinalIgnoreCase)) continue;
            string directory = NormalizeRoot(Path.GetDirectoryName(module.FileName)!);
            if (!names.Add(name) || root is not null && !SameRoot(root, directory))
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            root = directory;
        }
        return (root, names.Count == 2);
    }
}
