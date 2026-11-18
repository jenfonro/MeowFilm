using System.Diagnostics;
using System.IO;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class AppUserDataResetTests
{
    private static async Task NativeOwnersReset()
    {
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
            ?? throw new InvalidOperationException("A real aggregate Windows runtime bundle is required.");
        var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), AppIdentity.RuntimeId);
        string parent = TestWorkspace.DataPath("reset-native-owners-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(Path.Combine(parent, "aggregate"));
        using var data = new UserDataDirectory(paths.Documents, paths.Caches);
        data.Open();
        Directory.CreateDirectory(paths.Documents);
        Directory.CreateDirectory(paths.Caches);
        var errors = new List<string>();
        var store = new LocalDocumentStore(paths.Documents, AppIdentity.NativeNamespaces,
            AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
        var cache = new CacheDocumentStore(() => new LocalDocumentStore(paths.Caches, AppIdentity.CacheNamespaces,
            protectedNamespaces: AppIdentity.CacheProtectedNamespaces));
        NodeRuntime? session = null;
        Process? child = null;
        var runtime = new AppRuntimeConnectionHost(async token =>
        {
            session = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, AppIdentity.ScriptModules,
                paths.ScriptRuntime, token, ownerRevoked: token, settingsStore: store,
                settingsNamespaces: AppIdentity.RuntimeSettingsNamespaces);
            child = Process.GetProcessById(session.ProcessId);
            _ = child.SafeHandle;
            return session;
        });
        var audio = new NativeAudioPlayback(audioOutput: NativeAudioOutput.Dummy);
        var video = new FilmPlayback();
        var library = new MusicLibraryDatabase(Path.Combine(paths.Documents, "music-library"));
        var application = new MusicNativeApplication(store, library, runtime, cache);
        var music = new MusicCapability(application, store, audio, cacheStore: cache,
            playbackCacheRoot: Path.Combine(paths.Caches, "music-audio"));
        var rules = new FilmRuleService(runtime, store);
        var sources = new FilmSourceService(runtime, store);
        var metadata = new FilmMetadataService(runtime, store);
        var film = new FilmCapability(rules, sources, runtime, store, video, runtime.CaptureConnectionAsync, metadata);
        var novel = new NovelCapability(runtime, store);
        RuntimeConnection? connection = null;
        bool runtimeClosed = false, cacheClosed = false, libraryClosed = false, storeClosed = false;
        var participants = new[] {
            new NativeResetParticipant("music", music, BeforeAbort, errors),
            new NativeResetParticipant("film", film, BeforeAbort, errors),
            new NativeResetParticipant("novel", novel, BeforeAbort, errors)
        };
        var shutdown = new AppShutdown(participants, [
            new(() => audio.DisposeAsync()),
            new(() => video.DisposeAsync()),
            new(async () => { await cache.DisposeAsync(); cacheClosed = true; }),
            new(async () => { await CloseRuntime(); }, InterruptsPendingOperations: true),
            new(async () => { await library.DisposeAsync(); libraryClosed = true; }),
            new(async () => { await store.DisposeAsync(); storeClosed = true; })
        ]);
        bool cleared = false, reopened = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            runtime.Start();
            await music.InitializeAsync(deadline.Token).WaitAsync(deadline.Token);
            await film.InitializeAsync(deadline.Token).WaitAsync(deadline.Token);
            await novel.InitializeAsync(deadline.Token).WaitAsync(deadline.Token);
            connection = await runtime.CaptureConnectionAsync(deadline.Token);
            await film.StartSourcesAsync().WaitAsync(deadline.Token);
            Check(await novel.Rules.MatchUrlAsync("^https://reset[.]invalid$", "https://reset.invalid", deadline.Token));
            var created = await application.ExecuteAsync("music.library.create", new { name = "synthetic reset" }, deadline.Token);
            Check(created.GetProperty("ok").GetBoolean());
            // QQ has no channel directory: this publishes an anonymous native
            // platform scope, then Node rejects locally without any provider HTTP.
            var unsupported = await application.ExecuteAsync("music.catalog.channels", new { platform = "tx" }, deadline.Token);
            Check(!unsupported.GetProperty("ok").GetBoolean() && unsupported.GetProperty("code").GetString() == "UNSUPPORTED");
            File.WriteAllText(Path.Combine(paths.Caches, "old-generation"), "synthetic cache");
            await cache.DrainAsync();
            Check(!audio.HasMedia && !video.HasOutput && session is not null && child is not null && !child.HasExited);
            var reset = new AppUserDataReset(data.RequestReset, shutdown.ForceReleaseAsync,
                () =>
                {
                    Check(shutdown.IsReleased && runtimeClosed && cacheClosed && libraryClosed && storeClosed,
                        "All real owners and shared resources must release before deletion.");
                    data.Clear();
                    cleared = true;
                    return Task.CompletedTask;
                },
                async () =>
                {
                    Check(!Directory.Exists(paths.Documents) && !Directory.Exists(paths.Caches));
                    data.Open();
                    Directory.CreateDirectory(paths.Documents);
                    Directory.CreateDirectory(paths.Caches);
                    await using var freshLibrary = new MusicLibraryDatabase(Path.Combine(paths.Documents, "music-library"));
                    var fresh = await freshLibrary.ReadAsync("music:native");
                    Check(fresh.Revision == MusicLibraryRevision.Empty && fresh.Areas.Count == 0 &&
                        freshLibrary.SongIds().Count == 0);
                    File.WriteAllText(Path.Combine(paths.Documents, "new-generation"), "new business");
                    File.WriteAllText(Path.Combine(paths.Caches, "new-generation"), "new cache");
                    reopened = true;
                });
            var resetting = reset.ResetAsync();
            Check(ReferenceEquals(resetting, reset.ResetAsync()));
            await resetting.WaitAsync(TimeSpan.FromSeconds(10));
            await reset.ResetAsync();
            Check(cleared && reopened && shutdown.IsReleased && runtime.State == RuntimeConnectionState.Closed);
            Check(File.Exists(Path.Combine(paths.Documents, "new-generation")) &&
                File.Exists(Path.Combine(paths.Caches, "new-generation")) &&
                !File.Exists(Path.Combine(paths.Caches, "old-generation")));
        }
        catch (Exception error) { errors.Add("reset=" + NativeResetError(error)); }
        finally
        {
            // Keep the original owner failures visible even when AppShutdown's
            // fixed SHUTDOWN_FAILED receipt stops before the remaining owners.
            bool settled = await Cleanup("runtime", CloseRuntime) && runtimeClosed;
            foreach (var participant in participants)
                settled &= await Cleanup(participant.Id, participant.CleanupAsync);
            if (settled)
            {
                await Cleanup("audio", () => audio.DisposeAsync().AsTask());
                await Cleanup("video", () => video.DisposeAsync().AsTask());
                await Cleanup("cache", () => cache.DisposeAsync().AsTask());
                await Cleanup("library", () => library.DisposeAsync().AsTask());
                await Cleanup("store", () => store.DisposeAsync().AsTask());
            }
            child?.Dispose();
            data.Dispose();
            if (errors.Count == 0)
                Directory.Delete(TestWorkspace.ValidatePath(TestWorkspace.DataRoot, parent), recursive: true);
        }
        if (errors.Count > 0) throw new InvalidOperationException("NATIVE_RESET_FAILED " + string.Join("; ", errors.Distinct()));

        void BeforeAbort()
        {
            Check(runtimeClosed && runtime.State == RuntimeConnectionState.Closed &&
                connection?.Revoked.IsCancellationRequested == true && child?.HasExited == true,
                "Native abort must follow confirmed runtime revocation and actual Node process exit.");
        }
        async Task CloseRuntime()
        {
            await runtime.DisposeAsync();
            if (session is not null) await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (child is not null)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.HasExited);
            }
            runtimeClosed = true;
        }
        async Task<bool> Cleanup(string id, Func<Task> action)
        {
            Task? work = null;
            try { work = action(); await work.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) { errors.Add(id + ".cleanup=" + NativeResetError(error)); }
            return work?.IsCompleted == true;
        }
    }

    private sealed class NativeResetParticipant(string id, IDesktopShutdownParticipant owner,
        Action beforeAbort, List<string> errors) : IDesktopShutdownParticipant
    {
        internal string Id => id;
        public Task PrepareShutdownAsync() => owner.PrepareShutdownAsync();
        public Task CancelShutdownPreparationAsync() => owner.CancelShutdownPreparationAsync();
        public ValueTask DisposeAsync() => owner.DisposeAsync();
        internal Task CleanupAsync() => owner.AbortAsync();
        public async Task AbortAsync()
        {
            try { beforeAbort(); await owner.AbortAsync(); }
            catch (Exception error)
            {
                errors.Add(id + ".abort=" + NativeResetError(error));
                throw;
            }
        }
    }

    private static string NativeResetError(Exception error)
    {
        string? raw = error switch
        {
            BridgeException bridge => bridge.Code,
            MusicPlatformException platform => platform.Code,
            DocumentStoreException storage => storage.Error.ToString(),
            OperationCanceledException => "CANCELLED",
            TimeoutException => "TIMEOUT",
            _ => error.Message
        };
        string code = raw is "CLOSED" or "CANCELLED" or "TIMEOUT" or "SHUTDOWN_FAILED" or "COMMIT_UNKNOWN" or
            "SHUTDOWN_NOT_DRAINED" or "SHUTTING_DOWN" or "STORAGE_UNAVAILABLE" or "Closed" or "Storage" ? raw : "FAILED";
        return error.GetType().Name + "/" + code;
    }
}
