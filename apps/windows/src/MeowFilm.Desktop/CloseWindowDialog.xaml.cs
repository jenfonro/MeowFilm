using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeowFilm.Desktop;

internal partial class CloseWindowDialog : UserControl
{
    internal event Action<WindowCloseBehavior>? ChoiceRequested;
    internal event Action? CancelRequested;
    internal bool IsOpen => Visibility == Visibility.Visible;
    internal bool IsPending { get; private set; }
    internal bool ShouldRemember => RememberChoice.IsChecked == true;

    public CloseWindowDialog() => InitializeComponent();

    internal void Open()
    {
        RememberChoice.IsChecked = false;
        SetPending(false);
        Visibility = Visibility.Visible;
        CancelChoice.Focus();
    }

    internal void SetPending(bool pending)
    {
        IsPending = pending;
        RememberChoice.IsEnabled = Choices.IsEnabled = DismissButton.IsEnabled = !pending;
    }

    private void OnChoiceClick(object sender, RoutedEventArgs e)
    {
        if (!IsPending && sender is Button { Tag: WindowCloseBehavior behavior })
            ChoiceRequested?.Invoke(behavior);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (!IsPending) CancelRequested?.Invoke();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (!IsPending) CancelRequested?.Invoke();
    }
}
