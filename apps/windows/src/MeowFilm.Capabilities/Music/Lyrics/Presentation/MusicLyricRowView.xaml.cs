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
    private MusicLyricList? presenter;
    private FrameworkElement? rowHost;
    private IDisposable? themeObserver;
    private MusicLyricRow? row;
    private bool? lastSelected;
    private bool? lastShowTranslation;
    private bool? lastHighContrast;

    public MusicLyricRowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Reload();
        Loaded += (_, _) =>
        {
            if (lyrics is not null) lyrics.PropertyChanged -= Changed;
            lyrics = null;
            presenter = null;
            rowHost = null;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (rowHost is null && parent is ListViewItem item) rowHost = item;
                if (presenter is null && parent is MusicLyricList list) presenter = list;
                if (parent is MusicPlayerDetailView detail) { lyrics = detail.Lyrics; break; }
            }
            if (lyrics is not null) lyrics.PropertyChanged += Changed;
            themeObserver?.Dispose();
            themeObserver = DesktopTheme.Observe(this, () => Refresh(force: true));
            AttachWordsToPresenter();
            Reload();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            if (lyrics is not null) lyrics.PropertyChanged -= Changed;
            lyrics = null;
            themeObserver?.Dispose();
            themeObserver = null;
            for (int i = 0; i < WordsPanel.Children.Count; i++)
                if (WordsPanel.Children[i] is MusicLyricWordView word) word.DetachPresenter();
            presenter = null;
            rowHost = null;
        };
    }

    private void AttachWordsToPresenter()
    {
        if (presenter is null) return;
        var host = rowHost ?? this;
        for (int i = 0; i < WordsPanel.Children.Count; i++)
            if (WordsPanel.Children[i] is MusicLyricWordView word) word.AttachPresenter(presenter, host);
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
        int targetCount = timed?.Count ?? 0, currentCount = WordsPanel.Children.Count;
        for (int i = 0, reuse = Math.Min(currentCount, targetCount); i < reuse; i++)
            ((MusicLyricWordView)WordsPanel.Children[i]).Word = timed![i];
        var host = rowHost ?? this;
        for (int i = currentCount; i < targetCount; i++)
        {
            var view = wordPool.Count > 0 ? wordPool[^1] : new MusicLyricWordView();
            if (wordPool.Count > 0) wordPool.RemoveAt(wordPool.Count - 1);
            view.Word = timed![i];
            WordsPanel.Children.Add(view);
            if (presenter is not null) view.AttachPresenter(presenter, host);
        }
        for (int i = currentCount - 1; i >= targetCount; i--)
        {
            var view = (MusicLyricWordView)WordsPanel.Children[i];
            view.DetachPresenter();
            WordsPanel.Children.RemoveAt(i);
            view.StopHighlight(0);
            if (wordPool.Count < WordPoolLimit) wordPool.Add(view);
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
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        if (force || lastSelected != selected || lastHighContrast != contrast)
        {
            lastSelected = selected;
            lastHighContrast = contrast;
            var normal = selected ? SelectedTextBrush : UnselectedTextBrush;
            PlainText.Foreground = LyricTranslation.Foreground = RomanizationText.Foreground = normal;
            var wordNormal = selected ? SelectedWordNormalBrush : UnselectedWordNormalBrush;
            var wordHighlight = selected ? SelectedWordHighlightBrush : UnselectedWordHighlightBrush;
            for (int i = 0; i < WordsPanel.Children.Count; i++)
            {
                if (WordsPanel.Children[i] is MusicLyricWordView word)
                    word.SetColors(wordNormal, wordHighlight, contrast);
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
