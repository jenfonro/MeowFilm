using System.ComponentModel;
using System.Windows;

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
