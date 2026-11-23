using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Capabilities;

/// <summary>Shared login dialog chrome; the consumer owns methods, requests and account state.</summary>
public sealed class AccountLoginSurface : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AccountLoginSurface), new PropertyMetadata(""));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public event RoutedEventHandler? CloseRequested;
    private Button? close;

    protected override void OnApplyTemplate()
    {
        if (close is not null) close.Click -= Close;
        base.OnApplyTemplate();
        close = GetTemplateChild("PART_Close") as Button;
        if (close is not null) close.Click += Close;
    }
    private void Close(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, e);
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        CloseRequested?.Invoke(this, new());
    }
}
