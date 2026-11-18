using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public partial class MusicDesktopLyricLineView : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row),
        typeof(MusicLyricRow), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty NormalProperty = DependencyProperty.Register(nameof(Normal),
        typeof(Brush), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight),
        typeof(Brush), typeof(MusicDesktopLyricLineView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ShowTranslationProperty = DependencyProperty.Register(nameof(ShowTranslation),
        typeof(bool), typeof(MusicDesktopLyricLineView), new PropertyMetadata(true, Changed));
    public MusicLyricRow? Row { get => (MusicLyricRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public Brush Normal { get => (Brush)GetValue(NormalProperty); set => SetValue(NormalProperty, value); }
    public Brush Highlight { get => (Brush)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public bool ShowTranslation { get => (bool)GetValue(ShowTranslationProperty); set => SetValue(ShowTranslationProperty, value); }

    private MusicLyricRow? displayed;
    public MusicDesktopLyricLineView()
    {
        InitializeComponent();
        UpdateLine();
    }

    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args) =>
        ((MusicDesktopLyricLineView)owner).UpdateLine();

    private void UpdateLine()
    {
        if (PlainText is null) return;
        if (!ReferenceEquals(displayed, Row))
        {
            displayed = Row; Words.Children.Clear();
            if (Row?.Words is { } words)
                foreach (var word in words) Words.Children.Add(new MusicLyricWordView { Word = word, FontSize = 32, Stroke = MusicVisuals.Brush("#E6191919"), StrokeThickness = 1.1 });
        }
        foreach (var word in Words.Children.OfType<MusicLyricWordView>()) { word.Foreground = Normal; word.Highlight = Highlight; }
        // Only source words with real timing enter the existing word presenter.
        bool timed = Row?.Words is { Count: > 0 };
        PlainText.Visibility = timed ? Visibility.Collapsed : Visibility.Visible;
        Words.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        TranslationFrame.Visibility = ShowTranslation && !string.IsNullOrWhiteSpace(Row?.Translation)
            ? Visibility.Visible : Visibility.Collapsed;
        Opacity = Row is null ? 0 : 1;
    }
}
