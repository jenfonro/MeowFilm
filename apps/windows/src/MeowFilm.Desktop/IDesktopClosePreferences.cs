using System.ComponentModel;

namespace MeowFilm.Desktop;

public enum WindowCloseBehavior { Ask, MinimizeToTray, Exit }

public interface IDesktopClosePreferences : INotifyPropertyChanged
{
    WindowCloseBehavior Behavior { get; }
    bool CanEdit { get; }
    string Status { get; }
    Task LoadAsync();
    Task<bool> SaveAsync(WindowCloseBehavior behavior);
}
