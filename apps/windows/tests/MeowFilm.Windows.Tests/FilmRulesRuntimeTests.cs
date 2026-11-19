using System.IO;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;
using MeowFilm.Capabilities.Film.Rules;

namespace MeowFilm.Windows.Tests;

/// <summary>Production C# consumer through the actual Windows HTTP runtime and native addon.</summary>
public static class FilmRulesRuntimeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-rules-runtime: native rules save SHA revision notification and real Node cold restart preserve the unique document",
            () => MusicQualityConsumerTests.Sta(Persistence));
        yield return new("film-rules-runtime: native preference block is applied asynchronously and restored by a real cold runtime",
            () => MusicQualityConsumerTests.Sta(Preferences));
        yield return new("film-rules-runtime: real Node preview and normalization preserve Unicode captures flags and isolated UTF16",
            () => MusicQualityConsumerTests.Sta(Text));
    }
    private static void Check(bool value, string reason) => SearchFixture.Check(value, reason);
    private static string Data()
    {
        string root = TestWorkspace.DataPath("film-rules-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static async Task Persistence()
    {
        string root = Data(), savedRevision;
        var draft = new FilmRuleSet { AggregateRegexRules = ["\\[草稿\\]"] };
        await using (var host = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(root))
        {
            await using var rules = new FilmRuleService(host.Client, host.Store);
            await rules.InitializeAsync();
            string original = rules.Revision;
            Check(original.Length > 0 && rules.GetSavedSnapshot().EpisodeRules.Count > 0, "native initialization reads defaults and derives the SHA revision");
            var preview = await rules.ProcessAsync(new(FilmRuleSection.Search, "[草稿]银河"), draft);
            Check(preview.ProcessedText == "银河" && rules.Revision == original, "draft preview executes through Node without publishing a save");
            await rules.InitializeAsync();
            Check(rules.Revision == original && !rules.GetSavedSnapshot().AggregateRegexRules.SequenceEqual(draft.AggregateRegexRules),
                "a native read after preview still returns the saved defaults");
            await rules.SaveAsync(draft);
            var applied = await host.WaitSettingsAsync("film.rules");
            savedRevision = rules.Revision;
            var denied = await host.Client.CallAsync("film.rules.set",
                JsonSerializer.SerializeToElement(new { rules = new FilmRuleSet() }, FilmRuleService.Json));
            Check(!denied.GetProperty("ok").GetBoolean() && denied.GetProperty("code").GetString() == "PERMISSION_DENIED",
                "the real runtime refuses a second authoritative rules writer");
            Check(savedRevision != original && savedRevision == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(draft, FilmRuleService.Json))) &&
                applied.GetProperty("revision").GetString()!.Length > 0 && rules.GetSavedSnapshot().EpisodeRules.Count == 0 &&
                (await rules.ProcessAsync(new(FilmRuleSection.Search, "[草稿]银河"))).ProcessedText == "银河",
                "successful save commits exactly the whole draft and saved preview uses that owner snapshot");
        }
        await using var reopened = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(root);
        await using var restored = new FilmRuleService(reopened.Client, reopened.Store);
        await restored.InitializeAsync();
        Check(restored.Revision == savedRevision && restored.GetSavedSnapshot().EpisodeRules.Count == 0 &&
            restored.GetSavedSnapshot().AggregateRegexRules.SequenceEqual(draft.AggregateRegexRules),
            "a real process restart preserves the new rules document including deliberately empty groups");
        await restored.RestoreDefaultsAsync(FilmRuleSection.Search, draft);
        Check(restored.GetSavedSnapshot().AggregateRegexRules.Count > 0 && restored.GetSavedSnapshot().EpisodeRules.Count == 0,
            "native section restore keeps other empty draft groups");
        await reopened.WaitSettingsAsync("film.rules");
        await restored.ClearAsync();
        await reopened.WaitSettingsAsync("film.rules");
        Check(restored.GetSavedSnapshot().EpisodeRules.Count > 0, "native clear persists defaults for the real runtime to consume");
    }
    private static async Task Preferences()
    {
        string root = Data();
        var preferences = FilmPreferences.Defaults with { Keywords = ["原生优先词"],
            PanOrder = ["baidu", "quark", "yidong", "tianyi", "uc"] };
        await using (var host = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(root))
        {
            var settings = new FilmPlaybackSettings(host.Client, host.Store);
            await settings.SavePreferencesAsync(preferences);
            var applied = await host.WaitSettingsAsync("film.preferences");
            Check(JsonElement.DeepEquals(applied, FilmJson.Encode(preferences)), "read-only real runtime configuration proves the block notification was applied");
            var reply = await host.Client.CallAsync("film.settings.set", FilmJson.Encode(FilmPreferences.Defaults));
            Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == "PERMISSION_DENIED",
                "the real runtime cannot become a second preference writer");
        }
        await using var cold = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(root);
        Check(JsonElement.DeepEquals(await cold.WaitSettingsAsync("film.preferences"), FilmJson.Encode(preferences)),
            "a new real process restores preferences from the native document, without queued notification replay");
    }
    private static async Task Text()
    {
        await using var host = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(Data());
        await using var rules = new FilmRuleService(host.Client, host.Store);
        var draft = new FilmRuleSet
        {
            MovieRules = [new("(?<=甲)(\\p{Letter}+)(?=乙)", "<$1>", "gu")]
        };
        var result = await rules.ProcessAsync(new(FilmRuleSection.Movie, "甲丙丁乙"), draft);
        Check(result.RewrittenText == "甲<丙丁>乙" && result.Matched, "the production consumer reads real Unicode-property and lookaround capture output");
        draft = draft with { MovieRules = [new("^.", "$&")] };
        const string isolated = "\ud800A";
        result = await rules.ProcessAsync(new(FilmRuleSection.Movie, isolated), draft);
        Check(result.InputText == isolated && result.ProcessedText == isolated && result.RewrittenText == isolated,
            "UInt16 request and nested output arrays survive the actual HTTP/bridge/native boundary");
        var normalized = await rules.NormalizeNewMatchRuleAsync(" /a/gy ", " $1 ");
        Check(normalized == new FilmMatchRule("a", "$1", "gy"), "native input parsing preserves flags and trims replacement before actual Node execution");
        draft = draft with { MovieRules = [new("a", "X", "gy")] };
        foreach (int _ in Enumerable.Range(0, 2))
            Check((await rules.ProcessAsync(new(FilmRuleSection.Movie, "aa"), draft)).RewrittenText == "XX",
                "repeated real previews do not leak lastIndex from g/y rules");
    }
}
