using MeowFilm.Core.Scripting;
using MeowFilm.Platform.Scripting;

namespace MeowFilm.Windows.Tests;

public static class NativeJavaScriptTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("native JavaScript: JSON Unicode regex and isolated application function", JsonAndRegex);
        yield return new("native JavaScript: pathological regex cancels and subsequent call recovers", CancelRegex);
        yield return new("native JavaScript: execution deadline interrupts and subsequent call recovers", Timeout);
        yield return new("native JavaScript: allocation failure recovers without a second engine backend", MemoryFailure);
        yield return new("native JavaScript: disposal drains active and queued calls", Close);
    }

    private static async Task JsonAndRegex()
    {
        await using var engine = new NativeJavaScriptJsonEngine("""
            globalThis.meowfilmProcess = input => ({
                text: input.text.replace(/(?<=第)(\d+)(?=集)/g, '[$1]'),
                emoji: /\p{Extended_Pictographic}/u.test(input.text),
                node: typeof process, http: typeof fetch, require: typeof require
            });
            """);
        var result = await engine.InvokeAsync(new { text = "作品2 第12集 🐱" });
        Check(result.GetProperty("text").GetString() == "作品2 第[12]集 🐱");
        Check(result.GetProperty("emoji").GetBoolean());
        Check(new[] { "node", "http", "require" }.All(key => result.GetProperty(key).GetString() == "undefined"));
        // Calls are admitted concurrently but use a single native owner.
        var values = await Task.WhenAll(Enumerable.Range(1, 20).Select(i =>
            engine.InvokeAsync(new { text = $"第{i}集" })));
        Check(values.Select(value => value.GetProperty("text").GetString())
            .SequenceEqual(Enumerable.Range(1, 20).Select(i => $"第[{i}]集")));
    }

    private const string RegexProgram = """
        globalThis.meowfilmProcess = input => input.slow
            ? /^(a+)+$/.test(input.text) : {text: input.text};
        """;

    private static async Task CancelRegex()
    {
        await using var engine = new NativeJavaScriptJsonEngine(RegexProgram);
        await engine.InvokeAsync(new { text = "warm" });
        using var cancellation = new CancellationTokenSource();
        var work = engine.InvokeAsync(new { slow = true, text = new string('a', 128) + "!" }, cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        try { await work.WaitAsync(TimeSpan.FromSeconds(6)); throw new InvalidOperationException("Cancellation was not observed"); }
        catch (OperationCanceledException) { }
        Check((await engine.InvokeAsync(new { text = "after cancel" })).GetProperty("text").GetString() == "after cancel");
    }

    private static async Task Timeout()
    {
        await using var engine = new NativeJavaScriptJsonEngine(RegexProgram);
        try
        {
            await engine.InvokeAsync(new { slow = true, text = new string('a', 128) + "!" }).WaitAsync(TimeSpan.FromSeconds(8));
            throw new InvalidOperationException("Native deadline was not observed");
        }
        catch (JavaScriptEngineException error) { Check(error.Code == "EXECUTION_TIMEOUT"); }
        Check((await engine.InvokeAsync(new { text = "after deadline" })).GetProperty("text").GetString() == "after deadline");
    }

    private static async Task MemoryFailure()
    {
        await using var engine = new NativeJavaScriptJsonEngine("""
            globalThis.meowfilmProcess = input => input.large ? new ArrayBuffer(128 * 1024 * 1024) : {ok:true};
            """);
        try { await engine.InvokeAsync(new { large = true }); throw new InvalidOperationException("Missing memory limit"); }
        catch (JavaScriptEngineException error) { Check(error.Code == "EXECUTION_FAILED"); }
        Check((await engine.InvokeAsync(new { })).GetProperty("ok").GetBoolean());
    }

    private static async Task Close()
    {
        var engine = new NativeJavaScriptJsonEngine(RegexProgram);
        await engine.InvokeAsync(new { text = "warm" });
        var active = engine.InvokeAsync(new { slow = true, text = new string('a', 128) + "!" });
        var queued = engine.InvokeAsync(new { text = "pending" });
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
        foreach (var work in new[] { active, queued })
        {
            Check(work.IsCompleted);
            try { await work; throw new InvalidOperationException("Closed work returned success"); }
            catch (OperationCanceledException) { }
        }
        try { await engine.InvokeAsync(new { }); throw new InvalidOperationException("Closed engine accepted work"); }
        catch (ObjectDisposedException) { }
        await engine.DisposeAsync();
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Native JavaScript assertion failed");
    }
}
