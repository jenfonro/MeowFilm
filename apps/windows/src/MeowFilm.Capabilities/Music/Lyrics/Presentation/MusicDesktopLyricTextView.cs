using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

/// <summary>Desktop ordinary text with retained native shaping and a narrow outline.</summary>
public sealed class MusicDesktopLyricTextView : UserControl
{
    private static readonly SolidColorBrush OutlineBrush = DesktopVisual.Brush("#E6191919");
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

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
        textSurface = new Border { Background = TransparentBrush, Child = text };
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
        string next = Text ?? "";
        if (text.Text == next) return;
        text.Text = next;
        AutomationProperties.SetName(this, next);
        RefreshOutline();
    }

    private void RefreshTypography()
    {
        bool changed = text.FontFamily != FontFamily || Math.Abs(text.FontSize - FontSize) > 1e-6 ||
            text.FlowDirection != FlowDirection || text.Language != Language ||
            text.FontWeight.Weight != Microsoft.UI.Text.FontWeights.Normal.Weight;
        if (!changed) return;
        text.FontFamily = FontFamily; text.FontSize = FontSize;
        text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        text.FlowDirection = FlowDirection; text.Language = Language;
        RefreshOutline();
    }

    private void RefreshOutline()
    {
        if (string.IsNullOrWhiteSpace(text.Text)) outline.Update(text, textSurface, null, 0);
        else outline.Update(text, textSurface, OutlineBrush, 1.1);
    }
}
