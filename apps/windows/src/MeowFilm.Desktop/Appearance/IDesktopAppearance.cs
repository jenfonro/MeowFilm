using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace MeowFilm.Desktop;

/// <summary>App-owned persisted background; the view only edits a draft until confirmation.</summary>
public interface IDesktopAppearance : INotifyPropertyChanged
{
    string ThemeColor { get; }
    bool CanEdit { get; }
    string Status { get; }
    Task LoadAsync();
    Task SaveAsync(string color);
}

/// <summary>Capability-owned controls for the three General settings areas, without activating module settings.</summary>
public interface IDesktopGeneralSettingsSource
{
    FrameworkElement? AppearanceSettingsView => null;
    FrameworkElement? HomeSourceSettingsView => null;
    FrameworkElement? CacheSettingsView => null;
}

/// <summary>App-owned plaintext settings import/export coordinator for General → Data Management.</summary>
public interface IDesktopDataManagement
{
    bool IsBusy { get; }
    Task<bool> ExportAsync(FrameworkElement owner);
    Task<bool> ImportAsync(FrameworkElement owner);
    Task ExportToFileAsync(string path, CancellationToken cancellationToken = default);
    Task ImportFromFileAsync(string path, CancellationToken cancellationToken = default);
}
