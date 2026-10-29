using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace MeowFilm.Desktop;

/// <summary>
/// Windows UI boundary. Publish collections and view changes on the UI dispatcher.
/// Views remain capability-owned; cancellation must fence late business/view mutations.
/// The App shutdown callback owns disposal, not ShellWindow.
/// </summary>
public interface IDesktopModule : INotifyPropertyChanged, IDesktopShutdownParticipant
{
    string Id { get; }
    string Title { get; }
    string Version { get; }
    ObservableCollection<NavigationItem> Navigation { get; }
    FrameworkElement View { get; }
    FrameworkElement SettingsView { get; }
    FrameworkElement? PlayerView { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task NavigateAsync(string route, CancellationToken cancellationToken);
    Task SearchAsync(string query, CancellationToken cancellationToken);
    // Only fixed, user-facing business errors; never return the raw exception message.
    string OperationErrorText(Exception error) => "暂时无法完成操作";
}
