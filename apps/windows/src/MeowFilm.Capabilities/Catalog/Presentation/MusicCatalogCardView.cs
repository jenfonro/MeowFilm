using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
        Build();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MusicCatalogCard.PreviewSongs)) UpdatePreview(); }
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
    private static TextBlock CardText(MusicCatalogCard card, string property, double size = 14)
    {
        var text = Text("", size); MusicVisuals.Bind(text, TextBlock.TextProperty, property, card); return text;
    }
    private static ArtworkImage CardImage(MusicCatalogCard card, double size)
    {
        var image = Image("", card.Song?.Id ?? "", size);
        MusicVisuals.Bind(image, ArtworkImage.AddressProperty, nameof(MusicCatalogCard.Cover), card); return image;
    }
    private static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private static ArtworkImage Image(string address, string identity, double size) => new() { Address = address, DisplayIdentity = identity, Width = size, Height = size, Stretch = Stretch.UniformToFill };
    private Button Action(string label, Func<MusicCapability, Task> action, bool enabled = true)
    {
        var button = new Button { Content = label, IsEnabled = enabled, FontWeight = Microsoft.UI.Text.FontWeights.Normal, Padding = new Thickness(6), Margin = new Thickness(2, 0, 2, 0), Background = MusicVisuals.Brush("#00000000"), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => { if (Owner is { } owner) await action(owner); };
        return button;
    }
    private void Build()
    {
        preview = null;
        if (DataContext is not MusicCatalogCard card) { Content = null; return; }
        var body = new Grid();
        var open = new Button { Tag = card, IsEnabled = card.CanOpen, Padding = new Thickness(0), Background = MusicVisuals.Brush("#00000000"), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        open.Click += async (_, _) => { if (Owner is { } owner) await owner.OpenCatalogCardAsync(card); };
        MusicVisuals.Bind(open, AutomationProperties.NameProperty, nameof(MusicCatalogCard.Title), card);
        if (Presentation == MusicCatalogCardLayout.Song)
        {
            var row = new Border { Height = 84, CornerRadius = new CornerRadius(12), Background = MusicVisuals.Brush("#00000000") };
            var overlay = new Grid(); var hover = new Border { CornerRadius = new CornerRadius(12), Background = (Brush)MeowFilm.Desktop.DesktopVisual.Resource(this, "MeowFilm.Brush.Surface"), IsHitTestVisible = false };
            overlay.Children.Add(hover); row.Child = overlay;
            body.ColumnDefinitions.Add(new() { Width = new GridLength(60) }); body.ColumnDefinitions.Add(new());
            var artwork = new Grid { Width = 50, Height = 50, HorizontalAlignment = HorizontalAlignment.Left };
            CardClip.SetRadius(artwork, 8); artwork.Children.Add(CardImage(card, 50));
            var glyph = new CardPlayGlyph { Foreground = MusicVisuals.Brush("#FFFFFF") };
            MusicVisuals.Bind(glyph, CardPlayGlyph.IsBusyProperty, nameof(MusicCatalogCard.IsPreparing), card);
            var shade = new Border { Background = MusicVisuals.Brush("#40000000"), Child = glyph, IsHitTestVisible = false };
            artwork.Children.Add(shade); body.Children.Add(artwork);
            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = CardText(card, nameof(MusicCatalogCard.Title)); title.LineHeight = 20;
            var subtitle = CardText(card, nameof(MusicCatalogCard.Subtitle), 12); subtitle.LineHeight = 18; subtitle.Margin = new Thickness(0, 2, 0, 0);
            copy.Children.Add(title); copy.Children.Add(subtitle); Grid.SetColumn(copy, 1); body.Children.Add(copy);
            open.Padding = new Thickness(8); open.Content = body; overlay.Children.Add(open);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Background = hover.Background };
            var favorite = Action("收藏", owner => owner.FavoriteCatalogCardAsync(card), card.CanFavorite);
            favorite.Content = new FontIcon { Glyph = "\uEB51", FontSize = 16 };
            var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 16 }, Background = MusicVisuals.Brush("#00000000"), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(more, "歌曲操作"); more.Click += (_, _) => ShowMenu(more, card);
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
            var rank = Text(card.RankText, 22); body.Children.Add(rank);
            var image = CardImage(card, size); Grid.SetColumn(image, 1); body.Children.Add(image);
            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) };
            copy.Children.Add(CardText(card, nameof(MusicCatalogCard.Title))); copy.Children.Add(CardText(card, nameof(MusicCatalogCard.Subtitle), 12)); Grid.SetColumn(copy, 2); body.Children.Add(copy);
            if (Presentation == MusicCatalogCardLayout.AlbumRank) { var sales = Text(card.SalesText, 12); Grid.SetColumn(sales, 3); body.Children.Add(sales); }
            open.Content = body;
            var overlay = new Grid(); overlay.Children.Add(open);
            Content = overlay; return;
        }
        if (Presentation == MusicCatalogCardLayout.Ranking)
        {
            body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new());
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 14) }; heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.Children.Add(CardText(card, nameof(MusicCatalogCard.Title), 18)); var updated = Text(card.UpdateFrequency, 11); Grid.SetColumn(updated, 1); heading.Children.Add(updated); body.Children.Add(heading);
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(96) }); row.ColumnDefinitions.Add(new());
            var covers = new Grid { Width = 80, Height = 80, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (address, identity, inset) in new[] {
                (nameof(MusicCatalogCard.ThirdPreviewCover), nameof(MusicCatalogCard.ThirdPreviewIdentity), 12),
                (nameof(MusicCatalogCard.SecondPreviewCover), nameof(MusicCatalogCard.SecondPreviewIdentity), 6),
                (nameof(MusicCatalogCard.FirstPreviewCover), nameof(MusicCatalogCard.FirstPreviewIdentity), 0) })
            {
                var image = Image("", "", 80);
                MusicVisuals.Bind(image, ArtworkImage.AddressProperty, address, card);
                MusicVisuals.Bind(image, ArtworkImage.DisplayIdentityProperty, identity, card);
                image.Margin = new Thickness(inset, inset, -inset / 2d, -inset / 3d); covers.Children.Add(image);
            }
            row.Children.Add(covers);
            preview = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; UpdatePreview();
            Grid.SetColumn(preview, 1); row.Children.Add(preview); Grid.SetRow(row, 1); body.Children.Add(row);
            open.Content = new Border { Padding = new Thickness(20), Height = 154, CornerRadius = new CornerRadius(12), Child = body, Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"] };
            Content = OverlayPlay(open, covers, card); return;
        }
        var stack = new StackPanel();
        var cover = new ArtworkImage { DisplayIdentity = card.Song?.Id ?? "", Stretch = Presentation == MusicCatalogCardLayout.Banner ? Stretch.Uniform : Stretch.UniformToFill };
        MusicVisuals.Bind(cover, ArtworkImage.AddressProperty, nameof(MusicCatalogCard.Cover), card);
        var frame = new Grid { CornerRadius = new CornerRadius(Presentation == MusicCatalogCardLayout.Artist ? 1000 : 8), Background = MusicVisuals.Brush("#E7EAF0") };
        CardClip.SetRadius(frame, Presentation == MusicCatalogCardLayout.Artist ? 1000 : 8);
        frame.Children.Add(cover); frame.SizeChanged += (_, _) => frame.Height = Presentation == MusicCatalogCardLayout.Banner ? frame.ActualWidth / 2.56 : frame.ActualWidth;
        if (string.IsNullOrWhiteSpace(card.Cover)) { var fallback = CardText(card, nameof(MusicCatalogCard.Title), 18); fallback.TextWrapping = TextWrapping.Wrap; fallback.Margin = new Thickness(10); frame.Children.Add(fallback); }
        stack.Children.Add(frame);
        if (Presentation is not (MusicCatalogCardLayout.Banner or MusicCatalogCardLayout.RankCover))
        {
            var title = CardText(card, nameof(MusicCatalogCard.Title), 13); title.Height = 40; title.Margin = new Thickness(0, 8, 0, 0); title.TextWrapping = TextWrapping.Wrap; stack.Children.Add(title);
            if (Presentation == MusicCatalogCardLayout.Artist) stack.Children.Add(CardText(card, nameof(MusicCatalogCard.Subtitle), 12));
        }
        open.Content = stack;
        var result = OverlayPlay(open, frame, card);
        if (card.CanHeartbeatFavorites)
        { var heart = Action("心动", owner => owner.HeartbeatFavoritesCatalogCardAsync(card)); heart.HorizontalAlignment = HorizontalAlignment.Right; heart.VerticalAlignment = VerticalAlignment.Bottom; result.Children.Add(heart); }
        Content = result;
    }
    private Grid OverlayPlay(Button open, Grid artwork, MusicCatalogCard card)
    {
        var grid = new Grid(); grid.Children.Add(open);
        if (!card.CanPlay) return grid;
        var shade = new Border { IsHitTestVisible = false, Opacity = 0 };
        MeowFilm.Desktop.DesktopVisual.UseResource(shade, Border.BackgroundProperty, "Card.CoverHover");
        artwork.Children.Add(shade);
        var play = Action("播放", owner => owner.PlayCatalogCardAsync(card));
        var glyph = new CardPlayGlyph { Foreground = MusicVisuals.Brush("#FFFFFF") }; MusicVisuals.Bind(glyph, CardPlayGlyph.IsBusyProperty, nameof(MusicCatalogCard.IsPreparing), card); play.Content = glyph;
        play.Width = play.Height = 44; play.HorizontalAlignment = HorizontalAlignment.Center; play.VerticalAlignment = VerticalAlignment.Center;
        play.Opacity = 0; play.IsHitTestVisible = false; artwork.Children.Add(play);
        CardMotion.SetEnabled(grid, true); grid.RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => { shade.Opacity = play.Opacity = CardMotion.GetProgress(grid); play.IsHitTestVisible = play.Opacity > 0; });
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
