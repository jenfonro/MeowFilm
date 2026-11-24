using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class MusicLyricRowView : UserControl
{
    private const int WordPoolLimit = 24;
    [ThreadStatic] private static SolidColorBrush? selectedTextBrush, unselectedTextBrush;
    [ThreadStatic] private static SolidColorBrush? selectedWordNormalBrush, unselectedWordNormalBrush;
    [ThreadStatic] private static SolidColorBrush? selectedWordHighlightBrush, unselectedWordHighlightBrush;

    private static SolidColorBrush SelectedTextBrush => selectedTextBrush ??= DesktopVisual.Brush("#EBEAED");
    private static SolidColorBrush UnselectedTextBrush => unselectedTextBrush ??= DesktopVisual.Brush("#8B898C");
    private static SolidColorBrush SelectedWordNormalBrush => selectedWordNormalBrush ??= DesktopVisual.Brush("#8B898C");
    private static SolidColorBrush UnselectedWordNormalBrush => unselectedWordNormalBrush ??= DesktopVisual.Brush("#68666A");
    private static SolidColorBrush SelectedWordHighlightBrush => selectedWordHighlightBrush ??= DesktopVisual.Brush("#F4F3F6");
    private static SolidColorBrush UnselectedWordHighlightBrush => unselectedWordHighlightBrush ??= DesktopVisual.Brush("#928F90");

    private readonly List<MusicLyricWordView> wordPool = [];
    private MusicLyricsController? lyrics;
    private MusicLyricRow? row;
    private bool? lastSelected;
    private bool? lastShowTranslation;

    public MusicLyricRowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Reload();
        Loaded += (_, _) =>
        {
            if (lyrics is not null) lyrics.PropertyChanged -= Changed;
            lyrics = null;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is MusicPlayerDetailView detail) { lyrics = detail.Lyrics; break; }
            if (lyrics is not null) lyrics.PropertyChanged += Changed;
            Reload();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            if (lyrics is not null) lyrics.PropertyChanged -= Changed;
            lyrics = null;
        };
    }

    private void Reload()
    {
        if (!ReferenceEquals(row, DataContext))
        {
            row = DataContext as MusicLyricRow;
            SyncWordViews(row?.Words);
            lastSelected = null;
            lastShowTranslation = null;
        }
        PlainText.Text = row?.Text ?? "";
        PlainText.Visibility = WordsPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LyricTranslation.Text = row?.Translation ?? "";
        RomanizationText.Text = row?.Romanization ?? "";
        Refresh(force: true);
    }

    private void SyncWordViews(IReadOnlyList<MusicLyricWord>? timed)
    {
        int targetCount = timed?.Count ?? 0;
        int currentCount = WordsPanel.Children.Count;
        int reuseCount = Math.Min(currentCount, targetCount);

        for (int i = 0; i < reuseCount; i++)
        {
            var view = (MusicLyricWordView)WordsPanel.Children[i];
            view.Word = timed![i];
        }

        if (targetCount > currentCount)
        {
            for (int i = currentCount; i < targetCount; i++)
            {
                MusicLyricWordView view;
                if (wordPool.Count > 0)
                {
                    int last = wordPool.Count - 1;
                    view = wordPool[last];
                    wordPool.RemoveAt(last);
                    view.Word = timed![i];
                }
                else
                {
                    view = new MusicLyricWordView { Word = timed![i], FontSize = 20 };
                }
                WordsPanel.Children.Add(view);
            }
        }
        else if (targetCount < currentCount)
        {
            for (int i = currentCount - 1; i >= targetCount; i--)
            {
                var view = (MusicLyricWordView)WordsPanel.Children[i];
                WordsPanel.Children.RemoveAt(i);
                view.StopHighlight(0);
                if (wordPool.Count < WordPoolLimit)
                    wordPool.Add(view);
            }
        }
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicLyricsController.LyricDisplayIndex) or nameof(MusicLyricsController.ShowLyricTranslation))
            Refresh();
    }

    private void Refresh(bool force = false)
    {
        bool selected = lyrics is { } owner && owner.LyricDisplayIndex is int displayIndex &&
            displayIndex >= 0 && displayIndex < owner.Lyrics.Count && ReferenceEquals(owner.Lyrics[displayIndex], row);
        bool showTranslation = lyrics?.ShowLyricTranslation != false && LyricTranslation.Text.Length > 0;
        if (force || lastSelected != selected)
        {
            lastSelected = selected;
            var normal = selected ? SelectedTextBrush : UnselectedTextBrush;
            PlainText.Foreground = LyricTranslation.Foreground = RomanizationText.Foreground = normal;
            var wordNormal = selected ? SelectedWordNormalBrush : UnselectedWordNormalBrush;
            var wordHighlight = selected ? SelectedWordHighlightBrush : UnselectedWordHighlightBrush;
            for (int i = 0; i < WordsPanel.Children.Count; i++)
            {
                if (WordsPanel.Children[i] is MusicLyricWordView word)
                {
                    word.Foreground = wordNormal;
                    word.Highlight = wordHighlight;
                }
            }
        }
        if (force || lastShowTranslation != showTranslation)
        {
            lastShowTranslation = showTranslation;
            LyricTranslation.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        }
        RomanizationText.Visibility = RomanizationText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
