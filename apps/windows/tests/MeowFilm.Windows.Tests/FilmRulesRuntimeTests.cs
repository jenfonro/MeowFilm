using System.IO;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;

namespace MeowFilm.Windows.Tests;

/// <summary>Production C# consumer through the actual Windows HTTP runtime and native addon.</summary>
public static class FilmRulesRuntimeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-rules-runtime: real Node draft save opaque revision and restart use the unique rules owner",
            () => MusicQualityConsumerTests.Sta(Persistence));
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
            await using var rules = new FilmRuleService(host.Client);
            await rules.InitializeAsync();
            string original = rules.Revision;
            Check(original.Length > 0 && rules.GetSavedSnapshot().EpisodeRules.Count > 0, "real get decodes the opaque revision and defaults");
            var preview = await rules.ProcessAsync(new(FilmRuleSection.Search, "[草稿]银河"), draft);
            Check(preview.ProcessedText == "银河" && rules.Revision == original, "draft preview executes through Node without publishing a save");
            await rules.InitializeAsync();
            Check(rules.Revision == original && !rules.GetSavedSnapshot().AggregateRegexRules.SequenceEqual(draft.AggregateRegexRules),
                "a real owner read after preview still returns the saved defaults");
            await rules.SaveAsync(draft);
            savedRevision = rules.Revision;
            Check(savedRevision != original && rules.GetSavedSnapshot().EpisodeRules.Count == 0 &&
                (await rules.ProcessAsync(new(FilmRuleSection.Search, "[草稿]银河"))).ProcessedText == "银河",
                "successful save commits exactly the whole draft and saved preview uses that owner snapshot");
        }
        await using var reopened = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(root);
        await using var restored = new FilmRuleService(reopened.Client);
        await restored.InitializeAsync();
        Check(restored.Revision == savedRevision && restored.GetSavedSnapshot().EpisodeRules.Count == 0 &&
            restored.GetSavedSnapshot().AggregateRegexRules.SequenceEqual(draft.AggregateRegexRules),
            "a real process restart preserves the new rules document including deliberately empty groups");
        await restored.RestoreDefaultsAsync(FilmRuleSection.Search, draft);
        Check(restored.GetSavedSnapshot().AggregateRegexRules.Count > 0 && restored.GetSavedSnapshot().EpisodeRules.Count == 0,
            "section restore consumes Node defaults while keeping other empty draft groups");
        await restored.ClearAsync();
        Check(restored.GetSavedSnapshot().EpisodeRules.Count > 0, "real clear consumes the owner's default snapshot");
    }
    private static async Task Text()
    {
        await using var host = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(Data());
        await using var rules = new FilmRuleService(host.Client);
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
        Check(normalized == new FilmMatchRule("a", "$1", "gy"), "flags and replacement come from the Node normalizer");
        draft = draft with { MovieRules = [new("a", "X", "gy")] };
        foreach (int _ in Enumerable.Range(0, 2))
            Check((await rules.ProcessAsync(new(FilmRuleSection.Movie, "aa"), draft)).RewrittenText == "XX",
                "repeated real previews do not leak lastIndex from g/y rules");
    }
}
