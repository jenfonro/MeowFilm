using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using MeowFilm.AppHost;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class AppCapabilityTests
{
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
        private string _location = "initial";
        public Task NavigateAsync(string route, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _location = "route:" + route; return Task.CompletedTask;
        }
        public Task SearchAsync(string query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _location = "search:" + query; return Task.CompletedTask;
        }
        public string CaptureNavigationLocation() => _location;
        public Task RestoreNavigationAsync(string location, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _location = location; return Task.CompletedTask;
        }
        public Task PrepareShutdownAsync() => Task.CompletedTask;
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Capability composition assertion"); }

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
    }
}
