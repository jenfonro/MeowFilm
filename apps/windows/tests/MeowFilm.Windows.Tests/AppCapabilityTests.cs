using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using MeowFilm.AppHost;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class AppCapabilityTests
{
    private sealed class Module(Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> methods)
        : IScriptPlatformCapabilities
    {
        public int Reads;
        public IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> ScriptCapabilities
        { get { Reads++; return methods; } }
    }
    private sealed class DesktopModule(string id) : IDesktopModule
    {
        public string Id { get; } = id;
        public string Title => Id;
        public string Version => "test";
        public ObservableCollection<NavigationItem> Navigation { get; } = [];
        public FrameworkElement View => null!;
        public FrameworkElement SettingsView => null!;
        public FrameworkElement? PlayerView => null;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NavigateAsync(string route, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SearchAsync(string query, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PrepareShutdownAsync() => Task.CompletedTask;
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Task<object?> Reply(JsonElement _, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult<object?>(new { ok = true }); }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Capability composition assertion"); }
    private static void Rejected(Action action)
    {
        try { action(); throw new Exception("Invalid capability table was accepted"); }
        catch (InvalidOperationException e) { Check(e.Message == "SCRIPT_CAPABILITY_COLLISION"); }
    }

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("app-capabilities: native UI composition is independent from script module count", () =>
        {
            AppLauncher.ValidateModuleComposition([new DesktopModule("native-a"), new DesktopModule("native-b")]);
            foreach (IReadOnlyList<IDesktopModule> invalid in new IReadOnlyList<IDesktopModule>[]
            {
                [], [new DesktopModule("")], [new DesktopModule("same"), new DesktopModule("same")],
                new IDesktopModule[] { null! }
            })
            {
                try
                {
                    AppLauncher.ValidateModuleComposition(invalid);
                    throw new Exception("Invalid App composition was accepted");
                }
                catch (InvalidOperationException error)
                {
                    Check(error.Message == "APP_COMPOSITION_INVALID");
                }
            }
            return Task.CompletedTask;
        });
        yield return new("app-capabilities: passive frozen table registers only explicit capability methods", async () =>
        {
            int calls = 0;
            var platform = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
                { ["platform.document.read"] = Reply };
            var source = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            {
                ["platform.musicScript.read"] = (_, _) => { calls++; return Task.FromResult<object?>(1); }
            };
            var module = new Module(source);
            var combined = AppLauncher.ComposeScriptCapabilities(platform, [module]);
            Check(module.Reads == 1 && calls == 0 && combined.Count == 2);
            platform.Clear(); source.Clear(); source.Add("platform.injected", Reply);
            Check(combined.Count == 2 && !combined.ContainsKey("platform.injected"));
            Check((int)(await combined["platform.musicScript.read"](default, default))! == 1 && calls == 1);
            Check(combined is IDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> { IsReadOnly: true });
        });
        yield return new("app-capabilities: collisions and malformed privileges fail without invoking callbacks", () =>
        {
            foreach (var name in new[] { "music.library.write", "platform", "platform.bad method", "Platform.capability" })
            {
                var methods = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> { [name] = Reply };
                Rejected(() => AppLauncher.ComposeScriptCapabilities(methods, []));
                Rejected(() => AppLauncher.ComposeScriptCapabilities(
                    new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(), [new Module(methods)]));
            }
            var shared = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> { ["platform.shared"] = Reply };
            Rejected(() => AppLauncher.ComposeScriptCapabilities(shared, [new Module(shared)]));
            Rejected(() => AppLauncher.ComposeScriptCapabilities(
                new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(), [new Module(shared), new Module(shared)]));
            Rejected(() => AppLauncher.ComposeScriptCapabilities(
                new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> { ["platform.null"] = null! }, []));
            return Task.CompletedTask;
        });
        yield return new("app-capabilities: one total method budget includes base platform table", () =>
        {
            var platform = Enumerable.Range(0, 64).ToDictionary(i => "platform.p" + i,
                _ => (Func<JsonElement, CancellationToken, Task<object?>>)Reply, StringComparer.Ordinal);
            Check(AppLauncher.ComposeScriptCapabilities(platform, []).Count == 64);
            Rejected(() => AppLauncher.ComposeScriptCapabilities(platform,
                [new Module(new() { ["platform.extra"] = Reply })]));
            platform["platform.p64"] = Reply;
            Rejected(() => AppLauncher.ComposeScriptCapabilities(platform, []));
            return Task.CompletedTask;
        });
        yield return new("app-capabilities: callback cancellation preserves caller contract", async () =>
        {
            var table = AppLauncher.ComposeScriptCapabilities(
                new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> { ["platform.read"] = Reply }, []);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await table["platform.read"](default, cancelled.Token); throw new Exception("Cancellation lost"); }
            catch (OperationCanceledException) { }
        });
    }
}
