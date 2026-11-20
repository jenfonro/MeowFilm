using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

public enum MusicCatalogCardLayout { Playlist, RankCover, Row, AlbumRank, Banner, Ranking, Song, Artist }

// Card layout owns only its native presentation and delegates every action to the music owner.
public sealed class MusicCatalogCardView : UserControl
{
    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(nameof(Presentation), typeof(MusicCatalogCardLayout), typeof(MusicCatalogCardView), new PropertyMetadata(MusicCatalogCardLayout.Playlist, (owner, _) => ((MusicCatalogCardView)owner).Build()));
    public MusicCatalogCardLayout Presentation { get => (MusicCatalogCardLayout)GetValue(PresentationProperty); set => SetValue(PresentationProperty, value); }
    private MusicCatalogCard? observed;
    private MenuFlyout? menu;
    private StackPanel? preview;
    private TextBlock? fallback;
    private string? builtKey;
    private MusicCatalogCardLayout builtPresentation;
    private MusicCapability? Owner
    {
        get
        {
            for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is MusicCatalogView view) return view.Owner;
            return null;
        }
    }
    public MusicCatalogCardView()
    {
        _ = new MusicCatalogSlot(this);
        DataContextChanged += (_, _) => { menu?.Hide(); Observe(); }; Loaded += (_, _) => Observe();
        Unloaded += (_, _) => { menu?.Hide(); if (observed is not null) observed.PropertyChanged -= Changed; observed = null; };
        RightTapped += (_, e) => { if (DataContext is MusicCatalogCard card) { ShowMenu(this, card); e.Handled = true; } };
    }
    private void Observe()
    {
        if (ReferenceEquals(observed, DataContext)) return;
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = DataContext as MusicCatalogCard;
        if (observed is not null) observed.PropertyChanged += Changed;
        if (observed is null || builtKey != observed.Key || builtPresentation != Presentation) Build();
        else { UpdatePreview(); UpdateFallback(); }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicCatalogCard.PreviewSongs)) UpdatePreview();
        if (e.PropertyName == nameof(MusicCatalogCard.Cover)) UpdateFallback();
    }
    private void UpdateFallback()
    {
        if (fallback is not null) fallback.Visibility = string.IsNullOrWhiteSpace(observed?.Cover) ? Visibility.Visible : Visibility.Collapsed;
    }
    private static void Bind(FrameworkElement target, DependencyProperty property, string path) =>
        target.SetBinding(property, new Binding { Path = new PropertyPath(path), Mode = BindingMode.OneWay });
    private static void BindVisible(FrameworkElement target, string path) =>
        target.SetBinding(VisibilityProperty, new Binding { Path = new PropertyPath(path), Converter = new MusicVisibilityConverter() });
    private void UpdatePreview()
    {
        if (preview is null || observed is null) return;
        var songs = observed.PreviewSongs.Take(3).ToArray();
        while (preview.Children.Count > songs.Length) preview.Children.RemoveAt(preview.Children.Count - 1);
        for (int i = 0; i < songs.Length; i++)
        {
            if (i == preview.Children.Count) preview.Children.Add(Text("", 12));
            var song = songs[i];
            ((TextBlock)preview.Children[i]).Text = $"{song.Rank}  {song.Title} − {song.Artist}  {song.Movement}";
        }
    }
    private static TextBlock CardText(string property, double size = 14)
    {
        var text = Text("", size); Bind(text, TextBlock.TextProperty, property); return text;
    }
    private static ArtworkImage CardImage(double size)
    {
        var image = Image("", "", size);
        BindSongIdentity(image); Bind(image, ArtworkImage.AddressProperty, nameof(MusicCatalogCard.Cover)); return image;
    }
    internal static IEnumerable<ArtworkItemResource> ArtworkResources(MusicCatalogCard card)
    {
        yield return new("cover", card.Cover, card.Song?.Id ?? "");
        yield return new("decoration", card.Decoration);
        yield return new("first", card.FirstPreviewCover, card.FirstPreviewIdentity);
        yield return new("second", card.SecondPreviewCover, card.SecondPreviewIdentity);
        yield return new("third", card.ThirdPreviewCover, card.ThirdPreviewIdentity);
    }
    private static void BindSongIdentity(ArtworkImage image) => image.SetBinding(ArtworkImage.DisplayIdentityProperty,
        new Binding { Path = new PropertyPath("Song.Id"), TargetNullValue = "", FallbackValue = "", Mode = BindingMode.OneWay });
    private static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private static ArtworkImage Image(string address, string identity, double size) => new() { Address = address, DisplayIdentity = identity, Width = size, Height = size, Stretch = Stretch.UniformToFill };
    private Button Action(string label, Func<MusicCapability, MusicCatalogCard, Task> action, string? enabled = null)
    {
        var button = new Button { Content = label, FontWeight = Microsoft.UI.Text.FontWeights.Normal, Padding = new Thickness(6), Margin = new Thickness(2, 0, 2, 0), Background = MusicVisuals.Brush("#00000000"), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(button, label);
        if (enabled is not null) Bind(button, IsEnabledProperty, enabled);
        button.Click += async (_, _) => { if (Owner is { } owner && DataContext is MusicCatalogCard card) await action(owner, card); };
        return button;
    }
    private void Build()
    {
        preview = null; fallback = null;
        builtKey = (DataContext as MusicCatalogCard)?.Key; builtPresentation = Presentation;
        if (DataContext is not MusicCatalogCard) { Content = null; return; }
        var body = new Grid();
        var open = new Button { Padding = new Thickness(0), Margin = new Thickness(0), Background = MusicVisuals.Brush("#00000000"), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        MeowFilm.Desktop.DesktopVisual.UseResource(open, Control.TemplateProperty, "MeowFilm.ContentButtonTemplate");
        open.Click += async (_, _) => { if (Owner is { } owner && DataContext is MusicCatalogCard card) await owner.OpenCatalogCardAsync(card); };
        open.SetBinding(TagProperty, new Binding { Mode = BindingMode.OneWay }); Bind(open, IsEnabledProperty, nameof(MusicCatalogCard.CanOpen));
        Bind(open, AutomationProperties.NameProperty, nameof(MusicCatalogCard.Title));
        if (Presentation == MusicCatalogCardLayout.Song)
        {
            var row = new Border { Height = 84, CornerRadius = new CornerRadius(12), Background = MusicVisuals.Brush("#00000000") };
            var overlay = new Grid(); var hover = new Border { CornerRadius = new CornerRadius(12), Background = (Brush)MeowFilm.Desktop.DesktopVisual.Resource(this, "MeowFilm.Brush.Surface"), IsHitTestVisible = false };
            overlay.Children.Add(hover); row.Child = overlay;
            body.ColumnDefinitions.Add(new() { Width = new GridLength(60) }); body.ColumnDefinitions.Add(new());
            var artwork = new Grid { Width = 50, Height = 50, HorizontalAlignment = HorizontalAlignment.Left };
            CardClip.SetRadius(artwork, 8); artwork.Children.Add(CardImage(50));
            var glyph = new CardPlayGlyph { Foreground = MusicVisuals.Brush("#FFFFFF") };
            Bind(glyph, CardPlayGlyph.IsBusyProperty, nameof(MusicCatalogCard.IsPreparing));
            var shade = new Border { Background = MusicVisuals.Brush("#40000000"), Child = glyph, IsHitTestVisible = false };
            BindVisible(shade, nameof(MusicCatalogCard.CanPlay)); artwork.Children.Add(shade); body.Children.Add(artwork);
            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = CardText(nameof(MusicCatalogCard.Title)); title.LineHeight = 20;
            var subtitle = CardText(nameof(MusicCatalogCard.Subtitle), 12); subtitle.LineHeight = 18; subtitle.Margin = new Thickness(0, 2, 0, 0);
            copy.Children.Add(title); copy.Children.Add(subtitle); Grid.SetColumn(copy, 1); body.Children.Add(copy);
            open.Padding = new Thickness(8); open.Content = body; overlay.Children.Add(open);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Background = hover.Background };
            var favorite = Action("收藏", (owner, card) => owner.FavoriteCatalogCardAsync(card), nameof(MusicCatalogCard.CanFavorite));
            favorite.Content = new FontIcon { Glyph = "\uEB51", FontSize = 16 };
            var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 16 }, Background = MusicVisuals.Brush("#00000000"), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(more, "歌曲操作"); more.Click += (_, _) => { if (DataContext is MusicCatalogCard card) ShowMenu(more, card); };
            foreach (var action in new[] { favorite, more }) { action.Width = action.Height = 36; action.Margin = new Thickness(0); action.Padding = new Thickness(0); actions.Children.Add(action); }
            overlay.Children.Add(actions);
            CardMotion.SetEnabled(row, true);
            void Motion()
            {
                double progress = CardMotion.GetProgress(row); hover.Opacity = shade.Opacity = actions.Opacity = progress;
                actions.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
                actions.IsHitTestVisible = progress > 0; copy.Margin = new Thickness(0, 0, progress > 0 ? 72 : 0, 0);
            }
            row.RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => Motion()); Motion();
            Content = row; return;
        }
        if (Presentation is MusicCatalogCardLayout.Row or MusicCatalogCardLayout.AlbumRank)
        {
            body.Height = Presentation == MusicCatalogCardLayout.AlbumRank ? 72 : 64;
            double size = Presentation == MusicCatalogCardLayout.AlbumRank ? 64 : 48;
            body.ColumnDefinitions.Add(new() { Width = new GridLength(Presentation == MusicCatalogCardLayout.AlbumRank ? 44 : 0) });
            body.ColumnDefinitions.Add(new() { Width = new GridLength(size + 12) }); body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var rank = CardText(nameof(MusicCatalogCard.RankText), 22); body.Children.Add(rank);
            var image = CardImage(size); Grid.SetColumn(image, 1); body.Children.Add(image);
            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) };
            copy.Children.Add(CardText(nameof(MusicCatalogCard.Title))); copy.Children.Add(CardText(nameof(MusicCatalogCard.Subtitle), 12)); Grid.SetColumn(copy, 2); body.Children.Add(copy);
            if (Presentation == MusicCatalogCardLayout.AlbumRank) { var sales = CardText(nameof(MusicCatalogCard.SalesText), 12); Grid.SetColumn(sales, 3); body.Children.Add(sales); }
            open.Content = body;
            var overlay = new Grid(); overlay.Children.Add(open);
            Content = overlay; return;
        }
        if (Presentation == MusicCatalogCardLayout.Ranking)
        {
            body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new());
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 14) }; heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.Children.Add(CardText(nameof(MusicCatalogCard.Title), 18)); var updated = CardText(nameof(MusicCatalogCard.UpdateFrequency), 11); Grid.SetColumn(updated, 1); heading.Children.Add(updated); body.Children.Add(heading);
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(96) }); row.ColumnDefinitions.Add(new());
            var covers = new Grid { Width = 80, Height = 80, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (address, identity, slot, inset) in new[] {
                (nameof(MusicCatalogCard.ThirdPreviewCover), nameof(MusicCatalogCard.ThirdPreviewIdentity), "third", 12),
                (nameof(MusicCatalogCard.SecondPreviewCover), nameof(MusicCatalogCard.SecondPreviewIdentity), "second", 6),
                (nameof(MusicCatalogCard.FirstPreviewCover), nameof(MusicCatalogCard.FirstPreviewIdentity), "first", 0) })
            {
                var image = Image("", "", 80); image.ResourceSlot = slot;
                Bind(image, ArtworkImage.AddressProperty, address);
                Bind(image, ArtworkImage.DisplayIdentityProperty, identity);
                image.Margin = new Thickness(inset, inset, -inset / 2d, -inset / 3d); covers.Children.Add(image);
            }
            row.Children.Add(covers);
            preview = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; UpdatePreview();
            Grid.SetColumn(preview, 1); row.Children.Add(preview); Grid.SetRow(row, 1); body.Children.Add(row);
            open.Content = new Border { Padding = new Thickness(20), Height = 154, CornerRadius = new CornerRadius(12), Child = body, Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"] };
            Content = OverlayPlay(open, covers); return;
        }
        var stack = new StackPanel();
        var cover = new ArtworkImage { Stretch = Presentation == MusicCatalogCardLayout.Banner ? Stretch.Uniform : Stretch.UniformToFill };
        BindSongIdentity(cover); Bind(cover, ArtworkImage.AddressProperty, nameof(MusicCatalogCard.Cover));
        var frame = new Grid { CornerRadius = new CornerRadius(Presentation == MusicCatalogCardLayout.Artist ? 1000 : 8), Background = MusicVisuals.Brush("#E7EAF0") };
        CardClip.SetRadius(frame, Presentation == MusicCatalogCardLayout.Artist ? 1000 : 8);
        frame.Children.Add(cover); frame.SizeChanged += (_, _) => frame.Height = Presentation == MusicCatalogCardLayout.Banner ? frame.ActualWidth / 2.56 : frame.ActualWidth;
        fallback = CardText(nameof(MusicCatalogCard.Title), 18); fallback.TextWrapping = TextWrapping.Wrap; fallback.Margin = new Thickness(10); frame.Children.Add(fallback); UpdateFallback();
        stack.Children.Add(frame);
        if (Presentation is not (MusicCatalogCardLayout.Banner or MusicCatalogCardLayout.RankCover))
        {
            var title = CardText(nameof(MusicCatalogCard.Title), 13); title.Height = 40; title.Margin = new Thickness(0, 8, 0, 0); title.TextWrapping = TextWrapping.Wrap; stack.Children.Add(title);
            if (Presentation == MusicCatalogCardLayout.Artist) stack.Children.Add(CardText(nameof(MusicCatalogCard.Subtitle), 12));
        }
        open.Content = stack;
        var result = OverlayPlay(open, frame);
        var heart = Action("心动", (owner, card) => owner.HeartbeatFavoritesCatalogCardAsync(card), nameof(MusicCatalogCard.CanHeartbeatFavorites));
        BindVisible(heart, nameof(MusicCatalogCard.CanHeartbeatFavorites)); heart.HorizontalAlignment = HorizontalAlignment.Right; heart.VerticalAlignment = VerticalAlignment.Bottom; result.Children.Add(heart);
        Content = result;
    }
    private Grid OverlayPlay(Button open, Grid artwork)
    {
        var grid = new Grid(); grid.Children.Add(open);
        Border? shade = null;
        if (Presentation == MusicCatalogCardLayout.Playlist)
        {
            var brushes = new ResourceDictionary { Source = new("ms-appx:///MeowFilm.Capabilities/Presentation/Controls/CardBrushes.xaml") };
            shade = new Border { Background = (Brush)brushes["Card.CoverHover"], IsHitTestVisible = false, Opacity = 0 };
            BindVisible(shade, nameof(MusicCatalogCard.CanPlay)); artwork.Children.Add(shade);
        }
        var play = Action("播放", (owner, card) => owner.PlayCatalogCardAsync(card), nameof(MusicCatalogCard.CanPlay));
        BindVisible(play, nameof(MusicCatalogCard.CanPlay));
        var glyph = new CardPlayGlyph { Foreground = MusicVisuals.Brush("#FFFFFF") }; Bind(glyph, CardPlayGlyph.IsBusyProperty, nameof(MusicCatalogCard.IsPreparing)); play.Content = glyph;
        play.Width = play.Height = 44; play.HorizontalAlignment = HorizontalAlignment.Center; play.VerticalAlignment = VerticalAlignment.Center;
        play.Opacity = 0; play.IsHitTestVisible = false; artwork.Children.Add(play);
        CardMotion.SetEnabled(grid, true); grid.RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) =>
        {
            play.Opacity = CardMotion.GetProgress(grid); play.IsHitTestVisible = play.Opacity > 0;
            if (shade is not null) shade.Opacity = play.Opacity;
        });
        return grid;
    }
    internal MenuFlyout? ShowMenu(FrameworkElement host, MusicCatalogCard card)
    {
        if (Owner is not { } owner || card.Kind != "playlist" && card.Song is null) return null;
        menu?.Hide();
        var next = new MenuFlyout(); bool Current() => IsLoaded && ReferenceEquals(DataContext, card) && ReferenceEquals(Owner, owner);
        if (card.Song is not null) owner.PopulateSongMenu(next, card.Song, card, Current); else owner.PopulatePlaylistMenu(next, card, Current);
        MusicCapability.ApplyContentMenuStyles(host, next);
        next.Closed += (_, _) => { if (ReferenceEquals(menu, next)) menu = null; };
        menu = next; next.ShowAt(host);
        return next;
    }
}
