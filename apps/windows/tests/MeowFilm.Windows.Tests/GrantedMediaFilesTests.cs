using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class GrantedMediaFilesTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("current-format-roundtrip-restart-and-resolve", Roundtrip);
        yield return Case("corrupt-current-rows-are-rejected-without-change", NonCurrentDocuments);
        yield return Case("CAS-conflict-is-not-retried-or-overwritten", CasConflict);
        yield return Case("wrong-write-receipt-is-explicitly-uncertain", WrongReceipt);
        yield return Case("constructor-rejects-invalid-owner-contracts", ConstructorContracts);
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("native-files.grants." + name, run);

    private static async Task Roundtrip()
    {
        using var file = new Fixture();
        await using var store = new MemoryStore();
        var owner = Owner(store);
        var granted = await owner.GrantAsync([file.MediaPath, file.MediaPath]);
        Check(granted.Count == 1 && store.Writes == 1);
        Check(granted[0] == new GrantedFile(Id(file.MediaPath), file.MediaPath, file.Title));

        using (var document = JsonDocument.Parse(store.Data!))
        {
            var root = document.RootElement;
            Check(root.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(["Schema", "Items"]));
            Check(root.GetProperty("Schema").GetRawText() == "1");
            var item = root.GetProperty("Items").EnumerateArray().Single();
            Check(item.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(["Id", "Path", "Title"]));
            Check(item.GetProperty("Id").GetString() == Id(file.MediaPath));
            Check(item.GetProperty("Path").GetString() == file.MediaPath);
            Check(item.GetProperty("Title").GetString() == file.Title);
        }

        var restarted = Owner(store);
        var listed = await restarted.ListAsync();
        Check(listed.SequenceEqual(granted) && store.Writes == 1);
        Check(await restarted.ResolveAsync(granted[0].Id) == new Uri(file.MediaPath));
        Check(store.Writes == 1);
    }

    private static async Task NonCurrentDocuments()
    {
        using var file = new Fixture();
        foreach (string sql in new[]
        {
            "UPDATE windows_file_grants SET Schema=2 WHERE scope_key='device'",
            "UPDATE windows_file_grants_Items SET Path=replace(Path,'\\','/') WHERE scope_key='device'",
            "UPDATE windows_file_grants_Items SET Title=Title||'-wrong' WHERE scope_key='device'"
        })
        {
            await using var store = new MemoryStore();
            var owner = Owner(store);
            _ = await owner.GrantAsync([file.MediaPath]);
            await store.Inner.WriteAsync(db => db.Execute(sql));
            byte[] original = store.Data!;
            int writes = store.Writes;
            await Corrupt(() => owner.ListAsync());
            await Corrupt(async () => _ = await owner.ResolveAsync(Id(file.MediaPath)));
            await Corrupt(() => owner.GrantAsync([file.MediaPath]));
            Check(store.Writes == writes + 1 && store.Data!.SequenceEqual(original),
                "Invalid current rows were overwritten or replayed.");
        }
    }

    private static async Task CasConflict()
    {
        using var file = new Fixture();
        await using var store = new MemoryStore { Behavior = WriteBehavior.Conflict };
        var error = await Capture(() => Owner(store).GrantAsync([file.MediaPath]));
        Check(error is DocumentStoreException { Error: DocumentStoreError.Conflict });
        Check(store.Writes == 1 && store.Data is null, "conflict was retried or overwritten");
    }

    private static async Task WrongReceipt()
    {
        using var file = new Fixture();
        await using var store = new MemoryStore { Behavior = WriteBehavior.WrongReceipt };
        await Error("COMMIT_UNKNOWN", () => Owner(store).GrantAsync([file.MediaPath]));
        Check(store.Writes == 1 && store.Data is not null, "uncertain commit fixture was not applied once");
        var listed = await Owner(store).ListAsync();
        Check(listed.Count == 1 && listed[0].Id == Id(file.MediaPath));
    }

    private static Task ConstructorContracts()
    {
        var store = new MemoryStore();
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, []));
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, ["."]));
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, [".w.av"]));
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, [".音乐"]));
        _ = new GrantedMediaFiles(store, [".WAV", ".wav"]);
        return store.DisposeAsync().AsTask();
    }

    private static GrantedMediaFiles Owner(IStorageDatabase store) =>
        new(store, [".wav"]);

    private static string Id(string path) => "local:" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    private static async Task Corrupt(Func<Task> action) => await Error("CORRUPT_STORAGE", action);

    private static async Task Error(string code, Func<Task> action)
    {
        var error = await Capture(action);
        Check(error is InvalidDataException invalid && invalid.Message == code,
            "expected " + code + ", got " + (error?.GetType().Name + ":" + error?.Message));
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message = "granted media files assertion")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; }
        internal string MediaPath { get; }
        internal string Title => Path.GetFileNameWithoutExtension(MediaPath);

        internal Fixture()
        {
            Root = TestWorkspace.DataPath("granted-media-files", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            MediaPath = Path.Combine(Root, "本地授权-🎵.wav");
            File.WriteAllBytes(MediaPath, [1, 2, 3]);
        }

        public void Dispose()
        {
            string root = TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private enum WriteBehavior { Normal, Conflict, WrongReceipt }

    private sealed class MemoryStore : NativeTestDatabase
    {
        internal byte[]? Data
        {
            get { var value=Project(FileGrantRecords.Files,"device"); return value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value); }
            set { if (value is not null) Seed(FileGrantRecords.Files,JsonNode.Parse(value)!.AsObject(),"device"); }
        }
        internal WriteBehavior Behavior;
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession,T> write, CancellationToken token = default,
            int retainedBytes = 0, Action<Action>? commit = null)
        {
            if (Behavior == WriteBehavior.Conflict) { Writes++; throw new DocumentStoreException(DocumentStoreError.Conflict); }
            var result=await base.WriteAsync(write,token,retainedBytes,commit);
            if (Behavior == WriteBehavior.WrongReceipt) throw new DocumentStoreException(DocumentStoreError.Storage);
            return result;
        }
    }
}
