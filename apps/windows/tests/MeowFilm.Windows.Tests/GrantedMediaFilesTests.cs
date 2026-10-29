using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class GrantedMediaFilesTests
{
    private const string Namespace = "music.files.v1";

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("current-format-roundtrip-restart-and-resolve", Roundtrip);
        yield return Case("every-non-current-document-is-rejected-without-write", NonCurrentDocuments);
        yield return Case("wrong-fingerprint-rejects-every-entry-point-without-write", WrongFingerprint);
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
        Check(store.Writes == 1 && store.LastReadBuffer is not null &&
            store.LastReadBuffer.All(value => value == 0), "read plaintext was not cleared");
    }

    private static async Task NonCurrentDocuments()
    {
        using var file = new Fixture();
        string id = Id(file.MediaPath);
        string path = JsonSerializer.Serialize(file.MediaPath);
        string title = JsonSerializer.Serialize(file.Title);
        string identifier = JsonSerializer.Serialize(id);
        string item = $$"""{"Id":{{identifier}},"Path":{{path}},"Title":{{title}}}""";
        string secondPath = Path.Combine(file.Root, "second.wav");
        File.WriteAllBytes(secondPath, [4, 5, 6]);
        string second = CurrentItem(secondPath);
        string forwardPath = JsonSerializer.Serialize(file.MediaPath.Replace('\\', '/'));
        string wrongTitle = JsonSerializer.Serialize(file.Title + "-old");
        string badId = JsonSerializer.Serialize("local:" + new string('0', 64));

        string[] variants =
        [
            "",
            "{",
            "null",
            "[]",
            """{"Schema":2,"Items":[]}""",
            """{"Schema":1.0,"Items":[]}""",
            """{"Schema":1e0,"Items":[]}""",
            """{"Schema":"1","Items":[]}""",
            """{"schema":1,"items":[]}""",
            """{"Schema":1}""",
            """{"Schema":1,"Items":[],"Legacy":true}""",
            """{"Schema":1,"Schema":1,"Items":[]}""",
            """{"Schema":1,"Items":null}""",
            $$"""{"Schema":1,"Items":[{"Id":{{identifier}},"Path":{{path}}}]}""",
            $$"""{"Schema":1,"Items":[{"Id":{{identifier}},"Path":{{path}},"Title":{{title}},"Legacy":true}]}""",
            $$"""{"Schema":1,"Items":[{"Id":{{identifier}},"Id":{{identifier}},"Path":{{path}},"Title":{{title}}}]}""",
            $$"""{"Schema":1,"Items":[{"Id":{{badId}},"Path":{{path}},"Title":{{title}}}]}""",
            $$"""{"Schema":1,"Items":[{"Id":{{identifier}},"Path":{{forwardPath}},"Title":{{title}}}]}""",
            $$"""{"Schema":1,"Items":[{"Id":{{identifier}},"Path":{{path}},"Title":{{wrongTitle}}}]}""",
            $$"""{"Schema":1,"Items":[{{item}},{{item}}]}""",
            $$"""{"Schema":1,"Items":[{{item}},{{second}}],"Unknown":0}"""
        ];

        foreach (string json in variants)
        {
            await using var store = new MemoryStore { Data = Encoding.UTF8.GetBytes(json) };
            byte[] original = store.Data.ToArray();
            var owner = Owner(store);
            await Corrupt(() => owner.ListAsync());
            await Corrupt(async () => _ = await owner.ResolveAsync(id));
            await Corrupt(() => owner.GrantAsync([file.MediaPath]));
            Check(store.Writes == 0 && store.Data.SequenceEqual(original),
                "invalid document was written or replaced");
            Check(store.LastReadBuffer is not null && store.LastReadBuffer.All(value => value == 0),
                "invalid read plaintext was not cleared");
            CryptographicOperations.ZeroMemory(original);
        }
    }

    private static async Task WrongFingerprint()
    {
        using var file = new Fixture();
        await using var store = new MemoryStore
        {
            Data = Encoding.UTF8.GetBytes(CurrentDocument(file.MediaPath)),
            WrongFingerprint = true
        };
        byte[] original = store.Data.ToArray();
        var owner = Owner(store);
        await Corrupt(() => owner.ListAsync());
        await Corrupt(async () => _ = await owner.ResolveAsync(Id(file.MediaPath)));
        await Corrupt(() => owner.GrantAsync([file.MediaPath]));
        Check(store.Writes == 0 && store.Data.SequenceEqual(original));
        Check(store.LastReadBuffer is not null && store.LastReadBuffer.All(value => value == 0));
        CryptographicOperations.ZeroMemory(original);
    }

    private static async Task CasConflict()
    {
        using var file = new Fixture();
        await using var store = new MemoryStore { Behavior = WriteBehavior.Conflict };
        var error = await Capture(() => Owner(store).GrantAsync([file.MediaPath]));
        Check(error is ProtectedDocumentException { Error: DocumentStoreError.Conflict });
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
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, "", [".wav"]));
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, "../files", [".wav"]));
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, Namespace, []));
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, Namespace, ["."]));
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, Namespace, [".w.av"]));
        Throws<ArgumentException>(() => new GrantedMediaFiles(store, Namespace, [".音乐"]));
        _ = new GrantedMediaFiles(store, Namespace, [".WAV", ".wav"]);
        return store.DisposeAsync().AsTask();
    }

    private static GrantedMediaFiles Owner(IProtectedDocumentStore store) =>
        new(store, Namespace, [".wav"]);

    private static string CurrentDocument(string path) =>
        $$"""{"Schema":1,"Items":[{{CurrentItem(path)}}]}""";

    private static string CurrentItem(string path) => JsonSerializer.Serialize(new
    {
        Id = Id(path),
        Path = path,
        Title = Path.GetFileNameWithoutExtension(path)
    });

    private static string Id(string path) => "local:" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    private static async Task Corrupt(Func<Task> action) => await Error("CORRUPT_DOCUMENT", action);

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

    private sealed class MemoryStore : IProtectedDocumentStore
    {
        internal byte[]? Data;
        internal byte[]? LastReadBuffer;
        internal int Reads;
        internal int Writes;
        internal bool WrongFingerprint;
        internal WriteBehavior Behavior;

        public Task<ProtectedDocument> ReadAsync(string documentNamespace,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(documentNamespace == Namespace);
            Reads++;
            LastReadBuffer = Data?.ToArray();
            var fingerprint = Data is null ? DocumentFingerprint.Missing :
                WrongFingerprint ? new DocumentFingerprint(true, Data.Length, new string('0', 64)) :
                DocumentFingerprint.FromBytes(Data);
            return Task.FromResult(new ProtectedDocument(LastReadBuffer, fingerprint));
        }

        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(documentNamespace == Namespace);
            Writes++;
            var current = Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data);
            if (current != expected || Behavior == WriteBehavior.Conflict)
                throw new ProtectedDocumentException(DocumentStoreError.Conflict);
            Data = data.ToArray();
            return Task.FromResult(Behavior == WriteBehavior.WrongReceipt
                ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data));
        }

        public ValueTask DisposeAsync()
        {
            if (Data is not null) CryptographicOperations.ZeroMemory(Data);
            if (LastReadBuffer is not null) CryptographicOperations.ZeroMemory(LastReadBuffer);
            return ValueTask.CompletedTask;
        }
    }
}
