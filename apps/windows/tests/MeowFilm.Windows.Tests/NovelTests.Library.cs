using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private const string OpenLibrary = """
        {"version":1,
         "extra":{"big":9007199254740993123456789,"fraction":0.12345678901234567890123456789,"wide":1e400},
         "é":{"keep":1},"e\u0301":{"keep":2},
         "entries":[{"bookId":"raw-book","identityKey":"raw-key","onShelf":true,"chapterCount":2,
           "readAt":"2026-01-01T00:00:00+00:00","contentSignature":"original",
           "extra":{"big":9007199254740993123456789},"é":1,"e\u0301":2,"BookId":{"extra":true},
           "book":{"installationId":"a","revision":1,"bookUrl":"https://novel.invalid/book","name":"原书名",
             "extra":{"fraction":0.12345678901234567890123456789},"é":3,"e\u0301":4,
             "data":{"runtimeContext":{"big":9007199254740993123456789}}},
           "anchor":{"chapterUrl":"chapter-1","chapterTitle":"第一章","chapterIndex":0,"blockIndex":1,"utf16Offset":2,"ratio":0.25,
             "extra":{"fraction":0.12345678901234567890123456789},"é":5,"e\u0301":6}}],
         "appearance":{"mode":"future-mode","conversion":"future-conversion","theme":99,"fontSize":22,
           "lineHeight":1.80000000000000000000000000001,
           "extra":{"big":9007199254740993123456789},"é":7,"e\u0301":8},
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
        RawNumber(raw.GetProperty("extra"), "big", "9007199254740993123456789");
        RawNumber(raw.GetProperty("extra"), "fraction", "0.12345678901234567890123456789");
        RawNumber(raw.GetProperty("extra"), "wide", "1e400");
        RawNumber(entry.GetProperty("extra"), "big", "9007199254740993123456789");
        RawNumber(book.GetProperty("extra"), "fraction", "0.12345678901234567890123456789");
        RawNumber(book.GetProperty("data").GetProperty("runtimeContext"), "big", "9007199254740993123456789");
        RawNumber(anchor.GetProperty("extra"), "fraction", "0.12345678901234567890123456789");
        RawNumber(appearance.GetProperty("extra"), "big", "9007199254740993123456789");
        RawNumber(appearance, "lineHeight", "1.80000000000000000000000000001");
        RawNumber(rule.GetProperty("extra"), "fraction", "0.12345678901234567890123456789");
        Check(raw.GetProperty("é").GetProperty("keep").GetInt32() == 1 && raw.GetProperty("e\u0301").GetProperty("keep").GetInt32() == 2);
        DifferentKeys(entry, 1, 2); DifferentKeys(book, 3, 4); DifferentKeys(anchor, 5, 6); DifferentKeys(appearance, 7, 8); DifferentKeys(rule, 9, 10);
        Check(entry.GetProperty("BookId").GetProperty("extra").GetBoolean());
        await reopened.DeleteHistoryAsync("raw-book");
        Check(reopened.Entries.Single() is { OnShelf: true, ReadAt: null, Anchor: null });
        Check(StoredLibrary(store).GetProperty("entries")[0].GetProperty("extra").GetProperty("big").GetRawText() == "9007199254740993123456789");
    }
    private static async Task LibraryIsolation()
    {
        const string text = """
            {"version":1,"entries":[
              {"bookId":"live","identityKey":"live-key","book":{"installationId":"a","revision":1,"bookUrl":"live","name":"健康"},"onShelf":true},
              {"bookId":"dormant","identityKey":"dormant-key","book":{"installationId":"a","revision":1,"bookUrl":"dormant","name":"不相关记录"}},
              null,17,{"bookId":"broken","identityKey":"broken-key","book":false,"extra":9007199254740993123456789},
              {"bookId":"duplicate","identityKey":"duplicate-key","book":{"installationId":"a","revision":1,"bookUrl":"duplicate-a","name":"甲"}},
              {"bookId":"duplicate","identityKey":"duplicate-key","book":{"installationId":"a","revision":1,"bookUrl":"duplicate-b","name":"乙"}},
              {"bookId":"first","bookId":"second","book":{"installationId":"a","revision":1,"bookUrl":"ambiguous","name":"歧义"}}],
             "appearance":{"mode":{"unreadable":true},"fontSize":"broken","extra":1e400},
             "rules":[{"id":"keep","pattern":"广告"},false,{"id":"broken-rule","pattern":[]},
               {"id":"duplicate-rule","pattern":"甲"},{"id":"duplicate-rule","pattern":"乙"},
               {"id":"rule-first","id":"rule-second","pattern":"歧义"}]}
            """;
        await using var store = new MemoryStore { Bytes = Encoding.UTF8.GetBytes(text) };
        var before = StoredLibrary(store); var library = new NovelLibrary(store); await library.InitializeAsync();
        Check(library.Entries.Select(entry => entry.BookId).SequenceEqual(["live", "dormant"]) && library.Rules.Single().Id == "keep");
        Check(library.Appearance.FontSize == 22 && library.Appearance.Mode == "none");
        await library.PinAsync("live");
        await library.SaveRulesAsync([library.Rules.Single() with { Group = "健康组", Enabled = false }]);
        await library.SaveAppearanceAsync(library.Appearance with { FontSize = 24 });
        var raw = StoredLibrary(store);
        foreach (int index in Enumerable.Range(2, 6))
            Check(JsonElement.DeepEquals(before.GetProperty("entries")[index], raw.GetProperty("entries")[index]), "An isolated entry slot was rewritten or deleted.");
        foreach (int index in Enumerable.Range(1, 5))
            Check(JsonElement.DeepEquals(before.GetProperty("rules")[index], raw.GetProperty("rules")[index]), "An isolated rule slot was rewritten or deleted.");
        Check(JsonElement.DeepEquals(before.GetProperty("appearance").GetProperty("mode"), raw.GetProperty("appearance").GetProperty("mode")));
        RawNumber(raw.GetProperty("appearance"), "extra", "1e400");
        RawNumber(raw.GetProperty("entries")[4], "extra", "9007199254740993123456789");
        Check(raw.GetProperty("entries").EnumerateArray().Single(value => value.ValueKind == JsonValueKind.Object && NovelJson.Text(value, "bookId") == "dormant").GetProperty("book").GetProperty("name").GetString() == "不相关记录");
        var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
        Check(reopened.Entries.Count == 2 && reopened.Entries.Single(entry => entry.BookId == "live").PinnedAt is not null && reopened.Rules.Single().Group == "健康组");
        int writes = store.Writes;
        foreach (Func<Task> action in new Func<Task>[]
        {
            () => reopened.SaveEntryAsync(new("broken", "key", Book())),
            () => reopened.SaveEntryAsync(new("duplicate", "key", Book())),
            () => reopened.SetShelfAsync("broken", true),
            () => reopened.PinAsync("first"),
            () => reopened.DeleteHistoryAsync("second"),
            () => reopened.SaveRulesAsync([new() { Id = "broken-rule", Pattern = "x" }]),
            () => reopened.SaveRulesAsync([new() { Id = "rule-first", Pattern = "x" }])
        })
        {
            try { await action(); throw new InvalidOperationException("An unusable or ambiguous identity was overwritten."); }
            catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
        }
        Check(store.Writes == writes && JsonElement.DeepEquals(raw, StoredLibrary(store)));
        foreach (string key in new[] { "broken-key", "duplicate-key" })
        {
            try { reopened.Find(Book(), key); throw new InvalidOperationException("An ambiguous identity was guessed."); }
            catch (InvalidDataException error) { Check(error.Message == "AMBIGUOUS_NOVEL_IDENTITY"); }
        }
        foreach (string section in new[] { "entries", "appearance", "rules" })
        {
            string data = "{\"version\":1,\"entries\":[],\"appearance\":{},\"rules\":[]}";
            data = data.Replace("\"" + section + "\":" + (section == "appearance" ? "{}" : "[]"), "\"" + section + "\":false", StringComparison.Ordinal);
            await using var broken = new MemoryStore { Bytes = Encoding.UTF8.GetBytes(data) };
            var partial = new NovelLibrary(broken); await partial.InitializeAsync();
            if (section == "appearance") await partial.SaveEntryAsync(new("healthy", "key", Book(), OnShelf: true));
            else await partial.SaveAppearanceAsync(partial.Appearance with { FontSize = 25 });
            Check(StoredLibrary(broken).GetProperty(section).ValueKind == JsonValueKind.False);
            int accepted = broken.Writes;
            try
            {
                if (section == "entries") await partial.SaveEntryAsync(new("new", "key", Book()));
                else if (section == "appearance") await partial.SaveAppearanceAsync(new() { FontSize = 26 });
                else await partial.SaveRulesAsync([new() { Pattern = "x" }]);
                throw new InvalidOperationException("An unusable section was replaced from its empty projection.");
            }
            catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
            Check(broken.Writes == accepted && StoredLibrary(broken).GetProperty(section).ValueKind == JsonValueKind.False);
        }
    }
    private static async Task LibraryGuards()
    {
        foreach (string text in new[] { "broken", "[]", "{\"version\":2,\"entries\":[],\"appearance\":{},\"rules\":[]}",
            "{\"version\":1,\"version\":2,\"entries\":[],\"appearance\":{},\"rules\":[]}" })
        {
            await using var corrupt = new MemoryStore { Bytes = Encoding.UTF8.GetBytes(text) };
            var library = new NovelLibrary(corrupt);
            try { await library.SaveAppearanceAsync(new()); throw new InvalidOperationException("An unreadable root was overwritten."); }
            catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
            Check(corrupt.Writes == 0 && Encoding.UTF8.GetString(corrupt.Bytes!) == text);
        }
        await using var store = new MemoryStore();
        var first = new NovelLibrary(store); var stale = new NovelLibrary(store);
        await first.InitializeAsync(); await stale.InitializeAsync();
        await first.SaveEntryAsync(new("winner", "key", Book(), OnShelf: true)); var accepted = store.Bytes!.ToArray();
        try { await stale.SaveAppearanceAsync(new() { FontSize = 30 }); throw new InvalidOperationException("A stale CAS published."); }
        catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Conflict); }
        Check(stale.Appearance.FontSize == 22 && stale.Entries.Count == 0 && store.Bytes!.SequenceEqual(accepted));
        int writes = store.Writes;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await first.SaveAppearanceAsync(new() { FontSize = 31 }, cancellation.Token); throw new InvalidOperationException("A cancelled mutation was admitted."); }
        catch (OperationCanceledException) { }
        Check(store.Writes == writes && first.Appearance.FontSize == 22 && store.Bytes!.SequenceEqual(accepted));
    }
    private static async Task AppearanceRoundTrip()
    {
        const string text = """
            {"version":1,"entries":[],"rules":[],"appearance":{"mode":"future-mode","conversion":"future-conversion","theme":99,
              "fontSize":22.000000000000000000001,"lineHeight":1.800000000000000000001,"background":"future-color",
              "margin":1e400,"extra":{"big":9007199254740993123456789},"é":1,"e\u0301":2}}
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
            RawNumber(raw, "fontSize", "22.000000000000000000001");
            RawNumber(raw, "margin", "1e400");
            Input("字号").Text = "27"; await library.SaveAppearanceAsync(editor.Read());
            var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
            Check(reopened.Appearance.FontSize == 27 && reopened.Appearance.Mode == initial.Mode && reopened.Appearance.Conversion == initial.Conversion && reopened.Appearance.Theme == initial.Theme && reopened.Appearance.Background == "future-color");
            raw = StoredLibrary(store).GetProperty("appearance");
            RawNumber(raw, "lineHeight", "1.800000000000000000001"); RawNumber(raw, "margin", "1e400");
            RawNumber(raw.GetProperty("extra"), "big", "9007199254740993123456789"); DifferentKeys(raw, 1, 2);
            Select("阅读模式").SelectedIndex = 0; Select("简繁转换").SelectedIndex = 1; Select("主题").SelectedIndex = 1;
            await library.SaveAppearanceAsync(editor.Read());
            reopened = new NovelLibrary(store); await reopened.InitializeAsync();
            Check(reopened.Appearance.Mode == "cover" && reopened.Appearance.Conversion == "simplified" && reopened.Appearance.Theme == 1 &&
                reopened.Appearance.Background == NovelAppearance.Themes[1].Background && reopened.Appearance.Foreground == NovelAppearance.Themes[1].Foreground);
            RawNumber(StoredLibrary(store).GetProperty("appearance"), "margin", "1e400");
        }
        finally { host.Close(); await library.PrepareShutdownAsync(); }
    }
    private static async Task OfflineRules()
    {
        await using var store = new MemoryStore();
        var sourceRuntime = new Runtime();
        await using var runtime = new RuleReceiptRuntime { Respond = (_, _) => throw new IOException("runtime offline") };
        await using var module = new NovelCapability(new NovelSourceService(sourceRuntime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        await ((NovelSettingsView)module.SettingsView).ImportRulesAsync("""
            [{"id":"bad-regex","pattern":"[","isRegex":true},
             {"id":"disabled","pattern":"x","isEnabled":false,"timeoutMillisecond":0},
             {"id":"literal","pattern":"[x]","isRegex":false,"regex":true,"enable":false},false]
            """, default);
        Check(module.Library.Rules.Count == 3 && module.Library.Rules[0].Pattern == "[" && module.Library.Rules[1].TimeoutMillisecond == 0 &&
            module.Library.Rules[2].Enabled && !module.Library.Rules[2].IsRegex && module.Notice.Contains("规则 4 无效"));
        Check(runtime.Calls.Count == 0 && sourceRuntime.Calls.Count == 0, "Local rule import performed a runtime preflight.");
        var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
        Check(reopened.Rules.Count == 3 && reopened.Rules[0].Pattern == "[" && reopened.Rules[1].Enabled == false);
        var editor = new NovelRuleEditor(module.Library.Rules[1], _ => { });
        Check(editor.Read().TimeoutMillisecond == 0, "An unchanged rule budget was checked before execution.");
    }
}
