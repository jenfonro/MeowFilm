using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.AppHost;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>
/// Synthetic legacy files only. FilmNativeSettingsFixture is an explicit document/CAS
/// double; only ProtectedRoundTrip uses the real Windows LocalDocumentStore and DPAPI.
/// No runtime, upstream service or real credentials are used.
/// </summary>
public static class AppSettingsMigrationTests
{
    private static readonly IReadOnlyDictionary<string, string> Namespaces = AppIdentity.RuntimeSettingsNamespaces;
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        ["cat.sources"] = "settings.json", ["pan.accounts"] = "pan/accounts.json",
        ["tmdb"] = "tmdb/settings.json", ["douban"] = "douban/settings.json",
        ["film.rules"] = "film-rules/rules.json", ["film.preferences"] = "film-playback/library.json"
    };

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("first adoption splits intent facts and business without losing data", FirstAdoption);
        yield return Case("absent legacy never synthesizes settings or touches unrelated files", AbsentLegacy);
        foreach (string block in Paths.Keys)
        {
            yield return Case("valid native authority survives without replaying legacy " + block,
                () => ExistingAuthority(block));
        }
        yield return Case("cat version two facts reject missing native intent without writing data", MissingCatIntent);
        yield return Case("native intent edited after interrupted cat commit survives facts recovery", InterruptedCatEdit);
        foreach (string variant in new[] { "pan-version", "pan-revision", "tmdb-type", "cat-duplicate", "preferences-type" })
            yield return Case("invalid consumer schema is preserved " + variant, () => InvalidSchema(variant));
        foreach (var error in new[] { DocumentStoreError.Storage, DocumentStoreError.Protection })
            yield return Case("failed native commit preserves sensitive input " + error, () => FailedWrite(error));
        yield return Case("native CAS conflict preserves concurrent authority and legacy input", Conflict);
        yield return Case("corrupt native authority is not overwritten or used to retire legacy", CorruptNative);
        foreach (bool lost in new[] { false, true })
            yield return Case(lost ? "lost commit receipt recovers on restart" : "incorrect commit receipt prevents retirement",
                () => UncertainReceipt(lost));
        yield return Case("cancellation before admission preserves all input", CancelBeforeAdmission);
        yield return Case("cancellation after admission settles commit before restart cleanup", CancelAfterAdmission);
        yield return Case("legacy changed during native commit is never retired", LegacyChanged);
        foreach (string block in new[] { "cat.sources", "pan.accounts" })
            yield return Case("cleanup sharing failure retains both copies for restart " + block,
                () => CleanupFailure(block));
        yield return Case("partial multi-block failure resumes without overwriting committed blocks", PartialFailure);
        yield return Case("real LocalDocumentStore protects sensitive adoption and reopens", ProtectedRoundTrip);
    }

    private static async Task FirstAdoption()
    {
        using var files = new Files();
        await using var store = NewStore();
        var inputs = Paths.Keys.ToDictionary(block => block, Legacy);
        foreach (var (block, input) in inputs) files.Write(block, input);
        string source = files.Extra("sources/synthetic.js", "module.exports = {};");
        string volume = files.Extra("volumes/local/synthetic.txt", "synthetic-private-volume");
        var unrelated = Element(new { synthetic = "unrelated-native-document" });
        store.Seed("app.appearance.v1", unrelated);

        await Apply(store, files);

        Check(store.Writes == Paths.Count, "each setting has exactly one native initial commit");
        foreach (string block in Paths.Keys)
            Equal(Adopted(block, inputs[block]), store.Value(Namespaces[block]), "adopted native content differs");
        Facts(inputs["cat.sources"], files.Read("cat.sources"));
        Business(inputs["film.preferences"], files.Read("film.preferences"));
        foreach (string block in new[] { "pan.accounts", "tmdb", "douban", "film.rules" })
            Check(!File.Exists(files.PathFor(block)), "committed legacy settings must be retired");
        Check(File.ReadAllText(source) == "module.exports = {};" &&
            File.ReadAllText(volume) == "synthetic-private-volume", "migration must preserve source and private volume");
        Equal(unrelated, store.Value("app.appearance.v1"), "unrelated native settings changed");

        byte[] facts = File.ReadAllBytes(files.PathFor("cat.sources"));
        byte[] business = File.ReadAllBytes(files.PathFor("film.preferences"));
        await Apply(store, files);
        Check(store.Writes == Paths.Count &&
            File.ReadAllBytes(files.PathFor("cat.sources")).SequenceEqual(facts) &&
            File.ReadAllBytes(files.PathFor("film.preferences")).SequenceEqual(business),
            "repeat startup must not rewrite facts, business or adopted settings");
    }

    private static async Task AbsentLegacy()
    {
        using var files = new Files();
        await using var store = NewStore();
        string unrelated = files.Extra("unknown/synthetic.json", "{\"synthetic\":true}");
        await Apply(store, files);
        Check(store.Writes == 0 && File.ReadAllText(unrelated) == "{\"synthetic\":true}",
            "missing input is not an empty migration or permission to scan/delete other files");
        foreach (string space in Namespaces.Values) await Missing(store, space);
    }

    private static async Task ExistingAuthority(string block)
    {
        using var files = new Files();
        await using var store = NewStore();
        var legacy = Legacy(block);
        var authority = Adopted(block, legacy);
        if (block == "cat.sources")
        {
            authority["scripts"]![0]!["name"] = "synthetic-new-native-name";
            authority["scripts"]![0]!["enabled"] = true;
            authority["scripts"]![0]!["inputRevision"] = 7;
        }
        else if (block == "pan.accounts") authority["accounts"]!["quark"]!["credentials"]!["cookie"] = "synthetic-new-native-cookie";
        else if (block == "tmdb") authority["settings"]!["apiKey"] = "synthetic-new-native-key";
        else if (block == "douban") authority["settings"]!["cookie"] = "synthetic-new-native-cookie";
        else if (block == "film.rules") authority["rules"]!["episodeRules"]![0]!["pattern"] = "synthetic-new-native-pattern";
        else authority["preferences"]!["keywords"] = new JsonArray("synthetic-new-native-keyword");
        store.Seed(Namespaces[block], Element(authority));
        files.Write(block, legacy);

        await Apply(store, files, block);
        Equal(authority, store.Value(Namespaces[block]), "valid native authority must never be replaced");
        Check(store.Writes == 0, "existing authority must not be rewritten with stale legacy values");
        if (block == "cat.sources") Facts(legacy, files.Read(block));
        else Equal(legacy, files.Read(block), "different native authority must preserve the non-cat legacy input");
        await Apply(store, files, block);
        Equal(authority, store.Value(Namespaces[block]), "repeat startup must preserve newer native authority");
        Check(store.Writes == 0, "repeat startup must not replay legacy edits");
    }

    private static async Task MissingCatIntent()
    {
        using var files = new Files();
        await using var store = NewStore();
        var facts = RuntimeFacts(Legacy("cat.sources"));
        files.Write("cat.sources", facts);
        byte[] before = File.ReadAllBytes(files.PathFor("cat.sources"));
        await StoreError(() => Apply(store, files, "cat.sources"), DocumentStoreError.Corrupt);
        Check(store.Writes == 0 && File.ReadAllBytes(files.PathFor("cat.sources")).SequenceEqual(before),
            "missing native intent must fail without pruning facts or writing empty configuration");
        await Missing(store, Namespaces["cat.sources"]);
    }

    private static async Task InterruptedCatEdit()
    {
        using var files = new Files();
        await using var store = NewStore();
        var legacy = Legacy("cat.sources");
        files.Write("cat.sources", legacy);
        using var cancellation = new CancellationTokenSource();
        store.Writing = (_, token) =>
        {
            Check(!token.CanBeCanceled, "an admitted migration write must settle independently");
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        await Cancelled(() => Apply(store, files, "cat.sources", cancellation.Token));
        Equal(legacy, files.Read("cat.sources"), "interrupted cleanup must retain legacy facts");
        Equal(Adopted("cat.sources", legacy), store.Value(Namespaces["cat.sources"]), "admitted commit must survive cancellation");
        var newer = Adopted("cat.sources", legacy);
        newer["scripts"]![0]!["name"] = "synthetic-edited-after-interruption";
        newer["scripts"]![0]!["inputRevision"] = 2;
        store.Seed(Namespaces["cat.sources"], Element(newer));
        store.Writing = null;

        await Apply(store, files, "cat.sources");
        Equal(newer, store.Value(Namespaces["cat.sources"]), "recovery cannot restore yesterday's native intent");
        Facts(legacy, files.Read("cat.sources"));
        await Apply(store, files, "cat.sources");
        Check(store.Writes == 1, "recovery must finish facts without reimporting old intent");
    }

    private static async Task InvalidSchema(string variant)
    {
        string block = variant.StartsWith("pan-", StringComparison.Ordinal) ? "pan.accounts" :
            variant == "tmdb-type" ? "tmdb" : variant == "cat-duplicate" ? "cat.sources" : "film.preferences";
        using var files = new Files();
        await using var store = NewStore();
        var input = Legacy(block);
        switch (variant)
        {
            case "pan-version": input["version"] = 2; break;
            case "pan-revision": input["accounts"]!["quark"]!["revision"] = 0; break;
            case "tmdb-type": input["settings"]!["includeAdult"] = "synthetic-not-a-boolean"; break;
            case "cat-duplicate": input["scripts"]!.AsArray().Add(input["scripts"]![0]!.DeepClone()); break;
            default: input["preferences"] = "synthetic-not-an-object"; break;
        }
        files.Write(block, input);
        byte[] before = File.ReadAllBytes(files.PathFor(block));
        await StoreError(() => Apply(store, files, block), DocumentStoreError.Corrupt);
        Check(store.Writes == 0 && File.ReadAllBytes(files.PathFor(block)).SequenceEqual(before),
            "consumer-invalid settings cannot be committed and their recovery input cannot be deleted");
        await Missing(store, Namespaces[block]);
    }

    private static async Task FailedWrite(DocumentStoreError error)
    {
        using var files = new Files();
        await using var store = NewStore();
        files.Write("pan.accounts", Legacy("pan.accounts"));
        byte[] before = File.ReadAllBytes(files.PathFor("pan.accounts"));
        store.WriteError = error;
        await StoreError(() => Apply(store, files, "pan.accounts"), error);
        Check(File.ReadAllBytes(files.PathFor("pan.accounts")).SequenceEqual(before), "failed native commit cannot retire sensitive input");
        await Missing(store, Namespaces["pan.accounts"]);
        store.WriteError = null;
        await Apply(store, files, "pan.accounts");
        Check(!File.Exists(files.PathFor("pan.accounts")) && store.Writes == 2, "restart retries a failed commit exactly once");
    }

    private static async Task Conflict()
    {
        using var files = new Files();
        await using var store = NewStore();
        var input = Legacy("pan.accounts");
        files.Write("pan.accounts", input);
        var concurrent = Legacy("pan.accounts");
        concurrent["accounts"]!["quark"]!["revision"] = 9;
        concurrent["accounts"]!["quark"]!["credentials"]!["cookie"] = "synthetic-concurrent-cookie";
        store.Writing = (space, _) => { store.Seed(space, Element(concurrent)); return Task.CompletedTask; };
        await StoreError(() => Apply(store, files, "pan.accounts"), DocumentStoreError.Conflict);
        Equal(concurrent, store.Value(Namespaces["pan.accounts"]), "a CAS conflict cannot overwrite the winning account");
        Equal(input, files.Read("pan.accounts"), "CAS failure must retain its unconfirmed input");
        Check(store.Writes == 1, "a conflict must not blindly retry migration");
    }

    private static async Task CorruptNative()
    {
        using var files = new Files();
        await using var store = NewStore();
        files.Write("pan.accounts", Legacy("pan.accounts"));
        store.SeedBytes(Namespaces["pan.accounts"], "{"u8.ToArray());
        await StoreError(() => Apply(store, files, "pan.accounts"), DocumentStoreError.Corrupt);
        Check(store.Writes == 0 && File.Exists(files.PathFor("pan.accounts")), "corrupt native authority is never silently repaired by import");
        var snapshot = await store.ReadAsync(Namespaces["pan.accounts"]);
        Check(snapshot.Data is not null && snapshot.Data.AsSpan().SequenceEqual("{"u8), "corrupt native bytes must remain available for explicit recovery");
    }

    private static async Task UncertainReceipt(bool lost)
    {
        using var files = new Files();
        await using var store = NewStore();
        var input = Legacy("pan.accounts");
        files.Write("pan.accounts", input);
        var intercepted = new ReceiptDouble(store, async (space, data, expected, token) =>
        {
            await store.WriteAsync(space, data, expected, token);
            if (lost) throw new DocumentStoreException(DocumentStoreError.Storage);
            return DocumentFingerprint.Missing;
        });
        await StoreError(() => Apply(intercepted, files, "pan.accounts"), DocumentStoreError.Storage);
        Equal(input, files.Read("pan.accounts"), "unconfirmed receipts cannot authorize sensitive file retirement");
        Equal(input, store.Value(Namespaces["pan.accounts"]), "a lost receipt must not fabricate rollback of actual data");
        await Apply(store, files, "pan.accounts");
        Check(!File.Exists(files.PathFor("pan.accounts")) && store.Writes == 1,
            "restart confirms existing durable data and cleans without duplicate writes");
    }

    private static async Task CancelBeforeAdmission()
    {
        using var files = new Files();
        await using var store = NewStore();
        files.Write("pan.accounts", Legacy("pan.accounts"));
        byte[] before = File.ReadAllBytes(files.PathFor("pan.accounts"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Cancelled(() => Apply(store, files, "pan.accounts", cancellation.Token));
        Check(store.Writes == 0 && File.ReadAllBytes(files.PathFor("pan.accounts")).SequenceEqual(before),
            "pre-admission cancellation must not change either owner");
        await Missing(store, Namespaces["pan.accounts"]);
    }

    private static async Task CancelAfterAdmission()
    {
        using var files = new Files();
        await using var store = NewStore();
        var input = Legacy("pan.accounts");
        files.Write("pan.accounts", input);
        using var cancellation = new CancellationTokenSource();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Writing = async (_, token) =>
        {
            Check(!token.CanBeCanceled, "admitted migration commit must own its settlement");
            admitted.TrySetResult();
            await release.Task;
        };
        var migration = Apply(store, files, "pan.accounts", cancellation.Token);
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Check(!migration.IsCompleted && File.Exists(files.PathFor("pan.accounts")),
                "caller cancellation is not a completed durable receipt");
            release.TrySetResult();
            await Cancelled(() => migration);
        }
        finally { release.TrySetResult(); }
        Equal(input, store.Value(Namespaces["pan.accounts"]), "admitted write must settle even when cleanup is cancelled");
        Equal(input, files.Read("pan.accounts"), "cancelled cleanup must preserve sensitive recovery input");
        store.Writing = null;
        await Apply(store, files, "pan.accounts");
        Check(store.Writes == 1 && !File.Exists(files.PathFor("pan.accounts")), "restart must finish cleanup without replaying the commit");
    }

    private static async Task LegacyChanged()
    {
        using var files = new Files();
        await using var store = NewStore();
        var input = Legacy("pan.accounts");
        var changed = Legacy("pan.accounts");
        changed["accounts"]!["quark"]!["credentials"]!["cookie"] = "synthetic-later-legacy-cookie";
        files.Write("pan.accounts", input);
        store.Writing = (_, _) => { files.Write("pan.accounts", changed); return Task.CompletedTask; };
        await Apply(store, files, "pan.accounts");
        Equal(input, store.Value(Namespaces["pan.accounts"]), "native commit must correspond to its captured input");
        Equal(changed, files.Read("pan.accounts"), "changed legacy bytes must never be deleted as the captured migration input");
        Check(store.Writes == 1, "changed legacy must not trigger an unbounded or blind import retry");
    }

    private static async Task CleanupFailure(string block)
    {
        using var files = new Files();
        await using var store = NewStore();
        var input = Legacy(block);
        files.Write(block, input);
        // Windows sharing permits the verified read but deterministically denies Delete/Move.
        using (var pinned = new FileStream(files.PathFor(block), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await Apply(store, files, block); throw new InvalidOperationException("Expected legacy retirement sharing failure"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Equal(Adopted(block, input), store.Value(Namespaces[block]), "cleanup failure cannot roll back a durable native commit");
            Equal(input, files.Read(block), "failed cleanup must retain the complete legacy document");
            Check(!Directory.EnumerateFiles(files.Root, "*.tmp", SearchOption.AllDirectories).Any(),
                "failed facts replacement must remove only its own staging file");
        }
        await Apply(store, files, block);
        if (block == "cat.sources") Facts(input, files.Read(block));
        else Check(!File.Exists(files.PathFor(block)), "restart must finish sensitive cleanup once sharing permits it");
        Check(store.Writes == 1, "cleanup retry must not repeat the native commit");
    }

    private static async Task PartialFailure()
    {
        using var files = new Files();
        await using var store = NewStore();
        foreach (string block in Paths.Keys) files.Write(block, Legacy(block));
        var ordered = Namespaces.Keys.ToArray();
        string failing = ordered[1];
        store.Writing = (space, _) => space == Namespaces[failing]
            ? Task.FromException(new DocumentStoreException(DocumentStoreError.Storage)) : Task.CompletedTask;
        await StoreError(() => Apply(store, files), DocumentStoreError.Storage);
        Equal(Adopted(ordered[0], Legacy(ordered[0])), store.Value(Namespaces[ordered[0]]),
            "earlier confirmed blocks must survive a later migration failure");
        foreach (string block in ordered.Skip(1))
        {
            await Missing(store, Namespaces[block]);
            Equal(Legacy(block), files.Read(block), "unprocessed blocks must retain all their input");
        }
        store.Writing = null;
        await Apply(store, files);
        Check(store.Writes == Paths.Count + 1, "recovery must skip the confirmed first commit and retry only remaining blocks");
        foreach (string block in Paths.Keys)
            Equal(Adopted(block, Legacy(block)), store.Value(Namespaces[block]), "partial recovery lost a setting");
        Facts(Legacy("cat.sources"), files.Read("cat.sources"));
        Business(Legacy("film.preferences"), files.Read("film.preferences"));
    }

    private static async Task ProtectedRoundTrip()
    {
        using var files = new Files();
        string root = Path.Combine(files.Root, "native");
        Directory.CreateDirectory(root);
        await using (var store = new LocalDocumentStore(root, AppIdentity.NativeNamespaces,
            protectedNamespaces: AppIdentity.ProtectedNamespaces))
        {
            foreach (string block in Paths.Keys) files.Write(block, Legacy(block));
            await Apply(store, files);
            foreach (string block in Paths.Keys)
            {
                var snapshot = await store.ReadAsync(Namespaces[block]);
                try
                {
                    Check(snapshot.Data is not null, "real native migration did not persist a block");
                    Equal(Adopted(block, Legacy(block)), JsonSerializer.Deserialize<JsonElement>(snapshot.Data!),
                        "real native committed content differs from the adopted block");
                }
                finally { if (snapshot.Data is not null) CryptographicOperations.ZeroMemory(snapshot.Data); }
            }
        }
        foreach (string block in new[] { "cat.sources", "pan.accounts", "tmdb", "douban" })
        {
            string space = Namespaces[block];
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(space)));
            string directory = Path.Combine(root, "n-" + hash);
            Check(Directory.EnumerateFiles(directory).Select(Path.GetFileName).SequenceEqual(["document.mfdp"]),
                "sensitive migration must persist a single protected envelope without plaintext stages");
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, "document.mfdp"));
            Check(bytes.AsSpan().StartsWith("MEOWDPAP"u8) &&
                bytes.AsSpan().IndexOf(JsonSerializer.SerializeToUtf8Bytes(Adopted(block, Legacy(block)))) < 0,
                "sensitive migration must use a DPAPI envelope rather than plaintext JSON");
        }
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(path);
            foreach (string marker in new[] { "synthetic-cookie-migration-only", "synthetic-api-key-migration-only",
                "synthetic-access-token-migration-only", "synthetic-douban-cookie-migration-only" })
                Check(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) < 0,
                    "synthetic sensitive fields must not occur in any native disk file");
        }
        await using var reopened = new LocalDocumentStore(root, AppIdentity.NativeNamespaces,
            protectedNamespaces: AppIdentity.ProtectedNamespaces);
        await Apply(reopened, files);
        foreach (string block in Paths.Keys)
        {
            var actual = await new SettingsDocument(reopened, Namespaces[block]).ReadAsync();
            Check(actual is not null, "reopened real native owner is missing adopted data");
            Equal(Adopted(block, Legacy(block)), actual!.Value, "protected native data must decrypt and survive restart");
        }
    }

    private static JsonObject Legacy(string block) => block switch
    {
        "cat.sources" => JsonNode.Parse("""
            {"version":1,"port":12345,"providers":{"quark":false},"mappings":[{"provider":"quark","aliases":["synthetic-alias"]}],
             "scripts":[
              {"installationId":"local","name":"synthetic-local","sourceType":"local","sourceUrl":null,"enabled":false,
               "sourceHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","version":"1.2.3",
               "config":{"synthetic":true},"importedAt":10,"updatedAt":20,"updateState":"failed","updateError":"SYNTHETIC_FAILURE",
               "sites":[{"key":"synthetic-site","name":"synthetic-video","api":"/synthetic","actions":["search"],
                         "methods":{"search":"POST"},"discovery":"declared"}]},
              {"installationId":"network","name":"synthetic-network","sourceType":"network",
               "sourceUrl":"https://synthetic.invalid/cat.js","enabled":true,
               "sourceHash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","version":"2.0.0",
               "config":{},"importedAt":30,"updatedAt":null,"updateState":"idle","updateError":null,"sites":[]}]}
            """)!.AsObject(),
        "pan.accounts" => JsonNode.Parse("""
            {"version":1,"accounts":{"quark":{"revision":3,"credentials":{"cookie":"synthetic-cookie-migration-only"}}}}
            """)!.AsObject(),
        "tmdb" => JsonNode.Parse("""
            {"version":1,"settings":{"apiBase":"https://synthetic.invalid/3","imageBase":"https://synthetic.invalid/images",
             "apiKey":"synthetic-api-key-migration-only","accessToken":"synthetic-access-token-migration-only",
             "language":"zh-CN","region":"CN","includeAdult":false}}
            """)!.AsObject(),
        "douban" => JsonNode.Parse("""
            {"version":1,"settings":{"apiBase":"https://synthetic.invalid/api","searchBase":"https://synthetic.invalid/search",
             "imageBase":"https://synthetic.invalid/images","apiProxy":"","imageProxy":"","cookie":"synthetic-douban-cookie-migration-only"}}
            """)!.AsObject(),
        // The former rules-service writer emitted {rules}, without a version.
        "film.rules" => JsonNode.Parse("""
            {"rules":{"episodeCleanRegexRules":[],"episodeRules":[{"pattern":"synthetic-pattern","replace":"","flags":""}],
             "movieRules":[],"aggregateRegexRules":[]}}
            """)!.AsObject(),
        "film.preferences" => JsonNode.Parse("""
            {"version":1,"preferences":{"ruleOrder":["quality","pan","keyword"],
             "panOrder":["yidong","tianyi","quark","uc","baidu"],
             "keywords":["synthetic-keyword"],"siteCleanKeywords":[],"iosAutoPrepare":false},
             "history":[{"targetId":"tmdb:tv:42","title":"synthetic-film","sourceKey":"cat:local:synthetic-site",
              "candidateId":"synthetic-candidate","detailId":"synthetic-detail","lineId":"synthetic-line",
              "position":12.5,"duration":600,"paused":false,"ended":false,"playedAt":1000,"updatedAt":1200}],
             "manual":[{"id":"synthetic-manual","targetId":"tmdb:tv:42","kind":"site","sourceKey":"cat:local:synthetic-site",
              "detailId":"synthetic-detail","enabled":true,"autoDisable":true,"failureCount":0}],
             "blocks":[{"targetId":"tmdb:tv:42","sourceKey":"cat:local:synthetic-site",
              "detailId":"synthetic-detail","lineId":"synthetic-line"}],"previewTargets":["tmdb:tv:42"]}
            """)!.AsObject(),
        _ => throw new InvalidOperationException("Unknown synthetic migration block")
    };

    private static JsonObject Adopted(string block, JsonObject input)
    {
        if (block == "film.preferences")
            return new() { ["version"] = 1, ["preferences"] = input["preferences"]!.DeepClone() };
        if (block == "film.rules")
            return new() { ["version"] = 1, ["rules"] = input["rules"]!.DeepClone() };
        if (block != "cat.sources") return (JsonObject)input.DeepClone();
        var scripts = new JsonArray();
        foreach (var script in input["scripts"]!.AsArray())
            scripts.Add(new JsonObject
            {
                ["installationId"] = script!["installationId"]!.DeepClone(), ["name"] = script["name"]!.DeepClone(),
                ["sourceType"] = script["sourceType"]!.DeepClone(), ["sourceUrl"] = script["sourceUrl"]?.DeepClone(),
                ["localPath"] = null, ["localHash"] = script["sourceType"]!.GetValue<string>() == "local"
                    ? script["sourceHash"]!.DeepClone() : null,
                ["enabled"] = script["enabled"]!.DeepClone(), ["inputRevision"] = 1
            });
        return new() { ["version"] = 1, ["port"] = input["port"]!.DeepClone(),
            ["providers"] = input["providers"]!.DeepClone(), ["mappings"] = input["mappings"]!.DeepClone(), ["scripts"] = scripts };
    }

    private static JsonObject RuntimeFacts(JsonObject input)
    {
        var scripts = new JsonArray();
        foreach (var script in input["scripts"]!.AsArray())
        {
            var facts = new JsonObject();
            foreach (string field in new[] { "installationId", "sourceHash", "version", "config", "importedAt",
                "updatedAt", "updateState", "updateError", "sites" })
                facts[field] = script![field]?.DeepClone();
            facts["loadedIdentity"] = script!["sourceType"]!.GetValue<string>() == "network" ? null :
                JsonValue.Create("local:1:" + script["sourceHash"]!.GetValue<string>());
            scripts.Add(facts);
        }
        return new() { ["version"] = 2, ["scripts"] = scripts };
    }

    private static void Facts(JsonObject input, JsonObject actual) =>
        Equal(RuntimeFacts(input), Element(actual), "cat runtime file must contain only actual facts and preserve installed metadata");
    private static void Business(JsonObject input, JsonObject actual)
    {
        var expected = (JsonObject)input.DeepClone();
        expected.Remove("preferences");
        Equal(expected, Element(actual), "splitting preferences must preserve history, manual, blocks and preview targets exactly");
    }
    private static FilmNativeSettingsFixture NewStore() => new([.. Namespaces.Values, "app.appearance.v1"]);
    private static Task Apply(IDocumentStore store, Files files, string? block = null, CancellationToken token = default) =>
        AppSettingsMigration.ApplyAsync(store, files.Root,
            block is null ? Namespaces : new Dictionary<string, string> { [block] = Namespaces[block] }, token);
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static void Equal(JsonElement expected, JsonElement actual, string message) =>
        Check(JsonElement.DeepEquals(expected, actual), message);
    private static void Equal(JsonObject expected, JsonElement actual, string message) =>
        Check(JsonElement.DeepEquals(Element(expected), actual), message);
    private static void Equal(JsonObject expected, JsonObject actual, string message) => Equal(expected, Element(actual), message);
    private static TestCase Case(string name, Func<Task> run) => new("app-settings-migration: " + name, run);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Missing(IDocumentStore store, string space)
    {
        var snapshot = await store.ReadAsync(space);
        Check(snapshot.Data is null && snapshot.Fingerprint == DocumentFingerprint.Missing, "unexpected native data after rejected migration");
    }
    private static async Task StoreError(Func<Task> action, DocumentStoreError expected)
    {
        try { await action(); }
        catch (DocumentStoreException error) { Check(error.Error == expected, "unexpected migration storage error category"); return; }
        throw new InvalidOperationException("Expected migration storage error: " + expected);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected migration cancellation");
    }

    /// <summary>Receipt fault decorator over the explicit fixture, not another persistence owner.</summary>
    private sealed class ReceiptDouble(IDocumentStore inner,
        Func<string, ReadOnlyMemory<byte>, DocumentFingerprint, CancellationToken, Task<DocumentFingerprint>> write) : IDocumentStore
    {
        public Task<DocumentSnapshot> ReadAsync(string space, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(space, cancellationToken);
        public Task<DocumentFingerprint> WriteAsync(string space, ReadOnlyMemory<byte> data, DocumentFingerprint expected,
            CancellationToken cancellationToken = default) => write(space, data, expected, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = TestWorkspace.DataPath("app-settings-migration", Guid.NewGuid().ToString("N"));
        internal Files() => Directory.CreateDirectory(Root);
        internal string PathFor(string block) => Path.Combine(Root, Paths[block].Replace('/', Path.DirectorySeparatorChar));
        internal void Write(string block, JsonObject value) => Extra(Paths[block], value.ToJsonString());
        internal JsonObject Read(string block) => JsonNode.Parse(File.ReadAllBytes(PathFor(block)))!.AsObject();
        internal string Extra(string relative, string text)
        {
            string path = TestWorkspace.ValidatePath(Root, Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        public void Dispose() => Directory.Delete(TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root), true);
    }
}
