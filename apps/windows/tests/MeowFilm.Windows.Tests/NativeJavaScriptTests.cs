using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
        yield return new("native JavaScript: regex compilation cancellation drains then recovers", CancelCompilation);
        yield return new("native JavaScript: regex compilation deadline drains then recovers", CompilationDeadline);
        yield return new("native JavaScript: disposal interrupts active regex compilation", CloseCompilation);
        yield return new("native JavaScript: regex compilation interruption cannot enter JavaScript catch", CompilationUncatchable);
        yield return new("native JavaScript: capture prescan cancellation drains then recovers", () => CancelNativeOperation(ForwardReferenceInput()));
        yield return new("native JavaScript: capture prescan deadline drains then recovers", () => NativeOperationDeadline(ForwardReferenceInput()));
        yield return new("native JavaScript: capture prescan interruption cannot enter JavaScript catch", () => NativeOperationUncatchable(ForwardReferenceInput()));
        yield return new("native JavaScript: global deletion cancellation drains then recovers", () => CancelNativeOperation(GlobalDeletionInput()));
        yield return new("native JavaScript: global deletion deadline drains then recovers", () => NativeOperationDeadline(GlobalDeletionInput()));
        yield return new("native JavaScript: global deletion interruption cannot enter JavaScript catch", () => NativeOperationUncatchable(GlobalDeletionInput()));
        yield return new("native JavaScript: capture discovery and global deletion preserve ordinary semantics", CaptureAndDeletionSemantics);
        foreach (bool backward in new[] { false, true })
        {
            string direction = backward ? "backward" : "forward";
            yield return new($"native JavaScript: warmed {direction} references cancel and recover", () => CancelReferences(backward));
            yield return new($"native JavaScript: warmed {direction} references time out and recover", () => ReferenceDeadline(backward));
            yield return new($"native JavaScript: {direction} reference interruption cannot enter JavaScript catch", () => ReferenceUncatchable(backward));
        }
        yield return new("native JavaScript: disposal drains warmed reference execution and queued calls", CloseReferences);
        yield return new("native JavaScript: reference directions assertions and captures preserve ordinary semantics", ReferenceSemantics);
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

    private const string CompilationProgram = """
        globalThis.meowfilmProcess = input => {
            if (!input.pattern) return {ok:true};
            try { return {matched: new RegExp(input.pattern, 'i').test('b')}; }
            catch { return {caught:true}; }
        };
        """;

    private static string LongLookbehind() => "(?<=" + new string('a', 1_048_576) + ")b";

    private static async Task CancelCompilation()
    {
        await using var engine = new NativeJavaScriptJsonEngine(CompilationProgram);
        Check((await engine.InvokeAsync(new { })).GetProperty("ok").GetBoolean());
        using var cancellation = new CancellationTokenSource();
        var work = engine.InvokeAsync(new { pattern = LongLookbehind() }, cancellation.Token);
        await Task.Delay(100);
        Check(!work.IsCompleted);
        cancellation.Cancel();
        try { await work.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Compilation ignored cancellation"); }
        catch (OperationCanceledException) { }
        Check((await engine.InvokeAsync(new { })).GetProperty("ok").GetBoolean());
        Check((await engine.InvokeAsync(new { pattern = "(?<=a)b" })).GetProperty("matched").GetBoolean() == false);
    }

    private static async Task CompilationDeadline()
    {
        await using var engine = new NativeJavaScriptJsonEngine(CompilationProgram);
        await engine.InvokeAsync(new { });
        try
        {
            await engine.InvokeAsync(new { pattern = LongLookbehind() }).WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Compilation ignored the native deadline");
        }
        catch (JavaScriptEngineException error) { Check(error.Code == "EXECUTION_TIMEOUT"); }
        Check((await engine.InvokeAsync(new { })).GetProperty("ok").GetBoolean());
        Check((await engine.InvokeAsync(new { pattern = "[" })).GetProperty("caught").GetBoolean());
    }

    private static async Task CloseCompilation()
    {
        var engine = new NativeJavaScriptJsonEngine(CompilationProgram);
        try
        {
            await engine.InvokeAsync(new { });
            var work = engine.InvokeAsync(new { pattern = LongLookbehind() });
            await Task.Delay(100);
            Check(!work.IsCompleted);
            var queued = engine.InvokeAsync(new { });
            await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var call in new[] { work, queued })
            {
                Check(call.IsCompleted);
                try { await call; throw new InvalidOperationException("Disposal left native work alive"); }
                catch (OperationCanceledException) { }
            }
        }
        finally { await engine.DisposeAsync(); }
    }

    private static Task CompilationUncatchable()
    {
        // Exercise the same DLL without the managed owner's exception/reset
        // path, so an outer cancellation check cannot hide a swallowed error.
        var program = Encoding.UTF8.GetBytes("""
            globalThis.caught = false;
            globalThis.meowfilmProcess = input => {
                if (!input.pattern) return {caught: globalThis.caught};
                try { new RegExp(input.pattern, 'i'); }
                catch { globalThis.caught = true; }
                return {caught: globalThis.caught};
            };
            """ + "\0");
        long deadline = long.MaxValue;
        bool signalled = false;
        RawNative.Interrupt interrupt = _ =>
        {
            if (signalled || Stopwatch.GetTimestamp() < deadline) return 0;
            signalled = true;
            return 1;
        };
        nint engine = RawNative.Create(program, (nuint)(program.Length - 1),
            64 * 1024 * 1024, 512 * 1024, interrupt, 0);
        Check(engine != 0);
        nint output = 0;
        try
        {
            var input = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { pattern = LongLookbehind() }) + "\0");
            var watch = Stopwatch.StartNew();
            deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            int status = RawNative.Invoke(engine, input, (nuint)(input.Length - 1), out output, out _);
            Check(signalled && status == 2 && output == 0 && watch.Elapsed < TimeSpan.FromSeconds(5));
            deadline = long.MaxValue;
            input = Encoding.UTF8.GetBytes("{}\0");
            status = RawNative.Invoke(engine, input, (nuint)(input.Length - 1), out output, out _);
            Check(status == 0 && output != 0);
            using var result = JsonDocument.Parse(Marshal.PtrToStringUTF8(output)!);
            Check(!result.RootElement.GetProperty("caught").GetBoolean());
        }
        finally
        {
            if (output != 0) RawNative.Free(output);
            RawNative.Destroy(engine);
            GC.KeepAlive(interrupt);
        }
        return Task.CompletedTask;
    }

    private const string NativeOperationProgram = """
        globalThis.caught = false;
        globalThis.meowfilmProcess = input => {
            if (!input.pattern) return {ok:true, caught:globalThis.caught};
            try {
                const regex = new RegExp(input.pattern, input.flags || 'i');
                return input.remove
                    ? {text:input.text.replace(regex, ''), caught:globalThis.caught}
                    : {matched:regex.test(input.text), caught:globalThis.caught};
            } catch {
                globalThis.caught = true;
                return {caught:true};
            }
        };
        """;

    private static object ForwardReferenceInput() => new
    {
        pattern = string.Concat(Enumerable.Repeat(@"\k<x>", 9999)) + new string('a', 1_048_576) + "(?<x>b)",
        text = "b", flags = "i"
    };

    private static object GlobalDeletionInput() => new
    {
        pattern = "(?=a{4096})a", text = new string('a', 1_048_576), flags = "ig", remove = true
    };

    private static async Task CancelNativeOperation(object input)
    {
        await using var engine = new NativeJavaScriptJsonEngine(NativeOperationProgram);
        await CheckNativeOperationRecovery(engine);
        using var cancellation = new CancellationTokenSource();
        var work = engine.InvokeAsync(input, cancellation.Token);
        await Task.Delay(100);
        Check(!work.IsCompleted);
        cancellation.Cancel();
        try { await work.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Native operation ignored cancellation"); }
        catch (OperationCanceledException) { }
        Check(work.IsCanceled);
        await CheckNativeOperationRecovery(engine);
    }

    private static async Task NativeOperationDeadline(object input)
    {
        await using var engine = new NativeJavaScriptJsonEngine(NativeOperationProgram);
        await CheckNativeOperationRecovery(engine);
        var work = engine.InvokeAsync(input);
        try
        {
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Native operation ignored the execution deadline");
        }
        catch (JavaScriptEngineException error) { Check(error.Code == "EXECUTION_TIMEOUT"); }
        Check(work.IsCompleted);
        await CheckNativeOperationRecovery(engine);
    }

    private static async Task CheckNativeOperationRecovery(NativeJavaScriptJsonEngine engine)
    {
        var result = await engine.InvokeAsync(new { pattern = @"\k<x>(?<x>b)", text = "b", flags = "i" });
        Check(result.GetProperty("matched").GetBoolean() && !result.GetProperty("caught").GetBoolean());
        result = await engine.InvokeAsync(new { pattern = "(?=a{4})a", text = "aaaaab", flags = "ig", remove = true });
        Check(result.GetProperty("text").GetString() == "aaab" && !result.GetProperty("caught").GetBoolean());
    }

    private static Task NativeOperationUncatchable(object request)
    {
        // One interrupt pulse, then probe the same raw context. The managed
        // cancellation/reset path cannot hide a business catch swallowing it.
        var program = Encoding.UTF8.GetBytes(NativeOperationProgram + "\0");
        long deadline = long.MaxValue;
        bool signalled = false;
        RawNative.Interrupt interrupt = _ =>
        {
            if (signalled || Stopwatch.GetTimestamp() < deadline) return 0;
            signalled = true;
            return 1;
        };
        nint engine = RawNative.Create(program, (nuint)(program.Length - 1),
            64 * 1024 * 1024, 512 * 1024, interrupt, 0);
        Check(engine != 0);
        nint output = 0;
        try
        {
            var input = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\0");
            var watch = Stopwatch.StartNew();
            deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            int status = RawNative.Invoke(engine, input, (nuint)(input.Length - 1), out output, out _);
            Check(signalled && status == 2 && output == 0 && watch.Elapsed < TimeSpan.FromSeconds(5));
            deadline = long.MaxValue;
            input = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { pattern = @"\k<x>(?<x>b)", text = "b", flags = "i" }) + "\0");
            status = RawNative.Invoke(engine, input, (nuint)(input.Length - 1), out output, out _);
            Check(status == 0 && output != 0);
            using var result = JsonDocument.Parse(Marshal.PtrToStringUTF8(output)!);
            Check(result.RootElement.GetProperty("matched").GetBoolean() &&
                !result.RootElement.GetProperty("caught").GetBoolean());
        }
        finally
        {
            if (output != 0) RawNative.Free(output);
            RawNative.Destroy(engine);
            GC.KeepAlive(interrupt);
        }
        return Task.CompletedTask;
    }

    private static async Task CaptureAndDeletionSemantics()
    {
        await using var engine = new NativeJavaScriptJsonEngine(NativeOperationProgram);
        foreach (var (pattern, text) in new[]
        {
            (@"\k<x>(?<x>b)", "b"), (@"\1(b)", "b"), (@"\2(b)", "\u0002b"),
            (@"\k", "k"), (@"\k<->", "k<->"), (@"\k<missing>", "k<missing>"),
            ("a|(?<x>b)", "b"), (@"\k<x>[a](?<x>b)", "ab")
        })
        {
            var result = await engine.InvokeAsync(new { pattern, text, flags = "i" });
            Check(result.GetProperty("matched").GetBoolean() && !result.GetProperty("caught").GetBoolean());
        }
        foreach (var (pattern, text, flags, expected) in new[]
        {
            ("a", "banana", "g", "bnn"),
            ("(?=a{4})a", "aaaaab", "ig", "aaab"),
            ("(?:)", "A🐱B", "gu", "A🐱B"),
            ("a", "Aa!", "ig", "!"),
            ("z", "abc", "g", "abc")
        })
        {
            var result = await engine.InvokeAsync(new { pattern, text, flags, remove = true });
            Check(result.GetProperty("text").GetString() == expected && !result.GetProperty("caught").GetBoolean());
        }
        foreach (var (pattern, flags) in new[] { (@"\k<missing>(?<x>b)", "i"), (@"\2(b)", "u") })
        {
            var result = await engine.InvokeAsync(new { pattern, text = "b", flags });
            Check(result.GetProperty("caught").GetBoolean() && !result.TryGetProperty("matched", out _));
        }
    }

    private const string ReferenceProgram = """
        let expression, pattern, flags;
        globalThis.caught = false;
        globalThis.meowfilmProcess = input => {
            if (!input.pattern) return {ok:true, caught:globalThis.caught};
            try {
                if (pattern !== input.pattern || flags !== input.flags) {
                    expression = new RegExp(input.pattern, input.flags);
                    pattern = input.pattern;
                    flags = input.flags;
                }
                expression.lastIndex = 0;
                return {matched:expression.test(input.text), caught:globalThis.caught};
            } catch {
                globalThis.caught = true;
                return {caught:true};
            }
        };
        """;

    // The forward case is the fixed E3 input; the second exercises the same
    // long comparison while matching backwards, not a different length limit.
    private static string ReferencePattern(bool backward) =>
        "^(?=(a{1048576}))" + (backward ? "a{1048576}" : "") +
        string.Concat(Enumerable.Repeat(backward ? @"(?<=\1)" : @"(?=\1)", 9999));

    private static async Task WarmReferences(NativeJavaScriptJsonEngine engine, string pattern)
    {
        var result = await engine.InvokeAsync(new { pattern, flags = "i", text = "b" });
        Check(!result.GetProperty("matched").GetBoolean() && !result.GetProperty("caught").GetBoolean());
    }

    private static async Task CheckReferenceRecovery(NativeJavaScriptJsonEngine engine)
    {
        foreach (string pattern in new[] { @"^(a)\1$", @"^aa(?<=\1(a))$" })
        {
            var result = await engine.InvokeAsync(new { pattern, flags = "i", text = "aa" });
            Check(result.GetProperty("matched").GetBoolean() && !result.GetProperty("caught").GetBoolean());
        }
    }

    private static async Task CancelReferences(bool backward)
    {
        await using var engine = new NativeJavaScriptJsonEngine(ReferenceProgram);
        string pattern = ReferencePattern(backward);
        await WarmReferences(engine, pattern);
        using var cancellation = new CancellationTokenSource();
        var work = engine.InvokeAsync(new { pattern, flags = "i", text = new string('a', 1_048_576) }, cancellation.Token);
        await Task.Delay(200);
        Check(!work.IsCompleted);
        var watch = Stopwatch.StartNew();
        cancellation.Cancel();
        try { await work.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Reference execution ignored cancellation"); }
        catch (OperationCanceledException) { }
        Check(work.IsCanceled && watch.Elapsed <= TimeSpan.FromSeconds(5));
        Console.WriteLine($"EVIDENCE native references backward={backward}; cancelMs={watch.Elapsed.TotalMilliseconds:F3}; call={work.Status}");
        await CheckReferenceRecovery(engine);
    }

    private static async Task ReferenceDeadline(bool backward)
    {
        await using var engine = new NativeJavaScriptJsonEngine(ReferenceProgram);
        string pattern = ReferencePattern(backward);
        await WarmReferences(engine, pattern);
        var watch = Stopwatch.StartNew();
        var work = engine.InvokeAsync(new { pattern, flags = "i", text = new string('a', 1_048_576) });
        try
        {
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Reference execution ignored the native deadline");
        }
        catch (JavaScriptEngineException error) { Check(error.Code == "EXECUTION_TIMEOUT"); }
        Check(work.IsFaulted && watch.Elapsed <= TimeSpan.FromSeconds(5));
        Console.WriteLine($"EVIDENCE native references backward={backward}; budgetMs={watch.Elapsed.TotalMilliseconds:F3}; call={work.Status}");
        await CheckReferenceRecovery(engine);
    }

    private static Task ReferenceUncatchable(bool backward)
    {
        // Warm exactly this expression before arming a single interrupt pulse.
        // The same raw context must not execute the JavaScript catch afterwards.
        var program = Encoding.UTF8.GetBytes(ReferenceProgram + "\0");
        long deadline = long.MaxValue;
        bool signalled = false;
        RawNative.Interrupt interrupt = _ =>
        {
            if (signalled || Stopwatch.GetTimestamp() < deadline) return 0;
            signalled = true;
            return 1;
        };
        nint engine = RawNative.Create(program, (nuint)(program.Length - 1),
            64 * 1024 * 1024, 512 * 1024, interrupt, 0);
        Check(engine != 0);
        nint output = 0;
        try
        {
            string pattern = ReferencePattern(backward);
            var input = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { pattern, flags = "i", text = "b" }) + "\0");
            int status = RawNative.Invoke(engine, input, (nuint)(input.Length - 1), out output, out _);
            Check(status == 0 && output != 0);
            using (var warm = JsonDocument.Parse(Marshal.PtrToStringUTF8(output)!))
                Check(!warm.RootElement.GetProperty("matched").GetBoolean() && !warm.RootElement.GetProperty("caught").GetBoolean());
            RawNative.Free(output);
            output = 0;

            input = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { pattern, flags = "i", text = new string('a', 1_048_576) }) + "\0");
            var watch = Stopwatch.StartNew();
            deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
            status = RawNative.Invoke(engine, input, (nuint)(input.Length - 1), out output, out nuint length);
            Check(signalled && status == 2 && output == 0 && length == 0 && watch.Elapsed < TimeSpan.FromSeconds(5));
            Console.WriteLine($"EVIDENCE native raw references backward={backward}; interruptMs={watch.Elapsed.TotalMilliseconds:F3}; status={status}");
            deadline = long.MaxValue;
            input = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { pattern = @"^(a)\1$", flags = "i", text = "aa" }) + "\0");
            status = RawNative.Invoke(engine, input, (nuint)(input.Length - 1), out output, out _);
            Check(status == 0 && output != 0);
            using var result = JsonDocument.Parse(Marshal.PtrToStringUTF8(output)!);
            Check(result.RootElement.GetProperty("matched").GetBoolean() && !result.RootElement.GetProperty("caught").GetBoolean());
        }
        finally
        {
            if (output != 0) RawNative.Free(output);
            RawNative.Destroy(engine);
            GC.KeepAlive(interrupt);
        }
        return Task.CompletedTask;
    }

    private static async Task CloseReferences()
    {
        await using var engine = new NativeJavaScriptJsonEngine(ReferenceProgram);
        string pattern = ReferencePattern(false);
        await WarmReferences(engine, pattern);
        var active = engine.InvokeAsync(new { pattern, flags = "i", text = new string('a', 1_048_576) });
        await Task.Delay(200);
        Check(!active.IsCompleted);
        var queued = engine.InvokeAsync(new { });
        var watch = Stopwatch.StartNew();
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var work in new[] { active, queued })
        {
            Check(work.IsCanceled);
            try { await work; throw new InvalidOperationException("Disposed reference execution returned success"); }
            catch (OperationCanceledException) { }
        }
        Check(watch.Elapsed <= TimeSpan.FromSeconds(5));
        Console.WriteLine($"EVIDENCE native references disposeMs={watch.Elapsed.TotalMilliseconds:F3}; active={active.Status}; queued={queued.Status}");
    }

    private static async Task ReferenceSemantics()
    {
        await using var engine = new NativeJavaScriptJsonEngine(ReferenceProgram);
        foreach (var (pattern, text, flags, matched) in new[]
        {
            (@"^(a)\1$", "aa", "", true), (@"^(a)\1$", "aA", "", false),
            (@"^(a)\1$", "aA", "i", true), (@"^(?<x>a)\k<x>$", "aA", "i", true),
            (@"^..(?<=\1(a))$", "aa", "", true), (@"^..(?<=\1(a))$", "Aa", "", false),
            (@"^..(?<=\1(a))$", "Aa", "i", true), (@"^..(?<=\k<x>(?<x>a))$", "Aa", "i", true),
            (@"^(Ā)\1$", "ĀĀ", "", true), (@"^..(?<=\1(Ā))$", "ĀĀ", "", true),
            (@"^(😀)\1$", "😀😀", "u", true), (@"^..(?<=\1(😀))$", "😀😀", "u", true),
            (@"^(𐐀)\1$", "𐐀𐐨", "iu", true), (@"^..(?<=\1(𐐀))$", "𐐀𐐨", "iu", true),
            (@"^(a)?\1b$", "b", "", true),
            (@"^(?=(a+))(?=(\1))\2$", "aaaa", "", true),
            (@"^(?!(a+)b)(a+)\2$", "aaaa", "", true),
            (@"^(?!(a+)b)(a+)\2$", "aaaab", "", false),
            (@"^(?=(a|ab))\1c$", "abc", "", false),
            (@"^(?=(ab|a))\1c$", "abc", "", true)
        })
        {
            var result = await engine.InvokeAsync(new { pattern, text, flags });
            Check(result.GetProperty("matched").GetBoolean() == matched && !result.GetProperty("caught").GetBoolean());
        }
    }

    private static class RawNative
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int Interrupt(nint opaque);
        [DllImport("meowfilm_js", EntryPoint = "mf_js_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Create(byte[] source, nuint length, nuint memoryLimit,
            nuint stackLimit, Interrupt interrupt, nint opaque);
        [DllImport("meowfilm_js", EntryPoint = "mf_js_invoke", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Invoke(nint engine, byte[] input, nuint length, out nint output, out nuint outputLength);
        [DllImport("meowfilm_js", EntryPoint = "mf_js_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(nint output);
        [DllImport("meowfilm_js", EntryPoint = "mf_js_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(nint engine);
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Native JavaScript assertion failed");
    }
}
