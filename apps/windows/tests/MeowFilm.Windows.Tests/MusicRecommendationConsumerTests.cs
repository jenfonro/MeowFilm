using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicRecommendationConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.RecommendationConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> RecommendationConsumerCases()
    {
        yield return Case("recommendation native FM preview starts no session and explicit play consumes same queue and LX port", async () =>
        {
            await using var f = await RecommendationScene.Create();
            await f.Module.NavigateAsync("catalog-fm", default);
            Equal(0, (await f.Queue()).GetProperty("total").GetInt32());
            True(!f.Module.HasActiveRecommendation && !f.Runtime.HasStarted, "preview remains a native read");
            await f.EnableScript();
            await f.Module.PlayCatalogPlaylistAsync();
            await RecommendationUntil(() => f.Module.HasActiveRecommendation && !f.Module.RecommendationLoading);
            Equal(3, (await f.Queue()).GetProperty("total").GetInt32());
            Equal(1, f.Playback.Opens.Count);
            True(f.Runtime.Calls.All(m => m.StartsWith("music.script.", StringComparison.Ordinal)), "only actual LX uses Node");
            string id = (await f.Queue()).GetProperty("currentId").GetString()!;
            Equal(id, f.Playback.Identity);
            f.Playback.Start(); await Drain();
            await f.Module.StepAsync(true);
            Equal(2, f.Playback.Opens.Count);
            True((await f.Queue()).GetProperty("currentId").GetString() != id, "same native queue advances");
        });
        yield return Case("recommendation actual natural end appends at native tail once and opens next occurrence", async () =>
        {
            await using var f = await RecommendationScene.Create(); await f.EnableScript(); await f.Start("fm");
            f.Playback.Start();
            await f.Module.StepAsync(true); f.Playback.Start();
            await f.Module.StepAsync(true);
            int before = f.Http.Batches;
            f.Http.Hold = true;
            var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void AppendPublished(JsonElement snapshot)
            {
                if (snapshot.GetProperty("batch").GetInt32() > before && !snapshot.GetProperty("loading").GetBoolean())
                    appended.TrySetResult();
            }
            f.App.Changed += AppendPublished;
            try
            {
                f.Playback.Start();
                await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(3, (await f.Queue()).GetProperty("total").GetInt32());
                True(!appended.Task.IsCompleted, "HTTP admission is not a committed recommendation append");
                f.Http.Release.TrySetResult();
                await appended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { f.App.Changed -= AppendPublished; }
            Equal(6, (await f.Queue()).GetProperty("total").GetInt32());
            f.Playback.End(); f.Playback.End();
            await RecommendationUntil(() => f.Playback.Opens.Count == 4);
            Equal(4, f.Playback.Opens.Count);
            Equal(before + 1, f.Http.Batches);
        });
        yield return Case("recommendation similar retains current occurrence then returns original queue paused without replay", async () =>
        {
            await using var f = await RecommendationScene.Create(); await f.EnableScript(); await f.Start("fm");
            f.Playback.Start(); await f.Module.CancelRecommendationAsync();
            var before = await f.Queue();
            string seed = MusicSongFixture.NativeId(before.GetProperty("current").GetProperty("song"));
            string original = before.GetProperty("currentId").GetString()!;
            int opens = f.Playback.Opens.Count;
            await f.Call("music.recommendations.start", new { kind = "similar", platform = "wy", sourceIds = new[] { seed } });
            await RecommendationUntil(() => f.Module.RecommendationTitle == "相似推荐" && !f.Module.RecommendationLoading);
            Equal(original, (await f.Queue()).GetProperty("currentId").GetString());
            Equal(opens, f.Playback.Opens.Count);
            await f.Module.StepAsync(true); Equal(opens + 1, f.Playback.Opens.Count);
            await f.Module.ReturnRecommendationAsync();
            Equal(original, (await f.Queue()).GetProperty("currentId").GetString());
            Equal(before.GetProperty("entries").GetRawText(), (await f.Queue()).GetProperty("entries").GetRawText());
            Equal(opens + 1, f.Playback.Opens.Count);
            True(!f.Playback.IsPlaying && !f.Module.HasActiveRecommendation, "restoration is not a fresh listen");
        });
        yield return Case("recommendation cancel control physically drains pending native HTTP and never admits its late queue", async () =>
        {
            await using var f = await RecommendationScene.Create();
            await f.Module.NavigateAsync("catalog-fm", default);
            f.Http.Hold = true;
            var play = f.Module.PlayCatalogPlaylistAsync();
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RecommendationUntil(() => f.Module.CanCancelRecommendation);
            var cancel = f.Module.CancelRecommendationAsync();
            await f.Http.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            True(!cancel.IsCompleted && !play.IsCompleted, "real handler still owns the read");
            f.Http.Release.TrySetResult(); await Task.WhenAll(play, cancel);
            Equal(0, (await f.Queue()).GetProperty("total").GetInt32()); Equal(0, f.Playback.Opens.Count);
            True(!f.Runtime.HasStarted && !f.Module.HasActiveRecommendation, "late read not a queue or Node request");
        });
        yield return Case("recommendation module close retires actual batch before waiting command tail and resumes without autoplay", async () =>
        {
            await using var f = await RecommendationScene.Create();
            await f.Module.NavigateAsync("catalog-fm", default);
            f.Http.Hold = true; var play = f.Module.PlayCatalogPlaylistAsync();
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = f.Module.PrepareShutdownAsync();
            await f.Http.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            True(!close.IsCompleted, "physical request outstanding");
            f.Http.Release.TrySetResult(); await Task.WhenAll(play, close);
            int calls = f.Http.Batches;
            await f.Module.CancelShutdownPreparationAsync();
            Equal(calls, f.Http.Batches); Equal(0, f.Playback.Opens.Count);
            True(!f.Module.HasActiveRecommendation, "closed recommendation is not restored");
            await f.Call("music.library.create", new { name = "恢复后的本地歌单" });
        });
        yield return Case("recommendation external native queue replacement fences delayed batch without replay", async () =>
        {
            await using var f = await RecommendationScene.Create();
            await f.Module.NavigateAsync("catalog-fm", default);
            f.Http.Hold = true; var play = f.Module.PlayCatalogPlaylistAsync();
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Call("music.queue.replace", new { songs = new[] { RecommendationSong("77") }, index = 0 });
            f.Http.Release.TrySetResult(); await play;
            var queue = await f.Queue();
            Equal(1, queue.GetProperty("total").GetInt32());
            Equal("77", MusicSongFixture.NativeId(queue.GetProperty("current").GetProperty("song")));
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("recommendation real WPF session controls toggle continuation and return without explanatory content", async () =>
        {
            await using var f = await RecommendationScene.Create(); await f.EnableScript();
            await f.Start("similar");
            await RecommendationUntil(() => f.Module.HasActiveRecommendation && !f.Module.RecommendationLoading);
            var view = (System.Windows.FrameworkElement)f.Module.View;
            var host = new Border { Background = (System.Windows.Media.Brush)view.FindResource("MeowFilm.Brush.Window"), Child = view };
            Render(host, 1000, 720);
            var controls = Visuals(host).OfType<MusicRecommendationView>().Single();
            var toggle = Visuals(controls).OfType<CheckBox>().Single();
            True(toggle.IsEnabled && toggle.IsChecked != true, "default continuation off");
            toggle.IsChecked = true;
            toggle.RaiseEvent(new(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await RecommendationUntil(() => f.Module.RecommendationContinue);
            var back = Visuals(controls).OfType<Button>().Single(b => Equals(b.Content, "返回原队列"));
            True(back.IsEnabled, "actual return action");
            back.RaiseEvent(new(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await RecommendationUntil(() => !f.Module.HasActiveRecommendation);
            Equal(0, (await f.Queue()).GetProperty("total").GetInt32());
        });
        yield return Case("recommendation feedback carries live native provenance but never resurrects it from durable queue", async () =>
        {
            await using var f = await RecommendationScene.Create(); await f.EnableScript();
            await f.App.Feedback.ReadSettingsAsync(); await f.App.Feedback.SaveSettingsAsync(0, ["wy"]);
            await f.Start("fm"); f.Playback.Start();
            await RecommendationUntil(() => f.Reports.Starts.Count == 1);
            var start = f.Reports.Starts.Single();
            Equal("userfm", start.Source); Equal("playFm", start.Mode); Equal("native-recommendation-alg", start.Algorithm);
            await f.Module.CancelRecommendationAsync();
            await f.Module.StepAsync(true); f.Playback.Start();
            await RecommendationUntil(() => f.Reports.Starts.Count == 2);
            var manual = f.Reports.Starts.Last();
            True(manual.Source != "userfm" && manual.Mode != "playFm" && manual.Algorithm.Length == 0,
                "persistent occurrence is not a live FM proof");
        });
        yield return Case("recommendation account retirement stops the exact media without affecting local library access", async () =>
        {
            await using var f = await RecommendationScene.Create(); await f.EnableScript(); await f.Start("fm");
            f.Playback.Start();
            await f.Call("music.accounts.logout", new { platform = "wy" });
            await RecommendationUntil(() => !f.Module.HasActiveRecommendation && !f.Playback.IsPlaying);
            Equal(1, f.Playback.Opens.Count);
            await f.Call("music.library.create", new { name = "本地仍可用" });
        });
        foreach (bool persisted in new[] { false, true })
            yield return Case("recommendation cancelled close exposes explicit unknown queue reconciliation persisted " + persisted, async () =>
            {
                await using var f = await RecommendationScene.Create();
                await f.Module.NavigateAsync("catalog-fm", default);
                f.Store.UnknownNextLibraryWrite = persisted;
                await f.Module.PlayCatalogPlaylistAsync();
                await RecommendationUntil(() => f.Module.CanReconcileRecommendation);
                int batches = f.Http.Batches, writes = f.Store.LibraryWrites;
                Exception? closeError = null;
                try { await f.Module.PrepareShutdownAsync(); } catch (Exception error) { closeError = error; }
                True(closeError is not null, "an unknown queue write cannot pass close preparation");
                await f.Module.CancelShutdownPreparationAsync();
                await RecommendationUntil(() => f.Module.CanReconcileRecommendation);
                Equal("保存结果待核对", f.Module.RecommendationStatus);
                Equal(batches, f.Http.Batches); Equal(writes, f.Store.LibraryWrites);
                Equal(0, f.Playback.Opens.Count);
                var view = (System.Windows.FrameworkElement)f.Module.View;
                var host = new Border { Background = (System.Windows.Media.Brush)view.FindResource("MeowFilm.Brush.Window"), Child = view };
                Render(host, 1000, 720);
                var controls = Visuals(host).OfType<MusicRecommendationView>().Single();
                var reconcile = Visuals(controls).OfType<Button>().Single(b => Equals(b.Content, "核对保存结果"));
                True(reconcile.IsEnabled, "reconciliation is reachable after cancelling failed close");
                reconcile.RaiseEvent(new(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await RecommendationUntil(() => !f.Module.CanReconcileRecommendation);
                Equal(persisted ? 3 : 0, (await f.Queue()).GetProperty("total").GetInt32());
                Equal(batches, f.Http.Batches); Equal(writes, f.Store.LibraryWrites);
                Equal(0, f.Playback.Opens.Count);
                True(!f.Module.HasActiveRecommendation, "reconciliation does not restart recommendation playback");
                await f.Call("music.library.create", new { name = "核对后本地歌单" });
            });
    }

    private static async Task RecommendationUntil(Func<bool> done)
    {
        for (int n = 0; n < 500; n++)
        { if (done()) return; await Task.Delay(10); await Drain(); }
        throw new InvalidOperationException("Recommendation consumer did not settle");
    }
    private static object RecommendationSong(string id) =>
        MusicSongFixture.Online("wy", id, "推荐歌曲" + id, "合成歌手", "合成专辑", 180);
    private sealed class RecommendationScene : IAsyncDisposable
    {
        internal readonly string Root = NewRoot();
        internal readonly RecommendationStore Store = new();
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly RecommendationHttp Http = new();
        internal readonly RecommendationReports Reports = new();
        internal readonly PlaybackProbe Playback = new();
        internal readonly Dialogs Dialog = new();
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        private RecommendationScene()
        {
            App = new(Store, Runtime, Http, (_, _) => Reports); Runtime.Register(App);
            Module = new(App, Store, Playback, Dialog);
        }
        internal static async Task<RecommendationScene> Create()
        {
            var f = new RecommendationScene();
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, entries = new[] {
                    new { key = "[\"meowfilm\",\"music\",1,\"platform-account\",\"wy\"]", value = new {
                        version = 1, generation = 1, cookie = "MUSIC_U=SYNTHETIC_RECOMMENDATION; __csrf=SYNTHETIC_CSRF",
                        profile = new { platform = "wy", userId = "900", displayName = "合成账号" },
                        scope = "synthetic-recommendation", validatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } } } });
                try { await f.Store.WriteAsync(MusicAccountOwner.DocumentNamespace, bytes, DocumentFingerprint.Missing); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                await f.Module.InitializeAsync(default); await f.Module.ReadAccountsAsync(); await Drain();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task<JsonElement> Call(string method, object? input = null)
        {
            var result = await App.ExecuteAsync(method, input ?? new { });
            True(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText()); return result;
        }
        internal Task<JsonElement> Queue() => Call("music.queue.get", new { offset = 0, limit = 100 });
        internal async Task Start(string kind)
        {
            var input = kind == "fm" ? JsonSerializer.SerializeToElement(new { kind, platform = "wy" }) :
                JsonSerializer.SerializeToElement(new { kind, platform = "wy", sourceIds = new[] { "77" } });
            var response = await Call("music.recommendations.start", input);
            Equal("", response.GetProperty("error").GetString());
            await RecommendationUntil(() => Module.HasActiveRecommendation && !Module.RecommendationLoading);
        }
        internal async Task EnableScript()
        {
            Dialog.ScriptPath = Path.Combine(Root, "recommendation-authored-lx.js");
            File.WriteAllText(Dialog.ScriptPath, TestScript(), new UTF8Encoding(false));
            await Module.ImportSourceAsync(); Equal(1, Module.Sources.Count);
            Module.SelectedSource = Module.Sources.Single();
            await Module.LoadSourceAsync(Module.SelectedSource); await Module.SourceActionAsync("enable");
            // Source setup does not load the native quality snapshot. The new
            // SaveOnlineSettings guard correctly refuses an uninitialized draft.
            await Module.RefreshOnlineAsync();
            True(Module.CanEditQuality, "recommendation setup loaded native quality policy before target save");
            var before = await Call("music.quality.settings.get");
            string installation = Module.SelectedSource!.InstallationId;
            await Module.UseSourcePlatformAsync("wy");
            True(Module.Status.Length == 0, "script target accepted");
            // Read the real owner's receipt; an empty UI status alone cannot
            // prove the save was admitted or that a playback target exists.
            var saved = await Call("music.quality.settings.get");
            Equal(before.GetProperty("settings").GetProperty("revision").GetInt64(),
                saved.GetProperty("settings").GetProperty("revision").GetInt64());
            True(saved.GetProperty("signature").GetString() != before.GetProperty("signature").GetString(),
                "recommendation setup accepted a new native quality signature");
            Equal("320k", saved.GetProperty("settings").GetProperty("request").GetString());
            var online = saved.GetProperty("onlineSettings");
            True(online.GetProperty("target").ValueKind == JsonValueKind.Object,
                "recommendation setup persisted an explicit native playback target");
            Equal(installation, online.GetProperty("target").GetProperty("installationId").GetString());
            Equal("wy", online.GetProperty("target").GetProperty("platform").GetString());
            True(!online.TryGetProperty("quality", out _), "online target must not duplicate native quality policy");
        }
        public async ValueTask DisposeAsync()
        {
            Http.Release.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await Store.DisposeAsync(); }
        }
    }
    private sealed class RecommendationStore : IDocumentStore
    {
        private readonly LocalDocumentStore inner = ServiceStore();
        internal bool? UnknownNextLibraryWrite;
        internal int LibraryWrites;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(ns, cancellationToken);
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> value,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            if (ns != MusicLibraryOwner.DocumentNamespace)
                return await inner.WriteAsync(ns, value, expected, cancellationToken);
            LibraryWrites++;
            bool? unknown = UnknownNextLibraryWrite; UnknownNextLibraryWrite = null;
            if (unknown == false) throw new DocumentStoreException(DocumentStoreError.Storage);
            var receipt = await inner.WriteAsync(ns, value, expected, cancellationToken);
            if (unknown == true) throw new DocumentStoreException(DocumentStoreError.Storage);
            return receipt;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class RecommendationHttp : HttpMessageHandler
    {
        internal int Batches;
        internal bool Hold;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            True(uri.Scheme == "https" && uri.Host is "music.163.com" or "interfacepc.music.163.com", "fixed native platform");
            object response;
            if (uri.AbsolutePath.EndsWith("/w/nuser/account/get", StringComparison.Ordinal))
                response = new { code = 200, account = new { id = "900" }, profile = new { userId = "900", nickname = "合成账号" } };
            else
            {
                True(uri.AbsolutePath.EndsWith("/v1/radio/get", StringComparison.Ordinal), "exact recommendation API");
                int batch = Interlocked.Increment(ref Batches);
                using var registration = token.Register(() => Cancelled.TrySetResult());
                if (Hold) { Entered.TrySetResult(); await Release.Task; }
                response = new { code = 200, data = Enumerable.Range(batch * 1000 + 1, 3).Select(id => new {
                    id, name = "推荐歌曲" + id, ar = new[] { new { id = 1, name = "合成歌手" } },
                    al = new { id = 2, name = "合成专辑", picUrl = "" }, dt = 180000, fee = 0, alg = "native-recommendation-alg" }) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class RecommendationReports : IMusicFeedbackProtocol
    {
        internal readonly ConcurrentQueue<MusicFeedbackStart> Starts = new();
        public Task<IMusicFeedbackSession> StartAsync(MusicPlatformSession account, MusicFeedbackStart start,
            CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Starts.Enqueue(start); return Task.FromResult<IMusicFeedbackSession>(new Report(start.Recording)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Report(MusicFeedbackRecording recording) : IMusicFeedbackSession
        {
            public MusicFeedbackRecording Recording => recording;
            public Task FinishAsync(MusicFeedbackEnd end, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
