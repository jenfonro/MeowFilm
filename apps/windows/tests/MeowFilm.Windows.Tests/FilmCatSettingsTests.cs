using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class FilmCatSettingsTests
{
    private const string CatNamespace = "film.cat-sources.v1";

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("offline repeated edits survive a new owner without starting or notifying Node", Offline);
        yield return Case("source edits isolate typed bad siblings and ignore unused gateway and network file inputs", TypedInputs);
        yield return Case("file reference hash and revisions describe intent without storing script text", FileIntent);
        yield return Case("duplicate adds and invalid or cancelled selections preserve committed intent", InvalidSelection);
        yield return Case("native CAS conflict failure and bad receipts never notify or publish an uncommitted input", CommitFailures);
        yield return Case("cancellation after write admission cannot revoke the native receipt", AdmittedSave);
        yield return Case("lost native write receipts preserve actual committed intent without replaying add", LostNativeReceipt);
        yield return Case("ready and starting owners notify only the block without waiting for application", Notifications);
        yield return Case("late runtime status cannot override newer configured type name enabled state or revision", StaleStatus);
        yield return Case("shutdown drains notification cleanup without disposing borrowed runtime or storage", Shutdown);
    }

    private static async Task Offline()
    {
        foreach (var state in new[] { RuntimeConnectionState.Dormant, RuntimeConnectionState.Closed, RuntimeConnectionState.Failed })
        {
            var store = new Store();
            var runtime = new Runtime(store) { State = state };
            using var file = new SourceFile("module.exports = { search: async () => ({list:[]}) };");
            await using (var service = new FilmSourceService(runtime, store))
            {
                await service.ImportLocalAsync("初始", file.Path, "cat");
                await service.EnableCatAsync("cat", false);
                await service.EditCatAsync("cat", "网络一", "network", "https://fixture.invalid/one.js", null);
                await service.EditCatAsync("cat", "网络二", "network", "https://fixture.invalid/two.js", null);
                await service.ImportNetworkAsync("待删", "https://fixture.invalid/deleted.js", "deleted");
                await service.DeleteCatAsync("deleted");
                Check(service.Cats.Single() is { Name: "网络二", InputRevision: 3, Enabled: false, CanDetect: false },
                    "ordinary saves keep the final source identity and disabled intent");
            }
            await using var reopened = new FilmSourceService(runtime, store);
            await reopened.InitializeAsync();
            Check(reopened.Cats.Single() is { SourceType: "network", SourceUrl: "https://fixture.invalid/two.js",
                    InputRevision: 3, Enabled: false, LocalPath: null } &&
                runtime.Calls.Count == 0 && runtime.Captures == 0 && runtime.Retries == 0,
                "a new owner reads final native settings; offline saves neither start nor queue runtime work");
        }
    }

    private static async Task TypedInputs()
    {
        var store = new Store();
        store.CatBytes = Encoding.UTF8.GetBytes("""
            {"version":1,"port":-1,"providers":{"":true},"mappings":[{"provider":"","aliases":["kept"]}],
             "scripts":[{"installationId":"cat","name":"网络","sourceType":"network","sourceUrl":"https://fixture.invalid/old.js",
              "localPath":"missing-unused-file.js","localHash":"unused","enabled":true,"inputRevision":1},
              {"installationId":"broken","name":"坏本地","sourceType":"local","sourceUrl":null,
               "localPath":"missing.js","localHash":"invalid","enabled":true,"inputRevision":1}]}
            """);
        var before = store.Cat;
        var runtime = new Runtime(store);
        await using var service = new FilmSourceService(runtime, store);
        await service.InitializeAsync();
        await service.EditCatAsync("cat", "已编辑", "network", "https://fixture.invalid/new.js", "missing-unused-file.js");
        var after = store.Cat;
        foreach (string key in new[] { "port", "providers", "mappings" })
            Check(before.GetProperty(key).GetRawText() == after.GetProperty(key).GetRawText(), "unconsumed typed gateway settings remain unchanged");
        Check(JsonElement.DeepEquals(before.GetProperty("scripts")[1], after.GetProperty("scripts")[1]) &&
            service.Cats.Single() is { Name: "已编辑", InputRevision: 2 } && runtime.Calls.Count == 0,
            "a network edit ignores the unused file, preserves bad typed siblings, and advances its real input revision");
        await Code(() => service.ImportNetworkAsync("不覆盖坏身份", "https://fixture.invalid/replacement.js", "broken"), "STORAGE_CONFLICT");
        await service.DeleteCatAsync("cat");
        Check(service.Cats.Count == 0 && JsonElement.DeepEquals(before.GetProperty("scripts")[1], store.Cat.GetProperty("scripts").EnumerateArray().Single()),
            "deleting a healthy target does not save its isolated list over the bad sibling");
        await service.EditCatAsync("broken", "改为网络", "network", "https://fixture.invalid/repaired.js", "unused-file.js");
        Check(service.Cats.Single() is { InstallationId: "broken", InputRevision: 2, SourceType: "network" },
            "an explicit network replacement does not decode the discarded local hash or path");
        await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_cat_settings_scripts SET inputRevision=0 WHERE scripts_key='broken'"));
        await service.DeleteCatAsync("broken");
        Check(store.Cat.GetProperty("scripts").GetArrayLength() == 0,
            "explicit deletion uses the unique installation identity without decoding the discarded payload");
    }

    private static async Task FileIntent()
    {
        var store = new Store();
        var runtime = new Runtime(store);
        using var first = new SourceFile("// 完整🎬\nmodule.exports = {};" + new string('中', 70000));
        using var second = new SourceFile("// 替换\nmodule.exports = {};");
        await using var service = new FilmSourceService(runtime, store);
        await service.ImportLocalAsync("  文件猫  ", first.Path, "cat");
        var script = Input(store);
        Check(script.GetProperty("localPath").GetString() == first.Path &&
            script.GetProperty("localHash").GetString() == Hash(first.Path) &&
            script.GetProperty("name").GetString() == "文件猫" && script.GetProperty("sourceUrl").ValueKind == JsonValueKind.Null &&
            !Encoding.UTF8.GetString(store.CatBytes).Contains("module.exports", StringComparison.Ordinal) &&
            !store.HasSourceSnapshot,
            "native settings contain selected path/hash, never a second source body or video configuration owner");
        await service.EditCatAsync("cat", "改名");
        Check(service.Cats.Single().InputRevision == 1 && Input(store).GetProperty("localHash").GetString() == Hash(first.Path),
            "name-only edits do not trigger replacement");
        await service.UpdateCatAsync("cat", second.Path);
        Check(service.Cats.Single() is { Name: "改名", InputRevision: 2 } &&
            Input(store).GetProperty("localHash").GetString() == Hash(second.Path), "local update saves the new selected file hash");
        await service.UpdateCatAsync("cat", second.Path);
        Check(service.Cats.Single().InputRevision == 2, "same path/hash does not advance replacement identity");
        await service.EditCatAsync("cat", "网络", "network", "https://fixture.invalid/cat.js", null);
        Check(Input(store).GetProperty("localHash").ValueKind == JsonValueKind.Null &&
            service.Cats.Single() is { LocalPath: null, InputRevision: 3 }, "network intent clears the local file reference");
        await service.EditCatAsync("cat", "本地", "local", null, first.Path);
        Check(service.Cats.Single() is { SourceUrl: null, InputRevision: 4 } &&
            Input(store).GetProperty("localHash").GetString() == Hash(first.Path) && runtime.Calls.Count == 0,
            "network-to-local saves the selected input without a runtime upload");
    }

    private static async Task InvalidSelection()
    {
        var store = new Store();
        var runtime = new Runtime(store);
        using var file = new SourceFile("module.exports = {};");
        await using var service = new FilmSourceService(runtime, store);
        await service.ImportNetworkAsync("已有", "https://fixture.invalid/kept.js", "cat");
        byte[] before = store.CatBytes.ToArray();
        await Code(() => service.ImportLocalAsync("覆盖", file.Path, "cat"), "STORAGE_CONFLICT");
        await Code(() => service.ImportNetworkAsync("覆盖", "https://fixture.invalid/new.js", "cat"), "STORAGE_CONFLICT");
        await Code(() => service.EditCatAsync("cat", "缺文件", "local", null, null), "INVALID_ARGUMENT");
        await Code(() => service.ImportNetworkAsync("非法", "file:///cat.js", "new"), "INVALID_ARGUMENT");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            await service.ImportLocalAsync("取消", file.Path, "new", cancel.Token);
            throw new InvalidOperationException("Expected cancellation");
        }
        catch (OperationCanceledException) { }
        Check(store.CatBytes.SequenceEqual(before) && service.Cats.Single().Name == "已有" &&
            runtime.Calls.Count == 0, "failed validation duplicate IDs and pre-admission cancellation leave intent unchanged");
    }

    private static async Task CommitFailures()
    {
        foreach (string fault in new[] { "storage", "conflict", "receipt" })
        foreach (string operation in new[] { "edit", "enable", "delete", "add" })
        {
            var store = new Store();
            var runtime = new Runtime(store);
            await using var service = new FilmSourceService(runtime, store);
            await service.ImportNetworkAsync("已有", "https://fixture.invalid/kept.js", "cat");
            runtime.State = RuntimeConnectionState.Ready;
            byte[] before = store.CatBytes.ToArray();
            store.BeforeWrite = _ =>
            {
                if(fault=="conflict")store.CatBytes=JsonSerializer.SerializeToUtf8Bytes(new {version=1,port=0,providers=new{},mappings=Array.Empty<object>(),
                    scripts=new[]{new {installationId="external",name="外部",sourceType="network",sourceUrl="https://fixture.invalid/external.js",localPath=(string?)null,localHash=(string?)null,enabled=false,inputRevision=1}}});
                return Task.CompletedTask;
            };
            store.WriteError=fault=="conflict"?DocumentStoreError.Conflict:DocumentStoreError.Storage;
            try
            {
                Task change = operation switch
                {
                    "edit" => service.EditCatAsync("cat", "未提交", "network", "https://fixture.invalid/new.js", null),
                    "enable" => service.EnableCatAsync("cat", false),
                    "delete" => service.DeleteCatAsync("cat"),
                    _ => service.ImportNetworkAsync("未提交", "https://fixture.invalid/new.js", "new")
                };
                await change;
                throw new InvalidOperationException("Expected native write failure");
            }
            catch (DocumentStoreException error)
            {
                Check(error.Error == (fault == "conflict" ? DocumentStoreError.Conflict : DocumentStoreError.Storage),
                    "native failure retains its real category");
            }
            Check(service.Cats.Single().Name == "已有" && runtime.Captures == 0 && runtime.Calls.Count == 0,
                "no uncommitted input is published or notified");
            Check(fault == "conflict" ? Input(store).GetProperty("installationId").GetString() == "external" :
                store.CatBytes.SequenceEqual(before), "a conflicting writer is never overwritten");
        }
    }

    private static async Task AdmittedSave()
    {
        var store = new Store();
        var runtime = new Runtime(store);
        var admitted = Signal(); var release = Signal();
        using var cancel = new CancellationTokenSource();
        store.Writing = async token =>
        {
            Check(!token.CanBeCanceled, "admitted native storage receives owner-controlled settlement, not the page token");
            admitted.TrySetResult();
            await release.Task;

        };
        await using var service = new FilmSourceService(runtime, store);
        var save = service.ImportNetworkAsync("已提交", "https://fixture.invalid/cat.js", "cat", cancel.Token);
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            Check(!save.IsCompleted, "page cancellation is not proof that native storage settled");
            release.TrySetResult();
            var saved = await save.WaitAsync(TimeSpan.FromSeconds(5));
            Check(saved.Name == "已提交" && Input(store).GetProperty("name").GetString() == "已提交",
                "a successful real native receipt remains a successful save after page cancellation");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task Notifications()
    {
        foreach (var state in new[] { RuntimeConnectionState.Starting, RuntimeConnectionState.Ready })
        foreach (bool fail in new[] { false, true })
        {
            var store = new Store();
            var runtime = new Runtime(store) { State = state };
            var notified = Signal(); var release = Signal(); var observed = Signal();
            runtime.OnNotify = async token =>
            {
                notified.TrySetResult();
                await release.Task.WaitAsync(token);
                return fail ? Element(new { ok = false, code = "CLOSED" }) : Element(new { ok = true });
            };
            await using var service = new FilmSourceService(runtime, store);
            service.Changed += (_, _) =>
            {
                if (service.RuntimeError is not null || service.Cats.SingleOrDefault()?.CanDetect == true)
                    observed.TrySetResult();
            };
            var saved = await service.ImportNetworkAsync("不等加载", "https://fixture.invalid/cat.js", "cat")
                .WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(saved.InputRevision == 1 && service.Cats.Single().Name == "不等加载" &&
                    !observed.Task.IsCompleted, "saving returns while the real notification task remains outstanding");
                var notification = runtime.Calls.Single(call => call.Method == "runtime.settings.changed");
                Fields(notification.Input, "block");
                Check(notification.Input.GetProperty("block").GetString() == "cat.sources",
                    "notification contains only block identity, not URL name path hash or credentials");
                release.TrySetResult();
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(service.Cats.Single().SourceUrl == "https://fixture.invalid/cat.js" &&
                    (fail ? service.RuntimeError is not null : service.Cats.Single().CanDetect),
                    "notification failure never rolls back the native source intent");
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task LostNativeReceipt()
    {
        var store = new Store();
        var runtime = new Runtime(store);
        await using (var service = new FilmSourceService(runtime, store))
        {
            store.AfterWrite=()=>throw new DocumentStoreException(DocumentStoreError.Storage);
            try
            {
                await service.ImportNetworkAsync("实际提交", "https://fixture.invalid/committed.js", "cat");
                throw new InvalidOperationException("Expected lost native receipt");
            }
            catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Storage, "lost receipt stays a native storage error"); }
            Check(service.Cats.Count == 0 && runtime.Captures == 0 && runtime.Calls.Count == 0,
                "an uncertain native receipt does not fabricate success or replay the operation through Node");
        }
        store.AfterWrite = null;
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        await Code(() => reopened.ImportNetworkAsync("重复", "https://fixture.invalid/replayed.js", "cat"), "STORAGE_CONFLICT");
        Check(reopened.Cats.Single() is { Name: "实际提交", SourceUrl: "https://fixture.invalid/committed.js", InputRevision: 1 },
            "a new native owner reads the actual durable commit and rejects replay as duplicate rather than replacing it");
    }

    private static async Task StaleStatus()
    {
        var store = new Store();
        var runtime = new Runtime(store);
        using var local = new SourceFile("module.exports = {};");
        await using var service = new FilmSourceService(runtime, store);
        await service.ImportLocalAsync("旧本地", local.Path, "cat");
        await service.RefreshAsync();
        var stale = runtime.Status();
        await service.EnableCatAsync("cat", false);
        await service.EditCatAsync("cat", "新网络", "network", "https://fixture.invalid/new.js", null);
        runtime.OnStatus = () => stale;
        await service.RefreshAsync();
        Check(service.Cats.Single() is { Name: "新网络", SourceType: "network", InputRevision: 2, Enabled: false,
                CanDetect: false, Generation: 0, LocalPath: null } && service.Videos.Count == 1,
            "stale revision/type/enable status cannot roll back native intent or delete cached children");
        await service.ImportNetworkAsync("新身份", "https://fixture.invalid/newer.js", "new");
        await service.RefreshAsync();
        Check(service.Cats.Count == 2 && service.Cats.Single(cat => cat.InstallationId == "new").SourceUrl ==
            "https://fixture.invalid/newer.js", "a missing runtime instance does not remove newly committed intent");
    }

    private static async Task Shutdown()
    {
        var store = new Store();
        var runtime = new Runtime(store) { State = RuntimeConnectionState.Ready };
        var notified = Signal(); var cancelled = Signal(); var release = Signal();
        runtime.OnNotify = async token =>
        {
            notified.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await release.Task;
                throw;
            }
            throw new InvalidOperationException("Expected notification cancellation");
        };
        await using var service = new FilmSourceService(runtime, store);
        await service.ImportNetworkAsync("保留", "https://fixture.invalid/cat.js", "cat");
        try
        {
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = service.PrepareShutdownAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted && Input(store).GetProperty("name").GetString() == "保留",
                "shutdown cannot finish until the executing notification cleanup settles");
            release.TrySetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!runtime.Disposed && !store.Disposed, "service owns its work, not the borrowed runtime/store");
            service.Resume();
            runtime.State = RuntimeConnectionState.Closed;
            await service.EditCatAsync("cat", "恢复后保存");
            Check(service.Cats.Single().Name == "恢复后保存" && runtime.Captures == 1,
                "resumed offline saves do not replay the cancelled notification");
        }
        finally { release.TrySetResult(); }
    }

    private static TestCase Case(string name, Func<Task> run) => new("film-cat-settings: " + name, run);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Input(Store store) => store.Cat.GetProperty("scripts")[0];
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Fields(JsonElement value, params string[] names) =>
        Check(value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(names.Order(StringComparer.Ordinal)), "unexpected native configuration/notification fields");
    private static async Task Code(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (FilmSourceException error) { Check(error.Code == expected, "Expected " + expected + ", received " + error.Code); return; }
        throw new InvalidOperationException("Expected " + expected);
    }

    private sealed class SourceFile : IDisposable
    {
        internal string Path { get; } = TestWorkspace.DataPath("cat-settings-files", Guid.NewGuid().ToString("N") + ".js");
        internal SourceFile(string source)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, source, new UTF8Encoding(false));
        }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class Store : NativeTestDatabase
    {
        internal DocumentStoreError? WriteError;
        internal Func<CancellationToken,Task>? Writing;
        internal bool Disposed;
        internal byte[] CatBytes
        {
            get=>JsonSerializer.SerializeToUtf8Bytes(Project(FilmStorageRecords.Cat,"device"));
            set=>Seed(FilmStorageRecords.Cat,JsonNode.Parse(value)!.AsObject(),"device");
        }
        internal bool HasSourceSnapshot=>Project(FilmStorageRecords.Sources,"device") is not null;
        internal JsonElement Cat=>JsonSerializer.Deserialize<JsonElement>(CatBytes);
        public override Task<T> WriteAsync<T>(Func<DatabaseSession,T> write,CancellationToken token=default,
            int retainedBytes=0,Action<Action>? commit=null)=>base.WriteAsync(db=>{
                Writing?.Invoke(CancellationToken.None).GetAwaiter().GetResult();
                if(WriteError is { } error)throw new DocumentStoreException(error);return write(db);
            },token,retainedBytes,commit);
        public override async ValueTask DisposeAsync(){Disposed=true;await base.DisposeAsync();}
    }

    private sealed record Call(string Method, JsonElement Input);
    private sealed class Runtime(Store store) : IRuntimeSession, IRuntimeConnectionSource, IRuntimeRecovery
    {
        internal readonly List<Call> Calls = [];
        internal Func<CancellationToken, Task<JsonElement>>? OnNotify;
        internal Func<JsonElement>? OnStatus;
        internal int Captures, Retries;
        internal bool Disposed;
        public RuntimeConnectionState State { get; set; } = RuntimeConnectionState.Dormant;
        public bool IsConnected => State == RuntimeConnectionState.Ready;
        public long Generation => 1;
        public bool CanRetry => State == RuntimeConnectionState.Failed;
        public void RegisterRecoveryScope(IRuntimeRecoveryScope scope) { }
        public Task RetryAsync(CancellationToken token = default)
        { Retries++; throw new InvalidOperationException("Native saves must not recover a runtime."); }
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken token = default)
        {
            Captures++;
            token.ThrowIfCancellationRequested();
            Check(State is RuntimeConnectionState.Ready or RuntimeConnectionState.Starting, "only active owners may capture a connection");
            return Task.FromResult(new RuntimeConnection(this, new Uri("http://127.0.0.1:12345"), CancellationToken.None));
        }
        internal JsonElement Status() => Element(new
        {
            ok = true, contract = "cat.script.v2",
            value = new { instances = store.Cat.GetProperty("scripts").EnumerateArray().Select(input => new
            {
                installationId = input.GetProperty("installationId").GetString(), name = input.GetProperty("name").GetString(),
                sourceType = input.GetProperty("sourceType").GetString(), sourceUrl = input.GetProperty("sourceUrl").GetString(),
                enabled = input.GetProperty("enabled").GetBoolean(), inputRevision = input.GetProperty("inputRevision").GetInt64(),
                importedAt = 1, updatedAt = (long?)null, updateState = "idle", updateError = (string?)null,
                generation = 1, state = input.GetProperty("enabled").GetBoolean() ? "ready" : "disabled",
                sourceHash = new string('a', 64), error = (string?)null,
                sites = new[] { new { key = "video", name = "视频", api = "/spider/fixture", actions = new[] { "search" },
                    methods = new { search = "POST" }, discovery = "declared" } }
            }).ToArray() }
        });
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new(method, Element(parameters ?? new { })));
            return method switch
            {
                "runtime.settings.changed" => OnNotify?.Invoke(cancellationToken) ?? Task.FromResult(Element(new { ok = true })),
                "cat.script.status" => Task.FromResult(OnStatus?.Invoke() ?? Status()),
                _ => throw new InvalidOperationException("Native source settings must not call " + method)
            };
        }
        public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout,
            CancellationToken cancellationToken = default) => CallAsync(method, parameters, cancellationToken);
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Native saves must not upload source text.");
        public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No image calls in native settings.");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
