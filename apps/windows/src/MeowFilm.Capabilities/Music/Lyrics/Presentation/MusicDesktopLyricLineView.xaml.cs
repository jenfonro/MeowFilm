using MeowFilm.Desktop;
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

    private const int WordPoolLimit = 24;
    [ThreadStatic]
    private static SolidColorBrush? strokeBrush;
    private static SolidColorBrush StrokeBrush => strokeBrush ??= DesktopVisual.Brush("#E6191919");
    private readonly List<MusicLyricWordView> wordPool = [];
    private MusicDesktopLyricsInputRegion? inputRegion;
    public MusicDesktopLyricLineView()
    {
        InitializeComponent();
        Loaded += (_, _) => { inputRegion = MusicDesktopLyricsInputRegion.Find(this); inputRegion?.Refresh(); };
        Unloaded += (_, _) => { if (IsLoaded) return; inputRegion?.Refresh(); inputRegion = null; };
        UpdateRow();
    }

    private void UpdateRow()
    {
        if (PlainText is null) return;
        SyncWordViews(Row?.Words);
        // Only source words with real timing enter the existing word presenter.
        bool timed = Row?.Words is { Count: > 0 };
        PlainText.Visibility = timed ? Visibility.Collapsed : Visibility.Visible;
        Words.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        Opacity = Row is null ? 0 : 1;
        UpdateTranslation();
    }
    private void SyncWordViews(IReadOnlyList<MusicLyricWord>? timed)
    {
        int targetCount = timed?.Count ?? 0;
        int currentCount = Words.Children.Count;
        int reuseCount = Math.Min(currentCount, targetCount);
        for (int i = 0; i < reuseCount; i++)
            ((MusicLyricWordView)Words.Children[i]).Word = timed![i];
        if (targetCount > currentCount)
        {
            var stroke = StrokeBrush;
            for (int i = currentCount; i < targetCount; i++)
            {
                MusicLyricWordView view;
                if (wordPool.Count > 0)
                {
                    int last = wordPool.Count - 1;
                    view = wordPool[last];
                    wordPool.RemoveAt(last);
                    view.Word = timed![i];
                    view.Foreground = Normal;
                    view.Highlight = Highlight;
                }
                else
                {
                    view = new MusicLyricWordView
                    {
                        Word = timed![i], FontSize = 32, Foreground = Normal, Highlight = Highlight,
                        Stroke = stroke, StrokeThickness = 1.1
                    };
                }
                Words.Children.Add(view);
            }
        }
        else if (targetCount < currentCount)
        {
            for (int i = currentCount - 1; i >= targetCount; i--)
            {
                var view = (MusicLyricWordView)Words.Children[i];
                Words.Children.RemoveAt(i);
                view.StopHighlight(0);
                if (wordPool.Count < WordPoolLimit) wordPool.Add(view);
            }
        }
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
