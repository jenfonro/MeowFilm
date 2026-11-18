using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Desktop;

public partial class CloseWindowDialog : UserControl
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
        CancelChoice.Focus(FocusState.Programmatic);
    }

    internal void SetPending(bool pending)
    {
        IsPending = pending;
        RememberChoice.IsEnabled = DismissButton.IsEnabled = !pending;
        foreach (var choice in Choices.Children.OfType<Control>()) choice.IsEnabled = !pending;
    }

    private void OnChoiceClick(object sender, RoutedEventArgs e)
    {
        if (!IsPending && sender is Button { Tag: string choice } && Enum.TryParse<WindowCloseBehavior>(choice, out var behavior))
            ChoiceRequested?.Invoke(behavior);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => TryCancel();

    internal bool TryCancel()
    {
        if (!IsOpen || IsPending) return false;
        CancelRequested?.Invoke();
        return true;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || DesktopVisual.Modifiers != VirtualKeyModifiers.None) return;
        e.Handled = true;
        TryCancel();
    }
}
