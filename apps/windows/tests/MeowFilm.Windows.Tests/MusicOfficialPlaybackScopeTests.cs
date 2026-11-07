using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

// Real queue, policy, URL cache and complete-file owners; synthetic official and playback ports.
public static class MusicOfficialPlaybackScopeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("complete file on another known recording wins before any URL resolution or discovery", async () =>
        {
            await using var f = await Scene.Create(files: true);
            f.App.EnabledScript = true;
            var song = Song("first", alternate: true);
            await f.SeedFile(song, "tx");
            int transfers = f.Files.Requests;
            await f.Play(song);
            Check(f.Output.ReadOpens == 1 && f.Output.NetworkOpens == 0 &&
                f.Module.CurrentSource == "QQ 音乐", f.Module.Status);
            Check(f.App.Resolutions.Count == 0 && f.App.Plans.Count == 1 &&
                f.App.Completions.Count == 0);
            Check(f.Files.Requests == transfers && f.App.Authorizations == 0,
                "licensed complete bytes need neither another transfer nor online reauthorization");
            Check(JsonNode.DeepEquals(JsonNode.Parse(f.Module.QueueSurfaceRows.Single().Song.Raw.GetRawText()), song),
                "playback must preserve the queue song");
        });
        yield return Case("valid URL cache starts playback without catalog discovery or another media lookup", async () =>
        {
            await using var f = await Scene.Create();
            f.App.EnabledScript = true;
            var song = Song("first");
            var receipt = await f.App.Receipt(song, "wy");
            await f.App.Urls.PutAsync("fixture", LJ.S(song["songId"]), Signature("wy"), receipt, default);
            await f.Play(song);
            Check(f.Output.NetworkOpens == 1 && f.Output.LastUri!.AbsoluteUri == LJ.S(receipt["url"]));
            Check(f.App.MediaLookups == 0 && f.App.Resolutions.Count == 1 &&
                f.App.Resolutions.Single().GetProperty("refresh").ValueKind == JsonValueKind.False);
            Check(f.App.Plans.Count == 1 && f.App.Completions.Count == 0);
        });
        yield return Case("lower-quality official replies do not skip preferred tiers", async () =>
        {
            await using var f = await Scene.Create();
            await f.SetQuality("auto");
            f.App.MediaQuality = "320k";
            await f.Play(Song("first"));
            Check(f.Output.NetworkOpens == 1 && f.Module.CurrentQualityLabel == "高音质", f.Module.Status);
            Check(f.App.Resolutions.First().GetProperty("quality").GetString() == "master" &&
                f.App.Completions.Count == 1 && f.App.Resolutions.Count == 2,
                "known and completed candidates are tried at the preferred tier before accepting their lower reply");
        });
        yield return Case("another usable existing mapping is tried before catalog discovery", async () =>
        {
            await using var f = await Scene.Create();
            f.App.Failures["wy"] = "VIP_REQUIRED";
            await f.Play(Song("first", alternate: true));
            Check(f.Output.NetworkOpens == 1 && f.Module.CurrentSource == "QQ 音乐", f.Module.Status);
            Check(Platforms(f.App.Resolutions).SequenceEqual(["wy", "tx"]) && f.App.Plans.Count == 1,
                "rights denial must not force catalog work while an existing mapping is usable");
        });
        foreach (string failure in new[] { "NETWORK_ERROR", "AUTH_REQUIRED", "MEDIA_EXPIRED", "RECORDING_QUALITY_UNAVAILABLE" })
        {
            yield return Case("unavailable known source discovers once without repeating its media lookup: " + failure, async () =>
            {
                await using var f = await Scene.Create();
                var song = Song("first");
                f.App.Failures["wy"] = failure;
                await f.Play(song);
                Check(f.Output.NetworkOpens == 1 && f.Module.CurrentSource == "QQ 音乐", f.Module.Status);
                Check(Platforms(f.App.Resolutions).SequenceEqual(["wy", "tx"]) && f.App.Plans.Count == 2);
                Check(f.App.Completions.Count == 1 && f.App.Completions.Single().GetProperty("platforms")
                    .EnumerateArray().Any(item => item.GetString() == "tx"), "one shared completion for the missing platform");
                f.App.Failures.Clear();
                int plans = f.App.Plans.Count;
                await f.Replay();
                Check(f.Module.CurrentSource == "网易云音乐" && f.App.Plans.Count == plans + 1,
                    "transient source rejection is limited to the failed playback attempt");
            });
        }
        yield return Case("no existing mapping discovers without inventing an unavailable source", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HideKnown = true;
            await f.Play(Song("first"));
            Check(f.Output.NetworkOpens == 1 && f.App.Plans.Count == 2 &&
                f.App.Completions.Count == 1 &&
                Platforms(f.App.Resolutions).SequenceEqual(["tx"]), f.Module.Status);
        });
        yield return Case("changed quality during known resolution cannot open the stale result or discover", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HoldResolution = true;
            var play = f.Play(Song("first"));
            await f.App.ResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.SetQuality("128k");
            f.App.ResolutionRelease.TrySetResult();
            await play.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.NetworkOpens == 0 && f.App.Plans.Count == 1 && !f.Output.HasMedia,
                "current quality authority must be checked before accepting a known URL");
        });
        yield return Case("account revocation during known resolution cannot open its late result", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HoldResolution = true;
            var play = f.Play(Song("first"));
            await f.App.ResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.App.Revoke("wy");
            f.App.ResolutionRelease.TrySetResult();
            await play.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.NetworkOpens == 0 && !f.Output.HasMedia && f.App.Plans.Count == 1);
        });
        yield return Case("opening source list is cache-only and load more keeps playback and queue", async () =>
        {
            await using var f = await Scene.Create(files: true);
            var song = Song("first");
            await f.SeedFile(song, "wy");
            await f.Play(song, Song("next"));
            string? identity = f.Output.Identity;
            string queue = await f.Queue();
            int plans = f.App.Plans.Count, resolutions = f.App.Resolutions.Count, transfers = f.Files.Requests;
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenQuality(); surface.List();
            Check(f.App.Plans.Count == plans && f.App.Resolutions.Count == resolutions && f.App.Completions.Count == 0,
                "opening quality controls and source list never resolves or searches");
            surface.Search();
            await Until(() => f.Module.CanLoadMoreSources && f.App.Completions.Count == 1);
            Check(f.Module.SourceChoices.Any(row => row.Platform == "QQ") && f.Module.CurrentSource == "网易云音乐" &&
                f.Output.Identity == identity && f.Output.ReadOpens == 1 && f.Output.NetworkOpens == 0 && f.Files.Requests == transfers);
            Check(await f.Queue() == queue, "exploring sources does not mutate the queue");
            Check(await f.Files.Cache.ReadInfoAsync("fixture", LJ.S(song["songId"]), Signature("wy"), default) is not null);
        });
        yield return Case("closing the real quality popup cancels discovery and discards a noncooperative late reply", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"));
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenQuality(); surface.List();
            f.App.HoldDiscovery = true;
            surface.Search();
            await f.App.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            surface.Popup.IsOpen = false;
            // WPF destroys the popup window and raises Closed from its dispatcher, not inside the IsOpen setter.
            await Until(() => f.App.DiscoveryToken.IsCancellationRequested);
            f.App.DiscoveryRelease.TrySetResult();
            await Until(() => !f.Module.Busy);
            Check(!f.Module.SourceChoices.Any(row => row.Platform == "QQ") &&
                f.Output.NetworkOpens == 1 && f.Module.CurrentSource == "网易云音乐",
                "a retired search cannot publish candidates or replace the accepted input");
        });
        yield return Case("changing song cancels source search and prevents its old candidates reaching the next occurrence", async () =>
        {
            await using var f = await Scene.Create();
            var next = Song("next");
            await f.Play(Song("first"), next);
            f.App.HoldDiscovery = true;
            f.Module.OpenSourceChoices();
            var search = f.Module.LoadMoreSourcesAsync();
            await f.App.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var step = f.Module.StepAsync(true);
            Check(f.App.DiscoveryToken.IsCancellationRequested, "changing song must retire the discovery owner");
            f.App.DiscoveryRelease.TrySetResult();
            await Task.WhenAll(search, step).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Module.CurrentTitle == LJ.S(next["title"]) && f.Output.NetworkOpens == 2 &&
                !f.Module.SourceChoices.Any(row => row.Platform == "QQ"));
        });
        yield return Case("next-song admission cancels known resolution without starting catalog discovery for the retired song", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HoldResolution = true;
            var play = f.Play(Song("first"), Song("next"));
            await f.App.ResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var step = f.Module.StepAsync(true);
            Check(f.App.ResolutionToken.IsCancellationRequested);
            f.App.HoldResolution = false;
            f.App.ResolutionRelease.TrySetResult();
            await Task.WhenAll(play, step).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.NetworkOpens == 1 && f.Module.CurrentTitle == "Track next" &&
                f.App.Completions.Count == 0);
        });
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("official playback scope: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static void Check(bool value, string message = "official playback scope assertion")
    { if (!value) throw new InvalidOperationException(message); }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static string[] Platforms(IEnumerable<JsonElement> calls) =>
        calls.Select(call => call.GetProperty("platform").GetString()!).ToArray();
    private static string Signature(string platform) => new(platform == "wy" ? 'b' : platform == "tx" ? 'c' : 'd', 64);
    private static string Key(JsonObject song, string platform) => song["sources"]!.AsArray()
        .OfType<JsonObject>().Single(row => LJ.S(row["source"]) == platform)["sourceKey"]!.GetValue<string>();
    private static JsonObject Song(string id, bool alternate = false)
    {
        var song = MusicSongFixture.Online("wy", id, "Track " + id, "Artist", duration: 1);
        if (alternate)
            song["sources"]!.AsArray().Add(MusicSongFixture.Online("tx", id + "Tx",
                LJ.S(song["title"]), "Artist", duration: 1)["sources"]![0]!.DeepClone());
        return NativeMusicSong.Normalize(song);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("official playback scope");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!predicate()) await Task.Delay(1);
        }
    }
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly MusicPlaybackCacheTests.Fixture Files = new();
        internal readonly Output Output = new();
        internal readonly OfficialApp App;
        internal readonly MusicCapability Module;
        private Scene(bool files)
        {
            Files.Clock.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            App = new(new MusicNativeApplication(Files.Store, new NoNode(), new NoHttp()), Files);
            Module = new(App, Files.Store, Output, files ? Files.Cache : null, new Dialogs());
        }
        internal static async Task<Scene> Create(bool files = false)
        {
            var scene = new Scene(files);
            await scene.Module.InitializeAsync(default);
            await scene.SetQuality("flac");
            await scene.App.Ok("music.settings.save", new { ifRevision = 0, lyrics = false });
            await Until(() => !scene.Module.Busy);
            return scene;
        }
        internal async Task SetQuality(string request)
        {
            var current = await App.Ok("music.quality.settings.get");
            var policy = JsonNode.Parse(current.GetProperty("settings").GetRawText())!.AsObject();
            policy["ifRevision"] = policy["revision"]!.DeepClone();
            policy.Remove("schema"); policy.Remove("revision");
            policy["request"] = request;
            await App.Ok("music.quality.settings.save", policy);
        }
        internal async Task SeedFile(JsonObject song, string platform)
        {
            var info = await App.Receipt(song, platform);
            info["full"] = true;
            info["size"] = Files.Bytes.Length;
            info["licenseExpiresAt"] = Files.Clock.Now + 3600000;
            info["authorizationExpiresAt"] = info["licenseExpiresAt"]!.DeepClone();
            info["offlineAllowed"] = true;
            await Files.Cache.StorePlayedAsync("fixture", LJ.S(song["songId"]), Signature(platform),
                Element(info), () => true, default);
            Check(await Files.Cache.ReadInfoAsync("fixture", LJ.S(song["songId"]), Signature(platform), default) is not null);
        }
        internal async Task Play(params JsonObject[] songs)
        {
            await App.Ok("music.queue.replace", new { songs, index = 0 });
            await Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await Module.PlayQueueSurfaceItemAsync(Module.QueueSurfaceRows.First());
        }
        // A new playback attempt of the current occurrence through the module's own queue command.
        internal async Task Replay()
        {
            await Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default); // settles a pending row reload
            await Module.PlayQueueSurfaceItemAsync(Module.QueueSurfaceRows.First());
        }
        internal async Task<string> Queue() =>
            (await App.Ok("music.queue.get", new { offset = 0, limit = 10 })).GetRawText();
        public async ValueTask DisposeAsync()
        {
            App.DiscoveryRelease.TrySetResult(); App.ResolutionRelease.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Files.DisposeAsync(); }
        }
    }
    private sealed class OfficialApp(MusicNativeApplication inner, MusicPlaybackCacheTests.Fixture files)
        : MusicApplicationForwarder(inner)
    {
        internal readonly List<JsonElement> Plans = [], Resolutions = [], Completions = [];
        internal readonly Dictionary<string, string> Failures = [];
        internal readonly MusicSourceCache Urls = new(files.Store, files.Clock);
        internal readonly TaskCompletionSource DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DiscoveryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ResolutionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ResolutionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldDiscovery, HoldResolution, HideKnown, EnabledScript;
        internal string MediaQuality = "flac";
        internal int MediaLookups, Authorizations;
        internal CancellationToken DiscoveryToken, ResolutionToken;
        private readonly HashSet<string> revoked = [];
        private event Action<string>? invalidated;
        public override event Action<string>? AccountDetailsInvalidated
        {
            add { invalidated += value; Inner.AccountDetailsInvalidated += value; }
            remove { invalidated -= value; Inner.AccountDetailsInvalidated -= value; }
        }
        internal void Revoke(string platform)
        {
            revoked.Add(platform);
            invalidated?.Invoke(platform);
        }
        public override bool IsOfficialPlaybackCurrent(string platform, string scope, long generation) =>
            scope == "fixture" && generation == 7 && !revoked.Contains(platform);
        internal async Task<JsonElement> Ok(string method, object? parameters = null)
        {
            var reply = await ExecuteAsync(method, parameters);
            Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
            return reply;
        }
        internal async Task<JsonObject> Receipt(JsonObject song, string platform)
        {
            var policy = await inner.ExecuteAsync("music.quality.settings.get");
            string request = policy.GetProperty("settings").GetProperty("request").GetString()!;
            return new()
            {
                ["ok"] = true, ["kind"] = "official", ["platform"] = platform,
                ["songId"] = song["songId"]!.DeepClone(), ["sourceKey"] = Key(song, platform),
                ["scope"] = "fixture", ["generation"] = 7, ["quality"] = request == "auto" ? MediaQuality : request,
                ["requestedQuality"] = request, ["actualQuality"] = MediaQuality, ["qualityVerified"] = true,
                ["playbackVerified"] = false, ["qualityPolicySignature"] = policy.GetProperty("signature").GetString(),
                ["qualityCacheSignature"] = Signature(platform),
                ["url"] = new Uri(files.Origin, platform + ".wav").AbsoluteUri,
                ["headers"] = new JsonObject(), ["expiresAt"] = files.Clock.Now + 60000,
                ["duration"] = 1d, ["size"] = 0, ["full"] = false, ["licenseExpiresAt"] = null,
                ["delivery"] = platform + "-official", ["offlineAllowed"] = false, ["authorizationExpiresAt"] = null
            };
        }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            var input = Element(parameters ?? new { });
            if (method == "music.sources.list")
                return Element(new { ok = true, revision = 1, items = EnabledScript ? new object[] {
                    new { installationId = "higher-quality-script", name = "Script", enabled = true, executionHash = new string('a', 64),
                        sites = new { wy = new { actions = new[] { "musicUrl" }, qualitys = new[] { "master" } } } }
                } : [] });
            if (EnabledScript && method is "music.sources.detail" or "music.online.resolve")
                throw new InvalidOperationException("playable cache or known mapping must not probe scripts");
            if (method == "music.songs.complete")
            {
                Completions.Add(input); DiscoveryToken = cancellationToken; DiscoveryEntered.TrySetResult();
                if (HoldDiscovery) await DiscoveryRelease.Task;
                var raw = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                if (!raw["sources"]!.AsArray().OfType<JsonObject>().Any(row => LJ.S(row["source"]) == "tx"))
                    raw["sources"]!.AsArray().Add(MusicSongFixture.Online("tx", "Discovered", LJ.S(raw["title"]),
                        LJ.S(raw["artist"]), LJ.S(raw["album"]), LJ.N(raw["duration"]))["sources"]![0]!.DeepClone());
                return Element(new { ok = true, song = NativeMusicSong.Normalize(raw) });
            }
            if (method == "music.official.candidates")
            {
                Plans.Add(input);
                var raw = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                var policy = await inner.ExecuteAsync("music.quality.settings.get");
                return Element(new { ok = true, song = raw,
                    qualityPolicySignature = policy.GetProperty("signature").GetString(),
                    candidates = raw["sources"]!.AsArray().OfType<JsonObject>()
                        .Where(row => !(HideKnown && LJ.S(row["source"]) == "wy"))
                        .Select(row => new { platform = LJ.S(row["source"]), sourceKey = LJ.S(row["sourceKey"]),
                            scope = "fixture", generation = 7 }), authorizationRequired = false });
            }
            if (method == "music.official.cacheContext")
                return Element(new { ok = true, scope = "fixture", generation = 7,
                    sourceKey = input.GetProperty("sourceKey").GetString(),
                    qualityCacheSignature = Signature(input.GetProperty("platform").GetString()!) });
            if (method == "music.official.resolve")
            {
                string platform = input.GetProperty("platform").GetString()!;
                var song = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                var cached = await Urls.ReadAsync("fixture", LJ.S(song["songId"]), Signature(platform), cancellationToken);
                if (input.TryGetProperty("cacheOnly", out var cacheOnly) && cacheOnly.GetBoolean() && cached is null)
                    return Element(new { ok = false, code = "CACHE_MISS" });
                Resolutions.Add(input);
                var info = await Receipt(song, platform);
                info["quality"] = input.GetProperty("quality").GetString();
                info["requestedQuality"] = info["quality"]!.DeepClone();
                ResolutionToken = cancellationToken; ResolutionEntered.TrySetResult();
                if (HoldResolution) await ResolutionRelease.Task; // Exercise post-await account/policy/cancel checks.
                if (Failures.TryGetValue(platform, out var code)) return Element(new { ok = false, code });
                if (cached is not null) return Element(cached);
                MediaLookups++;
                return Element(info);
            }
            if (method == "music.official.authorize")
            { Authorizations++; return Element(new { ok = true }); }
            if (method == "music.official.confirm") return Element(new { ok = true });
            return await inner.ExecuteAsync(method, parameters, cancellationToken);
        }
    }
    private sealed class PlayerSurface : IDisposable
    {
        private readonly MusicPlayerView view;
        private readonly Window window;
        internal Popup Popup => (Popup)view.FindName("QualityPopup");
        internal PlayerSurface(MusicCapability module)
        {
            view = (MusicPlayerView)module.PlayerView;
            window = new Window { Content = view, Width = 1000, Height = 160, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show(); window.UpdateLayout();
        }
        internal async Task OpenQuality()
        {
            ((Button)view.FindName("QualityButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(Popup.IsOpen, "actual quality popup must open");
        }
        internal void List() => ((Button)view.FindName("SourceListButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        internal void Search()
        {
            var button = (Button)view.FindName("LoadMoreSourcesButton");
            Check(button.IsVisible && button.IsEnabled, "source search must be a usable UI action");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        public void Dispose()
        {
            Popup.IsOpen = false;
            window.Content = null; window.Close();
        }
    }
    private sealed class Output : IMusicPlayback, INativePlaybackContinuation, INativePlaybackHandoff
    {
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying { get; private set; }
        public double PositionSeconds => 0;
        public double DurationSeconds => 1;
        public int Volume { get; set; } = 50;
        public bool PauseRequested => !IsPlaying;
        public string? LastErrorCode => null;
        internal int NetworkOpens, ReadOpens;
        internal Uri? LastUri;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? Started;
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        {
            NetworkOpens++; LastUri = uri;
            return Open(owner, identity, token);
        }
        public async Task OpenReadAsync(string owner, string identity, INativeMediaReadLease lease,
            string title, CancellationToken token = default)
        {
            await using (lease) Check(lease.Length > 0 && lease.Content.ReadByte() >= 0);
            ReadOpens++;
            await Open(owner, identity, token);
        }
        private Task Open(string owner, string identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OwnerId = owner; Identity = identity; HasMedia = IsPlaying = true;
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            Started?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            HasMedia = IsPlaying = false; OwnerId = Identity = null;
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            return Task.CompletedTask;
        }
        public Task PauseAsync() { IsPlaying = false; return Task.CompletedTask; }
        public Task ResumeAsync() { IsPlaying = HasMedia; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
        public double PreparedHandoffDurationSeconds => 0;
        public Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
            throw new InvalidOperationException("exploration must not prepare playback");
        public Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token) =>
            throw new InvalidOperationException("exploration must not prepare playback");
        public async Task ReopenReadAsync(NativePlaybackContinuation continuation, INativeMediaReadLease lease,
            CancellationToken token = default)
        {
            await lease.DisposeAsync();
            throw new InvalidOperationException("no read continuation during exploration");
        }
        public Task CommitHandoffAsync(Action committed, CancellationToken token) => throw new InvalidOperationException("no handoff during exploration");
        public Task DiscardHandoffAsync() => Task.CompletedTask;
        public NativePlaybackContinuation? CaptureContinuation() => null;
        public Task ReopenAsync(NativePlaybackContinuation continuation, Uri uri,
            IReadOnlyDictionary<string, string> headers, CancellationToken token = default) =>
            throw new InvalidOperationException("source discovery must never reopen playback");
    }
    private sealed class NoNode : IRuntimeClient
    {
        public Task<JsonElement> CallAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("NO_NODE_ALLOWED");
    }
    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("NO_UPSTREAM_ALLOWED");
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
}
