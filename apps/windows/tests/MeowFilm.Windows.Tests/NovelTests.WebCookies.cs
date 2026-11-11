using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private static async Task WebCookieScopeOwner()
    {
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT") ?? throw new InvalidOperationException("Runtime bundles required");
        RuntimeModule[] modules = RuntimeHttpClientTests.Modules;
        var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate", modules);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var runtime = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, modules,
            TestWorkspace.DataPath("novel-web-cookie-scope"), deadline.Token);
        var sources = new NovelSourceService(runtime);
        await using var rules = new NovelRuleProgram(runtime);
        var owner = new StackPanel(); var host = GeneralAppearanceTests.Window(owner); NovelUi.Theme(host); host.Show();
        try
        {
            foreach (bool domainBefore in new[] { false, true })
            {
                string direction = domainBefore ? "domain-to-host" : "host-to-domain";
                const string page = "https://a.example.invalid/private/page";
                const string child = "https://child.a.example.invalid/private/page";
                var definition = new
                {
                    bookSourceName = direction, bookSourceUrl = "https://a.example.invalid/" + direction,
                    ruleContent = new { content = "article@text" },
                    coverDecodeJs = "cookie.setCookie('" + page + "', 'scope=old; Path=/private; Secure; HttpOnly; SameSite=Lax" +
                        (domainBefore ? "; Domain=a.example.invalid" : "") + "'); " +
                        "java.webView(\"<html><head><link rel='icon' href='data:,'></head><body>scope fixture</body></html>\", '" + page +
                        "', 'new Promise(resolve => { window.finishFixture = () => resolve(true); })', 0); " +
                        "JSON.stringify([cookie.getCookie('" + page + "'), cookie.getCookie('" + child + "')])"
                };
                var imported = await sources.ImportAsync(text: Json(definition).GetRawText(), token: deadline.Token);
                Check(imported.GetProperty("failures").GetArrayLength() == 0);
                ExceptionDispatchInfo? interactionFailure = null;
                sources.InteractAsync = async (pending, ct) =>
                {
                    using var interaction = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var operation = NovelWebInteraction.ShowAsync(owner, rules, pending, interaction.Token);
                    try
                    {
                        await Until(() => PopupItems<WebView2>().Any(w => w.CoreWebView2 is not null));
                        var web = PopupItems<WebView2>().Single();
                        while (await web.CoreWebView2.ExecuteScriptAsync("typeof window.finishFixture").WaitAsync(ct) != "\"function\"")
                            await Task.Delay(10, ct);
                        var manager = web.CoreWebView2.CookieManager;
                        var original = (await manager.GetCookiesAsync(null)).Single();
                        Check(original.Name == "scope");
                        Check(original.Domain == (domainBefore ? ".a.example.invalid" : "a.example.invalid") &&
                            original.Path == "/private" && original.IsSecure && original.IsHttpOnly);
                        // This isolated fixture contains only the seed. Await the
                        // browser's deletion receipt before creating its replacement.
                        await web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies).WaitAsync(ct);
                        Check(!(await manager.GetCookiesAsync(page)).Any(c => c.Name == "scope"));
                        var replacement = manager.CreateCookie("scope", "new",
                            domainBefore ? "a.example.invalid" : ".a.example.invalid", "/private");
                        replacement.IsSecure = true; replacement.IsHttpOnly = true; replacement.SameSite = CoreWebView2CookieSameSiteKind.Lax;
                        manager.AddOrUpdateCookie(replacement);
                        Check((await manager.GetCookiesAsync(page)).Single(c => c.Name == "scope").Value == "new");
                        await web.CoreWebView2.ExecuteScriptAsync("window.finishFixture()");
                        return await operation;
                    }
                    catch (Exception error)
                    {
                        interactionFailure = ExceptionDispatchInfo.Capture(error);
                        throw;
                    }
                    finally
                    {
                        interaction.Cancel();
                        try { await operation; } catch (OperationCanceledException) { }
                    }
                };
                JsonElement result;
                try
                {
                    result = await sources.InvokeAsync(sources.Sources.Single(s => s.Name == direction),
                        "image", new { body = "" }, deadline.Token);
                }
                catch when (interactionFailure is not null)
                {
                    // The production broker intentionally reports native cancellation;
                    // preserve the test fixture's original failure and stack instead.
                    interactionFailure.Throw(); throw;
                }
                using var visible = JsonDocument.Parse(result.GetProperty("body").GetString()!);
                Check(visible.RootElement[0].GetString() == "scope=new", direction + ": owner lost the replacement Cookie");
                Check(visible.RootElement[1].GetString() == (domainBefore ? "" : "scope=new"),
                    direction + ": owner retained the wrong host-only scope");
            }
        }
        finally { await sources.PrepareShutdownAsync(); host.Close(); }
    }
    private static async Task WebCookies()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var rules = new NovelRuleProgram(ruleRuntime);
        var owner = new StackPanel(); var host = GeneralAppearanceTests.Window(owner); NovelUi.Theme(host); host.Show();
        await using var server = new WebFixture();
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = BridgeCodec.Value((await NovelWebInteraction.ShowAsync(owner, rules, Json(new { mode = "rule", request = new
            {
                url = server.BaseUrl + "/redirect", headers = new { cOoKiE = "mustNotSeed=ignored" },
                cookies = new[]
                {
                    new NovelWebCookie("private", "unchanged", "127.0.0.1", true, "/private", false, true, "Strict", null),
                    new NovelWebCookie("hop", "old-a", "127.0.0.1", true, "/", false, false, "Lax", null),
                    new NovelWebCookie("other", "old-b", "localhost", true, "/", false, false, "Lax", null),
                    new NovelWebCookie("deleted", "remove", "localhost", true, "/", false, false, "Lax", null)
                },
                script = "document.cookie='deleted=; Max-Age=0; Path=/'; document.cookie"
            } }), token.Token))!);
            Check(result.GetProperty("url").GetString()!.Contains("localhost"));
            Check(result.GetProperty("value").GetString()!.Contains("other=updated-b") &&
                !result.GetProperty("value").GetString()!.Contains("mustNotSeed"));
            var changes = result.GetProperty("cookieChanges").EnumerateArray().ToArray();
            Check(changes.Length == 3 && result.GetProperty("headers").GetProperty("set-cookie").GetArrayLength() == 0);
            var hop = changes.Single(c => c.GetProperty("setCookie").GetString()!.StartsWith("hop="));
            var other = changes.Single(c => c.GetProperty("setCookie").GetString()!.StartsWith("other="));
            var deleted = changes.Single(c => c.GetProperty("setCookie").GetString()!.StartsWith("deleted="));
            Check(new Uri(hop.GetProperty("url").GetString()!).Host == "127.0.0.1" && hop.GetProperty("setCookie").GetString()!.Contains("updated-a"));
            Check(new Uri(other.GetProperty("url").GetString()!).Host == "localhost" && other.GetProperty("setCookie").GetString()!.Contains("updated-b"));
            Check(deleted.GetProperty("setCookie").GetString()!.Contains("Max-Age=0") && new Uri(deleted.GetProperty("url").GetString()!).Host == "localhost");
            Check(changes.All(c => !c.GetProperty("setCookie").GetString()!.Contains("Domain=")));
            Check(server.Requests.Where(r => r.Path == "/redirect" || r.Path == "/other").All(r => !r.Cookie.Contains("private=") && !r.Cookie.Contains("mustNotSeed=")));
            Check(server.Requests.Single(r => r.Path == "/other").Cookie.Contains("other=old-b") &&
                !server.Requests.Single(r => r.Path == "/other").Cookie.Contains("hop="));
        }
        finally { host.Close(); }
    }
    private static async Task WebScalars()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var rules = new NovelRuleProgram(ruleRuntime);
        var owner = new StackPanel(); var host = GeneralAppearanceTests.Window(owner); NovelUi.Theme(host); host.Show();
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
            string expiresText = expires.UtcDateTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            foreach (string scalar in new[] { "false", "null", "7" })
            {
                // The common envelope preserves scalars even for a QYSG mode; it
                // does not imply Legado's response/StrResponse shape or extra args.
                var operation = NovelWebInteraction.ShowAsync(owner, rules, Json(new { mode = scalar == "false" ? "webviewajax" : "rule", request = new
                {
                    url = "https://a.example.invalid/private/page",
                    html = "<html><head><link rel='icon' href='data:,'></head><body>cookie fixture</body></html>",
                    cookies = new[]
                    {
                        new NovelWebCookie("__Host-native", "old", "a.example.invalid", true, "/", true, false, "Lax", null),
                        new NovelWebCookie("domain", "old", "example.invalid", false, "/private", true, false, "Strict", expires.ToUnixTimeMilliseconds()),
                        new NovelWebCookie("untouched", "private", "a.example.invalid", true, "/private", true, true, "Lax", null)
                    },
                    script = "new Promise(resolve => { window.finishFixture = () => {" +
                        "document.cookie='__Host-native=new; Secure; Path=/; SameSite=Lax'; " +
                        "document.cookie='domain=new; Domain=example.invalid; Path=/private; Secure; SameSite=Strict; Expires=" + expiresText + "'; " +
                        "resolve(" + scalar + "); }; })"
                } }), token.Token);
                await Until(() => PopupItems<Microsoft.Web.WebView2.Wpf.WebView2>().Any(w => w.CoreWebView2 is not null));
                var web = PopupItems<Microsoft.Web.WebView2.Wpf.WebView2>().Single();
                while (await web.CoreWebView2.ExecuteScriptAsync("typeof window.finishFixture").WaitAsync(token.Token) != "\"function\"")
                    await Task.Delay(10, token.Token);
                var seeded = await web.CoreWebView2.CookieManager.GetCookiesAsync(null);
                Check(seeded.Single(c => c.Name == "domain").Domain == ".example.invalid");
                var untouched = seeded.Single(c => c.Name == "untouched");
                Check(untouched.Domain == "a.example.invalid" && untouched.Path == "/private" && untouched.IsSecure && untouched.IsHttpOnly);
                Check(seeded.Single(c => c.Name == "__Host-native").Domain == "a.example.invalid");
                Check(!seeded.Single(c => c.Name == "domain").IsSession &&
                    Math.Abs((seeded.Single(c => c.Name == "domain").Expires.ToUniversalTime() - expires.UtcDateTime).TotalSeconds) < 1);
                await web.CoreWebView2.ExecuteScriptAsync("window.finishFixture()");
                var result = BridgeCodec.Value((await operation)!);
                using var expected = JsonDocument.Parse(scalar); Check(JsonElement.DeepEquals(result.GetProperty("value"), expected.RootElement));
                var changes = result.GetProperty("cookieChanges").EnumerateArray().ToArray();
                Check(changes.Length == 2);
                var hostChange = changes.Single(c => c.GetProperty("setCookie").GetString()!.StartsWith("__Host-native="));
                string setCookie = hostChange.GetProperty("setCookie").GetString()!;
                Check(setCookie.StartsWith("__Host-native=new;") && setCookie.Contains("; Secure") && setCookie.Contains("; Path=/") && !setCookie.Contains("Domain="));
                Check(hostChange.GetProperty("url").GetString()!.StartsWith("https://a.example.invalid/"));
                string domain = changes.Single(c => c.GetProperty("setCookie").GetString()!.StartsWith("domain=")).GetProperty("setCookie").GetString()!;
                Check(domain.Contains("Domain=example.invalid") && domain.Contains("SameSite=Strict") && domain.Contains("Expires=") && domain.Contains("Path=/private"));
            }
            // Positional bridge arrays keep null/empty slots and the explicit delay.
            var parsed = NovelWebRequest.Parse(Json(new { mode = "webViewGetSource", request = new { arguments = new object?[]
                { "<body>source</body>", "https://a.example.invalid/", null, "", 37 } } }));
            Check(parsed.Script == "" && parsed.SourceRegex == "" && parsed.Delay == 37 && parsed.Url == "https://a.example.invalid/");
        }
        finally { host.Close(); }
    }
}
