using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

/// <summary>Desktop ordinary text with retained native shaping and a narrow outline.</summary>
public sealed class MusicDesktopLyricTextView : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(MusicDesktopLyricTextView), new PropertyMetadata("", (owner, _) => ((MusicDesktopLyricTextView)owner).RefreshText()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private readonly TextBlock text = new();
    private readonly Border textSurface;
    private readonly MusicLyricOutline outline;
    private MusicDesktopLyricsInputRegion? inputRegion;
    internal MusicLyricOutline Outline => outline;
    public MusicDesktopLyricTextView()
    {
        var grid = new Grid(); outline = new(grid);
        textSurface = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = text };
        grid.Children.Add(textSurface); Content = grid;
        IsTabStop = false; IsHitTestVisible = false;
        foreach (var property in new[] { FontSizeProperty, FontFamilyProperty, FlowDirectionProperty, LanguageProperty })
            RegisterPropertyChangedCallback(property, (_, _) => RefreshTypography());
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => text.Foreground = Foreground);
        Loaded += (_, _) =>
        {
            inputRegion = MusicDesktopLyricsInputRegion.Find(this);
            inputRegion?.Register(this, outline);
        };
        Unloaded += (_, _) => { inputRegion?.Unregister(this); inputRegion = null; };
        RefreshTypography(); text.Foreground = Foreground;
    }
    private void RefreshText()
    {
        text.Text = Text;
        AutomationProperties.SetName(this, Text);
        RefreshOutline();
    }
    private void RefreshTypography()
    {
        text.FontFamily = FontFamily; text.FontSize = FontSize;
        text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        text.FlowDirection = FlowDirection; text.Language = Language;
        RefreshOutline();
    }
    private void RefreshOutline() => outline.Update(text, textSurface, MusicVisuals.Brush("#E6191919"), 1.1);
}
