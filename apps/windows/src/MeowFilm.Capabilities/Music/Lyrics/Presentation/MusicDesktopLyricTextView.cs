using MeowFilm.Desktop;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

/// <summary>Desktop single-color lyric/status text rendered via Win2D with solid-backed vector stroke.</summary>
public sealed class MusicDesktopLyricTextView : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(MusicDesktopLyricTextView), new PropertyMetadata("", (owner, _) => ((MusicDesktopLyricTextView)owner).Refresh()));
    public static readonly DependencyProperty MaxLineWidthProperty = DependencyProperty.Register(nameof(MaxLineWidth), typeof(double),
        typeof(MusicDesktopLyricTextView), new PropertyMetadata(double.PositiveInfinity, (owner, _) => ((MusicDesktopLyricTextView)owner).Refresh()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double MaxLineWidth { get => (double)GetValue(MaxLineWidthProperty); set => SetValue(MaxLineWidthProperty, value); }

    private readonly Image image = new() { Stretch = Stretch.None, IsHitTestVisible = false };
    private XamlRoot? xamlRoot;
    private IDisposable? themeObserver;
    private (string Text, double FontSize, double MaxWidth, double Scale, FlowDirection Direction, Windows.UI.Color Fill, Windows.UI.Color Stroke)? lastKey;

    public MusicDesktopLyricTextView()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        Content = image;
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => Refresh());
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Refresh());
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => Refresh());
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (xamlRoot is not null) xamlRoot.Changed -= OnRootChanged;
        xamlRoot = XamlRoot;
        if (xamlRoot is not null) xamlRoot.Changed += OnRootChanged;
        themeObserver?.Dispose();
        themeObserver = DesktopTheme.Observe(this, Refresh);
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        if (xamlRoot is not null) xamlRoot.Changed -= OnRootChanged;
        xamlRoot = null;
        themeObserver?.Dispose();
        themeObserver = null;
        lastKey = null;
        image.Source = null;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        string text = Text ?? "";
        AutomationProperties.SetName(this, text);
        if (!IsLoaded || string.IsNullOrWhiteSpace(text))
        {
            lastKey = null;
            image.Source = null;
            image.Width = 0;
            image.Height = 0;
            return;
        }
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        double maxWidth = double.IsFinite(MaxLineWidth) && MaxLineWidth > 0 ? Math.Floor(MaxLineWidth) : double.PositiveInfinity;
        bool highContrast = DesktopTheme.GetIsHighContrast(this);
        var fill = MusicDesktopLyricRenderer.ResolveColor(Foreground, Colors.White);
        var stroke = MusicDesktopLyricRenderer.ResolveStrokeColor(highContrast);
        var key = (text, FontSize, maxWidth, Math.Round(scale, 3), FlowDirection, fill, stroke);
        if (lastKey == key) return;
        lastKey = key;

        var rendered = MusicDesktopLyricRenderer.RenderSingle(text, FontSize, maxWidth, scale, FlowDirection, fill, stroke);
        if (rendered is null)
        {
            image.Source = null;
            image.Width = 0;
            image.Height = 0;
            return;
        }
        image.Width = rendered.WidthDip;
        image.Height = rendered.HeightDip;
        image.Source = rendered.Image;
    }
}
