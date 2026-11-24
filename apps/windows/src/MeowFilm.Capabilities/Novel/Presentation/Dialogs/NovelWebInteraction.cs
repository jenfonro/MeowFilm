using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace MeowFilm.Capabilities;

internal sealed record NovelWebCookie(string Name, string Value, string Domain, bool HostOnly, string Path,
    bool Secure, bool HttpOnly, string? SameSite, double? Expires);
internal sealed record NovelWebCookieChange(string Url, string SetCookie);
internal sealed record NovelWebRequest(string Mode, string Url, string Html, string Script, string SourceRegex,
    string OverrideRegex, int Delay, string Method, string Body, IReadOnlyDictionary<string, string> Headers,
    NovelWebCookie[] Cookies)
{
    private static readonly HashSet<string> SupportedModes = new(StringComparer.Ordinal)
    {
        "request", "login", "browser", "openurl", "verificationcode", "webview", "rule",
        "webviewajax", "webviewgetsource", "webviewgetoverrideurl", "startbrowser",
        "startbrowserwithshouldoverrideurlloading", "startbrowserdp", "startbrowserdpwithhtml",
        "startbrowserdpwithshouldoverrideurlloading", "getverificationcode"
    };

    internal bool Automatic => Mode is "request" or "rule" or "webview" or "webviewajax" or "webviewgetsource" or "webviewgetoverrideurl";
    internal bool Verification => Mode is "verificationcode" or "getverificationcode";

    internal static NovelWebRequest Parse(JsonElement pending)
    {
        string mode = NovelJson.Text(pending, "mode").ToLowerInvariant();
        if (!SupportedModes.Contains(mode)) throw new InvalidDataException("WEB_REQUEST_UNSUPPORTED");
        var input = pending.GetProperty("request");
        string Field(string name) => NovelJson.Text(input, name);
        var args = input.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToArray() : [];
        string Arg(int index) => index < args.Length && args[index].ValueKind == JsonValueKind.String ? args[index].GetString()! : "";
        int? ArgInt(int index) => index < args.Length && args[index].ValueKind == JsonValueKind.Number &&
            args[index].TryGetDouble(out double value) && double.IsFinite(value) ? (int)Math.Min(int.MaxValue, Math.Max(0, value)) : null;
        string url = Field("url"), html = Field("html"), script = Field("script"), source = Field("sourceRegex"), over = Field("overrideUrlRegex");
        double explicitDelay = 0;
        bool hasExplicitDelay = input.TryGetProperty("delayTime", out var d) && d.ValueKind == JsonValueKind.Number &&
            d.TryGetDouble(out explicitDelay) && double.IsFinite(explicitDelay);
        int delay = hasExplicitDelay ? (int)Math.Min(int.MaxValue, Math.Max(0, explicitDelay)) : 0;
        if (args.Length > 0)
        {
            bool htmlFirst = mode.StartsWith("webview", StringComparison.Ordinal) &&
                args[0].ValueKind == JsonValueKind.String && !Allowed(Arg(0));
            if (url.Length == 0) url = Arg(htmlFirst ? 1 : 0);
            if (htmlFirst)
            {
                if (html.Length == 0) html = Arg(0);
                if (script.Length == 0) script = Arg(2);
                if (mode == "webviewgetsource" && source.Length == 0) source = Arg(3);
                else if (mode == "webviewgetoverrideurl" && over.Length == 0) over = Arg(3);
                if (!hasExplicitDelay && ArgInt(mode == "webview" ? 3 : 4) is { } argumentDelay)
                    delay = argumentDelay;
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
        string body = input.TryGetProperty("body", out var b) && b.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? b.ValueKind == JsonValueKind.String ? b.GetString()! : b.GetRawText()
            : "";
        return new(mode, url, html, script, source, over, delay, method.Length == 0 ? "GET" : method, body, headers, cookies);
    }
    internal static bool Allowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var target) && target.Scheme is "http" or "https";
    internal static bool SameUri(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) || !Uri.TryCreate(right, UriKind.Absolute, out var b))
            return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
            a.Port == b.Port &&
            string.Equals(a.PathAndQuery, b.PathAndQuery, StringComparison.Ordinal);
    }
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
    internal static async Task<object?> ShowAsync(FrameworkElement owner, NovelRuleProgram rules, JsonElement pending,
        CancellationToken token, string? tempRoot = null)
    {
        var input = NovelWebRequest.Parse(pending);
        if (input.Verification && input.Url.Length == 0)
        {
            NovelWebInteractionView? codeDialog = null;
            codeDialog = new NovelWebInteractionView(
                title: "验证码",
                submitLabel: "确认",
                showWeb: false,
                showCode: true,
                autoSubmit: false,
                onSubmit: (_, _) => Task.FromResult(codeDialog!.CodeText.Length > 0),
                token: token);
            if (!await codeDialog.PresentAsync(owner, token)) throw new OperationCanceledException(token);
            return codeDialog.CodeText;
        }
        if (input.Url.Length == 0) throw new InvalidDataException("WEB_REQUEST_UNSUPPORTED");

        var navigationGate = new object();
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool navigationSucceeded = false;
        bool navigationFailed = false;
        var selectedUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        string profile = Path.Combine(tempRoot, "novel-web", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using var retired = CancellationTokenSource.CreateLinkedTokenSource(token);
        CoreWebView2Environment? environment = null;
        var browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool browserCreated = false;
        var requests = new HashSet<Task>();
        var knownNoneSameSite = new HashSet<(string Name, string Domain, string Path)>();
        foreach (var seed in input.Cookies)
        {
            if (string.Equals(seed.SameSite, "None", StringComparison.OrdinalIgnoreCase))
                knownNoneSameSite.Add((seed.Name, (seed.HostOnly ? "" : ".") + seed.Domain, seed.Path));
        }
        BrowserCookie[] baseline = [];
        object? output = null;
        int statusCode = 200;

        bool isCodeMode = input.Verification;
        NovelWebInteractionView? dialog = null;
        Task initialization = null!;
        dialog = new NovelWebInteractionView(
            title: input.Mode == "login" ? "书源登录" : "书源验证",
            submitLabel: "完成",
            showWeb: true,
            showCode: isCodeMode,
            autoSubmit: input.Automatic,
            onSubmit: async (explicitSubmit, ct) =>
            {
                var web = dialog!.WebView;
                await initialization.WaitAsync(ct);
                if (!selectedUrl.Task.IsCompletedSuccessfully)
                {
                    Task waitNav;
                    lock (navigationGate)
                    {
                        if (!navigationSucceeded && navigationFailed)
                            throw new InvalidDataException("WEB_NAVIGATION_FAILED");
                        waitNav = navigation.Task;
                    }
                    await waitNav.WaitAsync(ct);
                }
                if (!explicitSubmit && !selectedUrl.Task.IsCompletedSuccessfully && input.Delay > 0)
                    await Task.Delay(input.Delay, ct);
                object? value;
                if (isCodeMode)
                {
                    if (dialog.CodeText.Length == 0) return false;
                    value = dialog.CodeText;
                }
                else if (input.Mode is "login" or "openurl")
                {
                    value = true;
                }
                else
                {
                    JsonElement? scripted = null;
                    if (!selectedUrl.Task.IsCompletedSuccessfully && input.Script.Length > 0)
                        scripted = await ExecuteScriptAsync(web.CoreWebView2, input.Script, ct);
                    bool matchedUrl = selectedUrl.Task.IsCompletedSuccessfully;
                    if (input.SourceRegex.Length > 0 || input.OverrideRegex.Length > 0)
                    {
                        if (matchedUrl) value = selectedUrl.Task.Result;
                        else if (selectedUrl.Task.IsFaulted) value = await selectedUrl.Task;
                        else if (!explicitSubmit) value = await selectedUrl.Task.WaitAsync(ct);
                        else if (scripted is { } fallbackScalar) value = fallbackScalar;
                        else value = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML").AsTask(ct)) ?? "";
                    }
                    else if (scripted is { } scalar) value = scalar;
                    else value = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML").AsTask(ct)) ?? "";
                    // Legado's browser helper builds StrResponse from this response value.
                    // QYSG scalar modes keep their existing result and positional arguments.
                    if (input.Mode is "request" or "browser" && !matchedUrl)
                        value = new { url = web.Source?.AbsoluteUri ?? input.Url, statusCode, data = value };
                }
                string finalUrl = selectedUrl.Task.IsCompletedSuccessfully && input.OverrideRegex.Length > 0
                    ? selectedUrl.Task.Result
                    : web.Source?.AbsoluteUri ?? input.Url;
                var baselineNone = baseline.Where(c => c.SameSite == "None").Select(c => c.Identity).ToHashSet();
                var final = (await web.CoreWebView2.CookieManager.GetCookiesAsync(null).AsTask(ct))
                    .Select(c => BrowserCookie.From(c, baselineNone)).ToArray();
                ct.ThrowIfCancellationRequested();
                output = NovelWebRequest.Result(value, finalUrl, CookieChanges(baseline, final));
                return true;
            },
            token: token);

        var web = dialog.WebView;
        initialization = Initialize();
        try
        {
            bool accepted = await dialog.PresentAsync(owner, token);
            if (!accepted) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
            return output;
        }
        finally
        {
            retired.Cancel();
            lock (navigationGate) { if (navigation.Task.IsFaulted) _ = navigation.Task.Exception; }
            if (selectedUrl.Task.IsFaulted) _ = selectedUrl.Task.Exception;
            try
            {
                try { await initialization; }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    DebugLog.Warn(DebugCategories.Novel, "web.ui", "init.shutdown_error", new { mode = input.Mode }, error: error);
                }
                await Task.WhenAll(requests.ToArray());
            }
            finally
            {
                try
                {
                    bool waitForBrowser;
                    try { waitForBrowser = browserCreated || environment is not null && environment.GetProcessInfos().Count > 0; }
                    finally { web.Close(); }
                    // The SDK event acknowledges the whole process group and release
                    // of the user data folder, not merely the main browser PID.
                    if (waitForBrowser) await browserExited.Task;
                }
                finally
                {
                    if (environment is not null) environment.BrowserProcessExited -= BrowserExited;
                }
                try
                {
                    if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        void BrowserExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e) => browserExited.TrySetResult();
        async Task Initialize()
        {
            environment = await CoreWebView2Environment.CreateWithOptionsAsync("", profile, new CoreWebView2EnvironmentOptions());
            environment.BrowserProcessExited += BrowserExited;
            await web.EnsureCoreWebView2Async(environment);
            browserCreated = true; var core = web.CoreWebView2;
            retired.Token.ThrowIfCancellationRequested();
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (NovelWebRequest.Allowed(e.Uri)) core.Navigate(e.Uri); };
            core.NavigationStarting += (sender, e) =>
            {
                if (!NovelWebRequest.Allowed(e.Uri)) { e.Cancel = true; return; }
                lock (navigationGate)
                {
                    if (!navigationSucceeded && navigationFailed)
                    {
                        navigationFailed = false;
                        if (navigation.Task.IsFaulted) _ = navigation.Task.Exception;
                        navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationCompleted += (sender, e) =>
            {
                if (e.HttpStatusCode > 0) statusCode = e.HttpStatusCode;
                lock (navigationGate)
                {
                    if (e.IsSuccess)
                    {
                        navigationSucceeded = true;
                        navigationFailed = false;
                        if (navigation.Task.IsFaulted)
                        {
                            _ = navigation.Task.Exception;
                            navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                        navigation.TrySetResult();
                    }
                    else if (!navigationSucceeded)
                    {
                        navigationFailed = true;
                        if (input.Automatic)
                            navigation.TrySetException(new InvalidDataException("WEB_NAVIGATION_FAILED"));
                    }
                }
            };
            bool supplied = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (retired.IsCancellationRequested) return;
                var deferral = e.GetDeferral();
                Task operation = null!;
                operation = Handle();
                if (!operation.IsCompleted) requests.Add(operation);
                async Task Handle()
                {
                    try
                    {
                        if (!NovelWebRequest.Allowed(e.Request.Uri)) return;
                        if (!supplied && input.Html.Length > 0 && NovelWebRequest.SameUri(e.Request.Uri, input.Url))
                        {
                            supplied = true;
                            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(input.Html)).AsRandomAccessStream(), 200, "OK", "Content-Type: text/html; charset=utf-8");
                            return;
                        }
                        if (input.OverrideRegex.Length > 0 && e.ResourceContext == CoreWebView2WebResourceContext.Document && await Match(input.OverrideRegex, e.Request.Uri))
                        {
                            selectedUrl.TrySetResult(e.Request.Uri);
                            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream().AsRandomAccessStream(), 200, "OK", "Content-Type: text/html");
                            if (!input.Automatic) dialog!.RequestAutoSubmit();
                            return;
                        }
                        if (input.SourceRegex.Length > 0 && await Match(input.SourceRegex, e.Request.Uri)) selectedUrl.TrySetResult(e.Request.Uri);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        DebugLog.Warn(DebugCategories.Novel, "web.ui", "filter.error", new { mode = input.Mode, uri = e.Request.Uri }, error: error);
                        selectedUrl.TrySetException(new InvalidDataException("WEB_FILTER_FAILED", error));
                    }
                    finally
                    {
                        deferral.Complete();
                        if (operation is not null) requests.Remove(operation);
                    }
                }
            };
            foreach (var seed in input.Cookies)
            {
                // Only domain cookies have a leading dot. Never reconstruct the scope
                // from a Cookie header or the current navigation URL.
                var cookie = core.CookieManager.CreateCookie(seed.Name, seed.Value,
                    (seed.HostOnly ? "" : ".") + seed.Domain, seed.Path);
                cookie.IsSecure = seed.Secure; cookie.IsHttpOnly = seed.HttpOnly;
                if (seed.SameSite is { } sameSite && Enum.TryParse<CoreWebView2CookieSameSiteKind>(sameSite, ignoreCase: true, out var parsedSameSite))
                    cookie.SameSite = parsedSameSite;
                if (seed.Expires is { } expiry) cookie.Expires = expiry / 1000d;
                core.CookieManager.AddOrUpdateCookie(cookie);
            }
            baseline = (await core.CookieManager.GetCookiesAsync(null)).Select(c => BrowserCookie.From(c, knownNoneSameSite)).ToArray();
            retired.Token.ThrowIfCancellationRequested();
            string headers = string.Join("\r\n", input.Headers.Select(h => h.Key + ": " + h.Value));
            var content = input.Body.Length == 0 ? null : new MemoryStream(Encoding.UTF8.GetBytes(input.Body)).AsRandomAccessStream();
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
                """).AsTask(token);
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
        bool HttpOnly, string? SameSite, DateTime? Expires)
    {
        internal (string, string, string) Identity => (Name, Domain, Path);
        internal static BrowserCookie From(CoreWebView2Cookie cookie, IReadOnlySet<(string, string, string)> knownNone)
        {
            var identity = (cookie.Name, cookie.Domain, cookie.Path);
            string? sameSite = cookie.SameSite switch
            {
                CoreWebView2CookieSameSiteKind.Lax => "Lax",
                CoreWebView2CookieSameSiteKind.Strict => "Strict",
                CoreWebView2CookieSameSiteKind.None => knownNone.Contains(identity) ? "None" : null,
                _ => null
            };
            return new(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly, sameSite,
                cookie.IsSession ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(cookie.Expires * 1000d)).UtcDateTime);
        }
        internal NovelWebCookieChange Change(bool deleted = false)
        {
            bool domainCookie = Domain.StartsWith('.') && !Name.StartsWith("__Host-", StringComparison.Ordinal);
            string host = Domain.TrimStart('.');
            string url = new UriBuilder(Secure ? "https" : "http", host) { Path = Path }.Uri.AbsoluteUri;
            string value = Name + "=" + (deleted ? "" : Value) + "; Path=" + Path +
                (domainCookie ? "; Domain=" + host : "") + (Secure ? "; Secure" : "") +
                (HttpOnly ? "; HttpOnly" : "") + (SameSite is not null ? "; SameSite=" + SameSite : "") +
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
