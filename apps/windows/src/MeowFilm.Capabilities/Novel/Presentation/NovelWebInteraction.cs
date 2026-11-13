using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MeowFilm.Capabilities;

internal sealed record NovelWebCookie(string Name, string Value, string Domain, bool HostOnly, string Path,
    bool Secure, bool HttpOnly, string? SameSite, double? Expires);
internal sealed record NovelWebCookieChange(string Url, string SetCookie);
internal sealed record NovelWebRequest(string Mode, string Url, string Html, string Script, string SourceRegex,
    string OverrideRegex, int Delay, string Method, string Body, IReadOnlyDictionary<string, string> Headers,
    NovelWebCookie[] Cookies)
{
    internal bool Automatic => !Mode.Contains("browser", StringComparison.OrdinalIgnoreCase) &&
        Mode is not ("login" or "openUrl" or "openurl" or "verificationCode" or "getVerificationCode");
    internal static NovelWebRequest Parse(JsonElement pending)
    {
        string mode = NovelJson.Text(pending, "mode"); var input = pending.GetProperty("request");
        string Field(string name) => NovelJson.Text(input, name);
        var args = input.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToArray() : [];
        string Arg(int index) => index < args.Length && args[index].ValueKind == JsonValueKind.String ? args[index].GetString()! : "";
        string url = Field("url"), html = Field("html"), script = Field("script"), source = Field("sourceRegex"), over = Field("overrideUrlRegex");
        int delay = input.TryGetProperty("delayTime", out var d) && d.TryGetInt32(out int milliseconds) ? Math.Max(0, milliseconds) : 0;
        if (args.Length > 0)
        {
            url = Arg(0);
            if ((mode is "webview" or "webViewGetSource") && !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                html = url; url = Arg(1); script = Arg(2); source = Arg(3);
                if (args.Length > 4 && args[4].ValueKind == JsonValueKind.Number && args[4].TryGetInt32(out int argumentDelay))
                    delay = Math.Max(0, argumentDelay);
            }
        }
        if (url.Length == 0 && html.Length > 0) throw new InvalidDataException("WEB_BASE_URL_REQUIRED");
        if (url.Length > 0 && !Allowed(url)) throw new InvalidDataException("WEB_URL_UNSUPPORTED");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            foreach (var header in h.EnumerateObject())
                if (!header.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) headers[header.Name] = header.Value.ToString();
        var cookies = input.TryGetProperty("cookies", out var c) ? NovelJson.Decode<NovelWebCookie[]>(c) : [];
        string method = Field("method");
        string body = input.TryGetProperty("body", out var b) ? b.ValueKind == JsonValueKind.String ? b.GetString()! : b.GetRawText() : "";
        return new(mode, url, html, script, source, over, delay, method.Length == 0 ? "GET" : method, body, headers, cookies);
    }
    internal static bool Allowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var target) && target.Scheme is "http" or "https";
    internal static object Result(object? value, string url, NovelWebCookieChange[] changes)
    {
        if (!Allowed(url)) throw new InvalidDataException("WEB_URL_UNSUPPORTED");
        return new { value, url, headers = new Dictionary<string, string[]> { ["set-cookie"] = [] },
            cookieChanges = changes.Select(c => new { url = c.Url, setCookie = c.SetCookie }).ToArray() };
    }
}

/// <summary>One ephemeral browser profile per pending source interaction, physically retired before returning.</summary>
internal static class NovelWebInteraction
{
    internal static async Task<object?> ShowAsync(FrameworkElement owner, NovelRuleProgram rules, JsonElement pending, CancellationToken token)
    {
        var input = NovelWebRequest.Parse(pending);
        if ((input.Mode is "verificationCode" or "getVerificationCode") && input.Url.Length == 0)
        {
            var code = NovelUi.Input("验证码");
            if (!await NovelUi.DialogAsync(owner, "验证码", NovelUi.Field("验证码", code), "确认", _ => Task.FromResult(code.Text.Length > 0), token)) throw new OperationCanceledException(token);
            return code.Text;
        }
        if (input.Url.Length == 0) throw new InvalidDataException("WEB_REQUEST_UNSUPPORTED");
        var web = new WebView2(); var panel = new DockPanel();
        var codeInput = NovelUi.Input("验证码");
        if (input.Mode is "verificationCode" or "getVerificationCode")
        {
            var codeField = NovelUi.Field("验证码", codeInput);
            DockPanel.SetDock(codeField, Dock.Bottom); panel.Children.Add(codeField);
        }
        web.MinHeight = 320; panel.Children.Add(web);
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectedUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string profile = Path.Combine(Path.GetTempPath(), "meowfilm", "novel-web", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using var retired = CancellationTokenSource.CreateLinkedTokenSource(token);
        CoreWebView2Environment? environment = null;
        var browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool browserCreated = false;
        var requests = new HashSet<Task>(); BrowserCookie[] baseline = []; object? output = null;
        int statusCode = 0;
        Task initialization = Initialize();
        try
        {
            bool accepted = await NovelUi.DialogAsync(owner, input.Mode == "login" ? "书源登录" : "书源验证", panel, "完成", async ct =>
            {
                await initialization.WaitAsync(ct); await navigation.Task.WaitAsync(ct);
                if (input.Delay > 0) await Task.Delay(input.Delay, ct);
                object? value;
                if (input.Mode is "verificationCode" or "getVerificationCode")
                { if (codeInput.Text.Length == 0) return false; value = codeInput.Text; }
                else
                {
                    JsonElement? scripted = null;
                    if (input.Script.Length > 0) scripted = await ExecuteScriptAsync(web.CoreWebView2, input.Script, ct);
                    if (input.SourceRegex.Length > 0 || input.OverrideRegex.Length > 0)
                        value = await selectedUrl.Task.WaitAsync(ct);
                    else if (scripted is { } scalar) value = scalar;
                    else value = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML").WaitAsync(ct)) ?? "";
                    // Legado's browser helper builds StrResponse from this response value.
                    // QYSG scalar modes keep their existing result and positional arguments.
                    if (input.Mode is "request" or "browser")
                        value = new { url = web.Source?.AbsoluteUri ?? input.Url, statusCode, data = value };
                }
                string finalUrl = web.Source?.AbsoluteUri ?? input.Url;
                var final = (await web.CoreWebView2.CookieManager.GetCookiesAsync(null).WaitAsync(ct)).Select(BrowserCookie.From).ToArray();
                ct.ThrowIfCancellationRequested();
                output = NovelWebRequest.Result(value, finalUrl, CookieChanges(baseline, final)); return true;
            }, token, autoSubmit: input.Automatic);
            if (!accepted) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested(); return output;
        }
        finally
        {
            retired.Cancel();
            try
            {
                try { await initialization; } catch { }
                await Task.WhenAll(requests.ToArray());
            }
            finally
            {
                try
                {
                    bool waitForBrowser;
                    try { waitForBrowser = browserCreated || environment is not null && environment.GetProcessInfos().Count > 0; }
                    finally { web.Dispose(); }
                    // The SDK event acknowledges the whole process group and release
                    // of the user data folder, not merely the main browser PID.
                    if (waitForBrowser) await browserExited.Task;
                }
                finally
                {
                    if (environment is not null) environment.BrowserProcessExited -= BrowserExited;
                }
                Directory.Delete(profile, recursive: true);
            }
        }
        void BrowserExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e) => browserExited.TrySetResult();
        async Task Initialize()
        {
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            environment.BrowserProcessExited += BrowserExited;
            await web.EnsureCoreWebView2Async(environment);
            browserCreated = true; var core = web.CoreWebView2;
            retired.Token.ThrowIfCancellationRequested();
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (NovelWebRequest.Allowed(e.Uri)) core.Navigate(e.Uri); };
            core.NavigationStarting += (_, e) => { if (!NovelWebRequest.Allowed(e.Uri)) e.Cancel = true; };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationCompleted += (_, e) =>
            {
                statusCode = e.HttpStatusCode;
                if (e.IsSuccess) navigation.TrySetResult();
                else navigation.TrySetException(new InvalidDataException("WEB_NAVIGATION_FAILED"));
            };
            bool supplied = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (retired.IsCancellationRequested) return;
                var deferral = e.GetDeferral(); var operation = Handle(); requests.Add(operation);
                async Task Handle()
                {
                    try
                    {
                        if (!NovelWebRequest.Allowed(e.Request.Uri)) return;
                        if (input.OverrideRegex.Length > 0 && e.ResourceContext == CoreWebView2WebResourceContext.Document && await Match(input.OverrideRegex, e.Request.Uri))
                        {
                            selectedUrl.TrySetResult(e.Request.Uri);
                            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 200, "OK", "Content-Type: text/html"); return;
                        }
                        if (input.SourceRegex.Length > 0 && await Match(input.SourceRegex, e.Request.Uri)) selectedUrl.TrySetResult(e.Request.Uri);
                        if (!supplied && input.Html.Length > 0 && e.Request.Uri == input.Url)
                        {
                            supplied = true; e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(input.Html)), 200, "OK", "Content-Type: text/html; charset=utf-8");
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { selectedUrl.TrySetException(new InvalidDataException("WEB_FILTER_FAILED")); }
                    finally { deferral.Complete(); }
                }
            };
            foreach (var seed in input.Cookies)
            {
                // Only domain cookies have a leading dot. Never reconstruct the scope
                // from a Cookie header or the current navigation URL.
                var cookie = core.CookieManager.CreateCookie(seed.Name, seed.Value,
                    (seed.HostOnly ? "" : ".") + seed.Domain, seed.Path);
                cookie.IsSecure = seed.Secure; cookie.IsHttpOnly = seed.HttpOnly;
                if (seed.SameSite is { } sameSite) cookie.SameSite = Enum.Parse<CoreWebView2CookieSameSiteKind>(sameSite);
                if (seed.Expires is { } expiry) cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds((long)expiry).UtcDateTime;
                core.CookieManager.AddOrUpdateCookie(cookie);
            }
            baseline = (await core.CookieManager.GetCookiesAsync(null)).Select(BrowserCookie.From).ToArray();
            retired.Token.ThrowIfCancellationRequested();
            string headers = string.Join("\r\n", input.Headers.Select(h => h.Key + ": " + h.Value));
            var content = input.Body.Length == 0 ? null : new MemoryStream(Encoding.UTF8.GetBytes(input.Body));
            core.NavigateWithWebResourceRequest(core.Environment.CreateWebResourceRequest(input.Url, input.Method, content, headers));
        }
        async Task<bool> Match(string pattern, string url)
        {
            bool matches = await rules.MatchUrlAsync(pattern, url, retired.Token);
            retired.Token.ThrowIfCancellationRequested();
            return matches;
        }
    }
    private static async Task<JsonElement> ExecuteScriptAsync(CoreWebView2 core, string script, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.WebMessageReceived += Message;
        try
        {
            // WebView2's JSON result does not await a JavaScript Promise. A request-scoped
            // message completes only after the supplied dynamic script has resolved.
            await core.ExecuteScriptAsync($$"""
                (async () => {
                  try {
                    const value = await (0, eval)({{JsonSerializer.Serialize(script)}});
                    window.chrome.webview.postMessage({id:{{JsonSerializer.Serialize(id)}},ok:true,value:value === undefined ? null : value});
                  } catch (_) {
                    window.chrome.webview.postMessage({id:{{JsonSerializer.Serialize(id)}},ok:false});
                  }
                })();
                """).WaitAsync(token);
            return await completion.Task.WaitAsync(token);
        }
        finally { core.WebMessageReceived -= Message; }
        void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            JsonDocument message;
            try { message = JsonDocument.Parse(args.WebMessageAsJson); }
            catch (JsonException) { return; }
            using var owned = message;
            var value = message.RootElement;
            if (value.ValueKind != JsonValueKind.Object) return;
            if (NovelJson.Text(value, "id") != id) return;
            if (value.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && value.TryGetProperty("value", out var result))
                completion.TrySetResult(result.Clone());
            else completion.TrySetException(new InvalidDataException("WEB_SCRIPT_FAILED"));
        }
    }
    private sealed record BrowserCookie(string Name, string Value, string Domain, string Path, bool Secure,
        bool HttpOnly, CoreWebView2CookieSameSiteKind SameSite, DateTime? Expires)
    {
        internal (string, string, string) Identity => (Name, Domain, Path);
        internal static BrowserCookie From(CoreWebView2Cookie cookie) => new(cookie.Name, cookie.Value,
            cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly, cookie.SameSite,
            cookie.IsSession ? null : cookie.Expires.ToUniversalTime());
        internal NovelWebCookieChange Change(bool deleted = false)
        {
            bool domainCookie = Domain.StartsWith('.') && !Name.StartsWith("__Host-", StringComparison.Ordinal);
            string host = Domain.TrimStart('.');
            string url = new UriBuilder(Secure ? "https" : "http", host) { Path = Path }.Uri.AbsoluteUri;
            string value = Name + "=" + (deleted ? "" : Value) + "; Path=" + Path +
                (domainCookie ? "; Domain=" + host : "") + (Secure ? "; Secure" : "") +
                (HttpOnly ? "; HttpOnly" : "") + "; SameSite=" + SameSite +
                (deleted ? "; Max-Age=0" : Expires is { } expiry ? "; Expires=" + expiry.ToString("R", CultureInfo.InvariantCulture) : "");
            return new(url, value);
        }
    }
    private static NovelWebCookieChange[] CookieChanges(BrowserCookie[] baseline, BrowserCookie[] final)
    {
        var prior = baseline.ToDictionary(cookie => cookie.Identity);
        var current = final.ToDictionary(cookie => cookie.Identity);
        return baseline.Where(cookie => !current.ContainsKey(cookie.Identity)).Select(cookie => cookie.Change(deleted: true))
            .Concat(final.Where(cookie => !prior.TryGetValue(cookie.Identity, out var original) || cookie != original).Select(cookie => cookie.Change())).ToArray();
    }
}
