using System.Windows;

namespace MeowFilm.Capabilities;

public enum SettingsLayoutMode { None, Sections, Pair }

public static class SettingsLayout
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(SettingsLayoutMode), typeof(SettingsLayout),
        new PropertyMetadata(SettingsLayoutMode.None, ModeChanged));
    public static readonly DependencyProperty IsNarrowProperty = DependencyProperty.RegisterAttached(
        "IsNarrow", typeof(bool), typeof(SettingsLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.RegisterAttached(
        "IsStacked", typeof(bool), typeof(SettingsLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static SettingsLayoutMode GetMode(DependencyObject element) => (SettingsLayoutMode)element.GetValue(ModeProperty);
    public static void SetMode(DependencyObject element, SettingsLayoutMode value) => element.SetValue(ModeProperty, value);
    public static bool GetIsNarrow(DependencyObject element) => (bool)element.GetValue(IsNarrowProperty);
    public static void SetIsNarrow(DependencyObject element, bool value) => element.SetValue(IsNarrowProperty, value);
    public static bool GetIsStacked(DependencyObject element) => (bool)element.GetValue(IsStackedProperty);
    public static void SetIsStacked(DependencyObject element, bool value) => element.SetValue(IsStackedProperty, value);

    private static void ModeChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not FrameworkElement element) return;
        element.Loaded -= Loaded;
        element.Unloaded -= Unloaded;
        element.SizeChanged -= SizeChanged;
        switch ((SettingsLayoutMode)e.OldValue)
        {
            case SettingsLayoutMode.Sections:
                element.ClearValue(IsNarrowProperty);
                break;
            case SettingsLayoutMode.Pair:
                element.ClearValue(IsStackedProperty);
                break;
        }
        if ((SettingsLayoutMode)e.NewValue == SettingsLayoutMode.None) return;
        element.Loaded += Loaded;
        element.Unloaded += Unloaded;
        // Initial layout also supports an unmounted component. Unloaded retires
        // resize observation; Loaded restores it using the current geometry.
        element.SizeChanged += SizeChanged;
        Update(element, element.ActualWidth);
    }

    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.SizeChanged -= SizeChanged;
        element.SizeChanged += SizeChanged;
        Update(element, element.ActualWidth);
    }

    private static void Unloaded(object sender, RoutedEventArgs e) =>
        ((FrameworkElement)sender).SizeChanged -= SizeChanged;

    private static void SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) Update((FrameworkElement)sender, e.NewSize.Width);
    }

    private static void Update(FrameworkElement element, double width)
    {
        switch (GetMode(element))
        {
            case SettingsLayoutMode.Sections:
                SetIsNarrow(element, width < 640);
                break;
            case SettingsLayoutMode.Pair:
                SetIsStacked(element, width < 544);
                break;
        }
    }
}
