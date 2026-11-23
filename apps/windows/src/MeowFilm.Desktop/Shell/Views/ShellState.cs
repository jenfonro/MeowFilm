using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MeowFilm.Desktop;

internal sealed class ShellState : INotifyPropertyChanged
{
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _isError;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _canRetry;
    private bool _isInteractive = true;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool HasStatus => Status.Length != 0;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool CanGoBack { get => _canGoBack; set => Set(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; set => Set(ref _canGoForward, value); }
    public bool CanRetry { get => _canRetry; private set => Set(ref _canRetry, value); }
    public bool IsInteractive
    {
        get => _isInteractive;
        set
        {
            if (_isInteractive == value) return;
            _isInteractive = value;
            PropertyChanged?.Invoke(this, new(nameof(IsInteractive)));
            PropertyChanged?.Invoke(this, new(nameof(CanCancel)));
            PropertyChanged?.Invoke(this, new(nameof(CanDismiss)));
        }
    }
    public bool CanCancel => IsBusy && IsInteractive;
    public bool CanDismiss => HasStatus && !IsBusy && IsInteractive;
    public void SetStatus(string status = "", bool busy = false, bool error = false, bool retry = false)
    {
        Status = status;
        IsBusy = busy;
        IsError = error;
        CanRetry = retry;
        PropertyChanged?.Invoke(this, new(nameof(HasStatus)));
        PropertyChanged?.Invoke(this, new(nameof(CanCancel)));
        PropertyChanged?.Invoke(this, new(nameof(CanDismiss)));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
