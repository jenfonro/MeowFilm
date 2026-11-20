using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

/// <summary>Desktop ordinary text with retained native shaping and a narrow outline.</summary>
public sealed class MusicDesktopLyricTextView : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(MusicDesktopLyricTextView), new PropertyMetadata("", (owner, _) => ((MusicDesktopLyricTextView)owner).Refresh()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private readonly TextBlock text = new();
    private readonly Border textSurface;
    private readonly MusicLyricOutline outline;
    internal MusicLyricOutline Outline => outline;
    public MusicDesktopLyricTextView()
    {
        var grid = new Grid(); outline = new(grid);
        textSurface = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = text };
        grid.Children.Add(textSurface); Content = grid;
        IsTabStop = false; IsHitTestVisible = false;
        foreach (var property in new[] { FontSizeProperty, FontFamilyProperty, ForegroundProperty, FlowDirectionProperty, LanguageProperty })
            RegisterPropertyChangedCallback(property, (_, _) => Refresh());
        Loaded += (_, _) => Refresh();
    }
    private void Refresh()
    {
        text.Text = Text; text.FontFamily = FontFamily; text.FontSize = FontSize;
        text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        text.Foreground = Foreground; text.FlowDirection = FlowDirection; text.Language = Language;
        outline.Update(text, textSurface, MusicVisuals.Brush("#E6191919"), 1.1);
        AutomationProperties.SetName(this, Text);
    }
}
