using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public partial class MusicDesktopLyricLineView : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row),
        typeof(MusicLyricRow), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, (owner, _) => ((MusicDesktopLyricLineView)owner).UpdateRow()));
    public static readonly DependencyProperty NormalProperty = DependencyProperty.Register(nameof(Normal),
        typeof(Brush), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, (owner, _) => ((MusicDesktopLyricLineView)owner).UpdateNormalColor()));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight),
        typeof(Brush), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, (owner, _) => ((MusicDesktopLyricLineView)owner).UpdateHighlightColor()));
    public static readonly DependencyProperty ShowTranslationProperty = DependencyProperty.Register(nameof(ShowTranslation),
        typeof(bool), typeof(MusicDesktopLyricLineView), new PropertyMetadata(true, (owner, _) => ((MusicDesktopLyricLineView)owner).UpdateTranslation()));
    public MusicLyricRow? Row { get => (MusicLyricRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public Brush Normal { get => (Brush)GetValue(NormalProperty); set => SetValue(NormalProperty, value); }
    public Brush Highlight { get => (Brush)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public bool ShowTranslation { get => (bool)GetValue(ShowTranslationProperty); set => SetValue(ShowTranslationProperty, value); }

    private MusicDesktopLyricsInputRegion? inputRegion;
    public MusicDesktopLyricLineView()
    {
        InitializeComponent();
        Loaded += (_, _) => { inputRegion = MusicDesktopLyricsInputRegion.Find(this); inputRegion?.Refresh(); };
        Unloaded += (_, _) => { inputRegion?.Refresh(); inputRegion = null; };
        UpdateRow();
    }

    private void UpdateRow()
    {
        if (PlainText is null) return;
        Words.Children.Clear();
        if (Row?.Words is { } words)
            foreach (var word in words) Words.Children.Add(new MusicLyricWordView
            {
                Word = word, FontSize = 32, Foreground = Normal, Highlight = Highlight,
                Stroke = MusicVisuals.Brush("#E6191919"), StrokeThickness = 1.1
            });
        // Only source words with real timing enter the existing word presenter.
        bool timed = Row?.Words is { Count: > 0 };
        PlainText.Visibility = timed ? Visibility.Collapsed : Visibility.Visible;
        Words.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        Opacity = Row is null ? 0 : 1;
        UpdateTranslation();
    }
    private void UpdateNormalColor()
    {
        if (Words is null) return;
        foreach (var word in Words.Children.OfType<MusicLyricWordView>()) word.Foreground = Normal;
    }
    private void UpdateHighlightColor()
    {
        if (Words is null) return;
        foreach (var word in Words.Children.OfType<MusicLyricWordView>()) word.Highlight = Highlight;
    }
    private void UpdateTranslation()
    {
        if (TranslationFrame is null) return;
        TranslationFrame.Visibility = ShowTranslation && !string.IsNullOrWhiteSpace(Row?.Translation)
            ? Visibility.Visible : Visibility.Collapsed;
        inputRegion?.Refresh();
    }
}
