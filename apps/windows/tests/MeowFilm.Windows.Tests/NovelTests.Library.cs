using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private const string OpenLibrary = """
        {"version":1,
         "entries":[{"bookId":"raw-book","identityKey":"raw-key","onShelf":true,"chapterCount":2,
           "readAt":"2026-01-01T00:00:00+00:00","contentSignature":"original",
           "book":{"installationId":"a","revision":1,"bookUrl":"https://novel.invalid/book","name":"原书名",
             "data":{"extra":{"fraction":0.12345678901234567890123456789},"é":3,"e\u0301":4,
               "runtimeContext":{"big":9007199254740993123456789}}},
           "anchor":{"chapterUrl":"chapter-1","chapterTitle":"第一章","chapterIndex":0,"blockIndex":1,"utf16Offset":2,"ratio":0.25}}],
         "appearance":{"mode":"future-mode","conversion":"future-conversion","theme":99,"fontSize":22,
           "lineHeight":1.8},
         "rules":[{"id":"raw-rule","name":"原规则","pattern":"广告","replacement":"","isRegex":false,
           "extra":{"fraction":0.12345678901234567890123456789},"é":9,"e\u0301":10}]}
        """;
    private static JsonElement StoredLibrary(MemoryStore store)
    {
        using var json = JsonDocument.Parse(store.Bytes!); return json.RootElement.Clone();
    }
    private static void RawNumber(JsonElement value, string key, string expected) =>
        Check(value.GetProperty(key).GetRawText() == expected, "A raw JSON number changed: " + key);
    private static void DifferentKeys(JsonElement value, int first, int second) =>
        Check(value.GetProperty("é").GetInt32() == first && value.GetProperty("e\u0301").GetInt32() == second,
            "Distinct UTF8 property names were normalized or merged.");
    private static async Task LibraryRoundTrip()
    {
        await using var store = new MemoryStore { Bytes = Encoding.UTF8.GetBytes(OpenLibrary) };
        var library = new NovelLibrary(store); await library.InitializeAsync();
        Check(library.Entries.Single().BookId == "raw-book" && library.Rules.Single().Id == "raw-rule");
        await library.PinAsync("raw-book");
        await library.SaveMetadataAsync(library.Entries.Single() with { Book = library.Entries.Single().Book with { Name = "新书名" }, ChapterCount = 3 });
        await library.RecordReadingAsync(library.Entries.Single() with
            { Anchor = library.Entries.Single().Anchor! with { Utf16Offset = 5 }, ReadAt = DateTimeOffset.UtcNow });
        await library.SetShelfAsync("raw-book", false); await library.SetShelfAsync("raw-book", true);
        await library.SaveEntryAsync(library.Entries.Single() with { ContentSignature = "new" });
        await library.SaveRulesAsync([library.Rules.Single() with { Name = "新规则" }]);
        var rules = library.Rules.ToArray();
        await library.SaveAppearanceAsync(library.Appearance with { FontSize = 27 });
        Check(rules.SequenceEqual(library.Rules), "An unrelated local write changed the rule execution snapshot.");
        await library.SaveRulesAsync(library.Rules.Select(rule => rule with { }).ToArray());
        Check(rules.SequenceEqual(library.Rules), "An equivalent rule save changed the rule execution snapshot.");
        var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
        Check(reopened.Entries.Single() is { OnShelf: true, ChapterCount: 3, ContentSignature: "new", Anchor.Utf16Offset: 5 } &&
            reopened.Entries.Single().Book.Name == "新书名" && reopened.Rules.Single().Name == "新规则");
        Check(reopened.Appearance.Mode == "future-mode" && reopened.Appearance.Conversion == "future-conversion" && reopened.Appearance.Theme == 99);
        var raw = StoredLibrary(store); var entry = raw.GetProperty("entries")[0]; var book = entry.GetProperty("book"); var anchor = entry.GetProperty("anchor");
        var appearance = raw.GetProperty("appearance"); var rule = raw.GetProperty("rules")[0];
        RawNumber(book.GetProperty("data").GetProperty("extra"), "fraction", "0.12345678901234567890123456789");
        RawNumber(book.GetProperty("data").GetProperty("runtimeContext"), "big", "9007199254740993123456789");
        RawNumber(appearance, "lineHeight", "1.8");
        RawNumber(rule.GetProperty("extra"), "fraction", "0.12345678901234567890123456789");
        DifferentKeys(book.GetProperty("data"), 3, 4); DifferentKeys(rule, 9, 10);
        await reopened.DeleteHistoryAsync("raw-book");
        Check(reopened.Entries.Single() is { OnShelf: true, ReadAt: null, Anchor: null });
        Check(StoredLibrary(store).GetProperty("entries")[0].GetProperty("book").GetProperty("data").GetProperty("runtimeContext").GetProperty("big").GetRawText() == "9007199254740993123456789");
    }
    private static async Task LibraryIsolation()
    {
        await using var store = new MemoryStore();
        var library = new NovelLibrary(store);
        await library.SaveEntryAsync(new("live", "live-key", Book(), OnShelf: true));
        await library.SaveEntryAsync(new("other", "other-key", Book(url: "https://novel.invalid/other"), OnShelf: true));
        var before = store.Bytes!;
        foreach (string sql in new[]
        {
            "INSERT INTO windows_novel_library_entries SELECT * FROM windows_novel_library_entries",
            "UPDATE windows_novel_library_entries SET scope_key='missing-parent'",
            "UPDATE windows_novel_library_entries SET ordinal=-1",
            "UPDATE windows_novel_library_entries SET onShelf=2"
        })
        {
            try
            {
                await store.Inner.WriteAsync(db =>
                {
                    db.Execute("UPDATE windows_novel_library_entries SET contentSignature='must-roll-back'");
                    return db.Execute(sql);
                });
                throw new InvalidOperationException("Expected relational constraint failure");
            }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict) { }
            Check(store.Bytes!.SequenceEqual(before), "constraint failure must roll back the complete transaction");
        }
        await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_novel_library_entries_book SET revision=0 WHERE entries_key='other'"));
        var bad = StoredLibrary(store).GetProperty("entries").EnumerateArray().Single(row => row.GetProperty("bookId").GetString() == "other");
        await library.PinAsync("live");
        Check(JsonElement.DeepEquals(bad, StoredLibrary(store).GetProperty("entries").EnumerateArray().Single(row => row.GetProperty("bookId").GetString() == "other")),
            "a selected row write preserves unrelated corrupt data");
        before = store.Bytes!;
        try { await library.PinAsync("other"); throw new InvalidOperationException("Corrupt selected row was replaced"); }
        catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
        var reopened = new NovelLibrary(store);
        try { await reopened.InitializeAsync(); throw new InvalidOperationException("Corrupt entry was silently discarded"); }
        catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
        Check(store.Bytes!.SequenceEqual(before));
        await library.PrepareShutdownAsync(); await reopened.PrepareShutdownAsync();
    }
    private static async Task RuleProjectionExecution()
    {
        const string text = """
            {"version":1,"entries":[],"appearance":{},"rules":[
              {"id":"name","name":"第一条","unused":9007199254740993123456789,"group":"正文","pattern":"广告","replacement":"甲","isRegex":false,
               "isEnabled":true,"scopeTitle":false,"scopeContent":true,"scope":"","excludeScope":"","order":0,"timeoutMillisecond":3000},
              {"id":"group","name":"第二条","group":"次组","pattern":"甲","replacement":"乙","isRegex":false,"order":1,
               "isEnabled":true,"scopeTitle":false,"scopeContent":true,"scope":"","excludeScope":"","timeoutMillisecond":3000}]}
            """;
        await using var store = new MemoryStore { Bytes = Encoding.UTF8.GetBytes(text) };
        var library = new NovelLibrary(store); await library.InitializeAsync();
        var original = StoredLibrary(store).GetProperty("rules");
        Check(library.Rules.Select(rule => rule.Id).SequenceEqual(["name", "group"]) &&
            library.Rules[0].Name == "第一条" && library.Rules[1].Group == "次组");
        var runtime = new Runtime { Text = "广告正文" }; var sources = NovelTestSources.Create(runtime);
        await using var engine = new RuleProbe(); await using var rules = new NovelRuleProgram(engine);
        var session = new NovelBookSession(sources, library, rules, new("projection", "key", Book()));
        NovelBookSession? restored = null;
        try
        {
            await session.ContinueAsync(); Check(session.Blocks.Single().Text == "乙正文");
            await session.SaveProgressAsync(new("chapter-1", "第一章", 0, 0, 1, .4));
            int transforms = engine.Transforms;
            await library.SaveAppearanceAsync(library.Appearance with { FontSize = 25 });
            await library.SaveRulesAsync(library.Rules.Select(rule => rule with { }).ToArray());
            await session.ContinueAsync();
            Check(engine.Transforms == transforms && JsonElement.DeepEquals(original, StoredLibrary(store).GetProperty("rules")),
                "An unrelated or equivalent save changed raw rules or reprocessed unchanged input.");
            var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
            restored = new(sources, reopened, rules, reopened.Entries.Single());
            await restored.ContinueAsync(); Check(restored.Blocks.Single().Text == "乙正文");
            var blocks = restored.Blocks; var entry = restored.Entry; var chapter = restored.Chapter;
            var invalid = new NovelRule { Id = "invalid", Pattern = "[", Order = 2 };
            await reopened.SaveRulesAsync(reopened.Rules.Append(invalid).ToArray());
            int writes = store.Writes;
            try { await restored.ReprocessAsync(); throw new InvalidOperationException("A bad enabled rule disappeared from execution."); }
            catch (BridgeException error) { Check(error.Code == "INVALID_REQUEST"); }
            Check(ReferenceEquals(restored.Blocks, blocks) && restored.Entry == entry &&
                ReferenceEquals(restored.Chapter, chapter) && store.Writes == writes &&
                reopened.Rules.Single(rule => rule.Id == "invalid").Pattern == "[");
            var raw = StoredLibrary(store).GetProperty("rules");
            Check(JsonElement.DeepEquals(raw[0], original[0]) && JsonElement.DeepEquals(raw[1], original[1]));
            RawNumber(raw[0], "unused", "9007199254740993123456789");
            await reopened.SaveRulesAsync(reopened.Rules.Select(rule => rule.Id == "invalid" ? rule with { Enabled = false } : rule).ToArray());
            await restored.ReprocessAsync(); Check(restored.Blocks.Single().Text == "乙正文" && restored.Entry.Anchor == entry.Anchor);
        }
        finally
        {
            if (restored is not null) await restored.PrepareShutdownAsync();
            await session.PrepareShutdownAsync(); await sources.PrepareShutdownAsync();
        }
    }
    private static async Task LibraryGuards()
    {
        foreach (string sql in new[] { "UPDATE windows_novel_library SET version=2", "UPDATE windows_novel_library SET shape_mask=shape_mask & ~2" })
        {
            await using var corrupt = new MemoryStore();
            var initialized = new NovelLibrary(corrupt); await initialized.SaveAppearanceAsync(new() { FontSize = 24 });
            await corrupt.Inner.WriteAsync(db => db.Execute(sql)); var before = corrupt.Bytes!;
            var library = new NovelLibrary(corrupt);
            try { await library.SaveAppearanceAsync(new()); throw new InvalidOperationException("An unreadable root was overwritten"); }
            catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
            Check(corrupt.Bytes!.SequenceEqual(before));
            await initialized.PrepareShutdownAsync(); await library.PrepareShutdownAsync();
        }
        await using var store = new MemoryStore();
        var first = new NovelLibrary(store); var other = new NovelLibrary(store);
        await first.InitializeAsync(); await other.InitializeAsync();
        await first.SaveEntryAsync(new("winner", "key", Book(), OnShelf: true));
        await other.SaveAppearanceAsync(new() { FontSize = 30 });
        var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
        Check(reopened.Appearance.FontSize == 30 && reopened.Entries.Single().BookId == "winner",
            "independent field writes preserve another owner's committed entry");
        int writes = store.Writes; var accepted = store.Bytes!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await first.SaveAppearanceAsync(new() { FontSize = 31 }, cancellation.Token); throw new InvalidOperationException("Cancelled mutation was admitted"); }
        catch (OperationCanceledException) { }
        Check(store.Writes == writes && store.Bytes!.SequenceEqual(accepted));
        await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_novel_library SET version=2"));
        accepted = store.Bytes!;
        try { await other.SaveAppearanceAsync(new() { FontSize = 32 }); throw new InvalidOperationException("Loaded owner reset a corrupt root"); }
        catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
        Check(store.Bytes!.SequenceEqual(accepted));
        await first.PrepareShutdownAsync(); await other.PrepareShutdownAsync(); await reopened.PrepareShutdownAsync();
    }
    private static async Task AppearanceRoundTrip()
    {
        const string text = """
            {"version":1,"entries":[],"rules":[],"appearance":{"mode":"future-mode","conversion":"future-conversion","theme":99,
              "fontSize":22,"lineHeight":1.8,"background":"future-color",
              "margin":32}}
            """;
        await using var store = new MemoryStore { Bytes = Encoding.UTF8.GetBytes(text) };
        var library = new NovelLibrary(store); await library.InitializeAsync();
        var initial = library.Appearance;
        Check(initial.Mode == "future-mode" && initial.Conversion == "future-conversion" && initial.Theme == 99);
        var display = initial.ForReading();
        Check(display.Mode == "none" && display.Conversion == "none" && display.Theme == 0 && display.Background == new NovelAppearance().Background);
        var editor = new NovelAppearanceEditor(initial);
        var host = GeneralAppearanceTests.Window(editor); NovelUi.Theme(host); host.Show();
        try
        {
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            ComboBox Select(string label) => Find<ComboBox>(editor).Single(value => AutomationProperties.GetName(value) == label);
            TextBox Input(string label) => Find<TextBox>(editor).Single(value => AutomationProperties.GetName(value) == label);
            Check(Select("阅读模式").SelectedIndex == 2 && Select("简繁转换").SelectedIndex == 0 && Select("主题").SelectedIndex == 0);
            await library.SaveAppearanceAsync(editor.Read());
            var raw = StoredLibrary(store).GetProperty("appearance");
            RawNumber(raw, "fontSize", "22");
            RawNumber(raw, "margin", "32");
            Input("字号").Text = "27"; await library.SaveAppearanceAsync(editor.Read());
            var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
            Check(reopened.Appearance.FontSize == 27 && reopened.Appearance.Mode == initial.Mode && reopened.Appearance.Conversion == initial.Conversion && reopened.Appearance.Theme == initial.Theme && reopened.Appearance.Background == "future-color");
            raw = StoredLibrary(store).GetProperty("appearance");
            RawNumber(raw, "lineHeight", "1.8"); RawNumber(raw, "margin", "32");
            Select("阅读模式").SelectedIndex = 0; Select("简繁转换").SelectedIndex = 1; Select("主题").SelectedIndex = 1;
            await library.SaveAppearanceAsync(editor.Read());
            reopened = new NovelLibrary(store); await reopened.InitializeAsync();
            Check(reopened.Appearance.Mode == "cover" && reopened.Appearance.Conversion == "simplified" && reopened.Appearance.Theme == 1 &&
                reopened.Appearance.Background == NovelAppearance.Themes[1].Background && reopened.Appearance.Foreground == NovelAppearance.Themes[1].Foreground);
            RawNumber(StoredLibrary(store).GetProperty("appearance"), "margin", "32");
        }
        finally { host.Close(); await library.PrepareShutdownAsync(); }
    }
    private static async Task OfflineRules()
    {
        await using var store = new MemoryStore();
        var sourceRuntime = new Runtime();
        await using var runtime = new RuleReceiptRuntime { Respond = (_, _) => throw new IOException("runtime offline") };
        await using var module = new NovelCapability(NovelTestSources.Create(sourceRuntime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        await ((NovelSettingsView)module.SettingsView).ImportRulesAsync("""
            [{"id":"bad-regex","pattern":"[","isRegex":true},
             {"id":"disabled","pattern":"x","isEnabled":false,"timeoutMillisecond":1},
             {"id":"literal","pattern":"[x]","isRegex":false,"regex":true,"enable":false},false]
            """, default);
        Check(module.Library.Rules.Count == 3 && module.Library.Rules[0].Pattern == "[" && module.Library.Rules[1].TimeoutMillisecond == 1 &&
            module.Library.Rules[2].Enabled && !module.Library.Rules[2].IsRegex && module.Notice.Contains("规则 4 无效"));
        Check(runtime.Calls.Count == 0 && sourceRuntime.Calls.Count == 0, "Local rule import performed a runtime preflight.");
        var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
        Check(reopened.Rules.Count == 3 && reopened.Rules[0].Pattern == "[" && reopened.Rules[1].Enabled == false);
        var editor = new NovelRuleEditor(module.Library.Rules[1], _ => { });
        Check(editor.Read().TimeoutMillisecond == 1, "The native rule budget changed while opening the editor.");
    }
}
