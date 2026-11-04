using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MeowFilm.Desktop;

public sealed class LoadingSkeleton : Border
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LoadingSkeleton),
        new PropertyMetadata(true, (value, _) => ((LoadingSkeleton)value).Refresh()));

    private static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        "Tone", typeof(Brush), typeof(LoadingSkeleton),
        new PropertyMetadata(Brushes.Gray, (value, _) => ((LoadingSkeleton)value).RefreshColors()));

    private readonly TranslateTransform sweep = new();
    private readonly LinearGradientBrush gradient = new()
    {
        StartPoint = new(0, .5), EndPoint = new(1, .5), SpreadMethod = GradientSpreadMethod.Reflect
    };
    private bool listening;
    internal bool IsSweeping => sweep.HasAnimatedProperties;

    public LoadingSkeleton()
    {
        IsHitTestVisible = false;
        Focusable = false;
        gradient.GradientStops.Add(new(Colors.Transparent, 0));
        gradient.GradientStops.Add(new(Colors.Transparent, .5));
        gradient.GradientStops.Add(new(Colors.Transparent, 1));
        gradient.RelativeTransform = sweep;
        Background = gradient;
        SetResourceReference(ToneProperty, "MeowFilm.Brush.Text");
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => Refresh();
        RefreshColors();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DesktopTheme.IsHighContrastProperty) Refresh();
    }

    private void RefreshColors()
    {
        if (gradient.GradientStops.Count == 0) return;
        Color tone = (GetValue(ToneProperty) as SolidColorBrush)?.Color ?? Colors.Gray;
        bool contrast = SystemParameters.HighContrast || DesktopTheme.GetIsHighContrast(this);
        if (contrast) tone = SystemColors.WindowTextColor;
        gradient.GradientStops[0].Color = Color.FromArgb(contrast ? (byte)80 : (byte)10, tone.R, tone.G, tone.B);
        gradient.GradientStops[1].Color = Color.FromArgb(contrast ? (byte)80 : (byte)20, tone.R, tone.G, tone.B);
        gradient.GradientStops[2].Color = gradient.GradientStops[0].Color;
    }

    private void Refresh()
    {
        if (!IsLoaded || !IsVisible || !IsActive) { Stop(); return; }
        if (!listening)
        {
            SystemParameters.StaticPropertyChanged += OnSystemChanged;
            listening = true;
        }
        RefreshColors();
        UpdateAnimation(SystemParameters.ClientAreaAnimation,
            SystemParameters.HighContrast || DesktopTheme.GetIsHighContrast(this));
    }

    // Kept separate so system preference transitions can be verified without changing OS settings.
    internal void UpdateAnimation(bool animationsEnabled, bool highContrast)
    {
        bool run = IsLoaded && IsVisible && IsActive && animationsEnabled && !highContrast;
        if (run == IsSweeping) return;
        sweep.BeginAnimation(TranslateTransform.XProperty, run
            ? new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
        {
            if (Dispatcher.CheckAccess()) Refresh();
            else _ = Dispatcher.BeginInvoke(new Action(Refresh));
        }
    }

    private void Stop()
    {
        sweep.BeginAnimation(TranslateTransform.XProperty, null);
        if (!listening) return;
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
        listening = false;
    }
}
