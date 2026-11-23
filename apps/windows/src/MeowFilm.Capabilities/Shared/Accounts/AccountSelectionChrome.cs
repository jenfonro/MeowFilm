using System.Runtime.CompilerServices;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

/// <summary>Projects a login mode's visibility tag; it does not select or change an account method.</summary>
public static class AccountSelectionChrome
{
    public static readonly DependencyProperty ObserveTagProperty = DependencyProperty.RegisterAttached("ObserveTag", typeof(bool), typeof(AccountSelectionChrome), new PropertyMetadata(false, Changed));
    private static readonly ConditionalWeakTable<Button, ChromeSubscription> Subscriptions = new();

    public static bool GetObserveTag(DependencyObject value) => (bool)value.GetValue(ObserveTagProperty);
    public static void SetObserveTag(DependencyObject value, bool enabled) => value.SetValue(ObserveTagProperty, enabled);

    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Button button) return;
        if (Subscriptions.TryGetValue(button, out var existing))
        {
            existing.Dispose();
            Subscriptions.Remove(button);
        }
        if ((bool)args.NewValue)
            Subscriptions.Add(button, new ChromeSubscription(button));
        else
            button.ClearValue(Control.BackgroundProperty);
    }

    private sealed class ChromeSubscription : IDisposable
    {
        private readonly Button button;
        private readonly long tagToken;
        private IDisposable? theme;
        private bool disposed;

        internal ChromeSubscription(Button button)
        {
            this.button = button;
            tagToken = button.RegisterPropertyChangedCallback(FrameworkElement.TagProperty, (_, _) => Refresh());
            button.Loaded += OnLoaded;
            button.Unloaded += OnUnloaded;
            if (button.IsLoaded) AttachTheme();
            else Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => AttachTheme();

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (button.IsLoaded) return;
            theme?.Dispose();
            theme = null;
        }

        private void AttachTheme()
        {
            if (disposed) return;
            theme?.Dispose();
            theme = DesktopTheme.Observe(button, Refresh);
        }

        private void Refresh()
        {
            if (disposed) return;
            string key = button.Tag is Visibility.Visible ? "MeowFilm.Brush.Surface" : "MeowFilm.Brush.InputSurface";
            button.Background = (Brush)DesktopVisual.Resource(button, key);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            button.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, tagToken);
            button.Loaded -= OnLoaded;
            button.Unloaded -= OnUnloaded;
            theme?.Dispose();
            theme = null;
        }
    }
}
