using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeowFilm.Desktop.Artwork;

/// <summary>The owning App explicitly supplies its loader; no default singleton or URL fetcher.</summary>
public static class ArtworkScope
{
    public static readonly DependencyProperty LoaderProperty = DependencyProperty.RegisterAttached("Loader",
        typeof(IArtworkLoader), typeof(ArtworkScope), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.Inherits, Changed));
    public static IArtworkLoader? GetLoader(DependencyObject view) => (IArtworkLoader?)view.GetValue(LoaderProperty);
    public static void SetLoader(DependencyObject view, IArtworkLoader? value) => view.SetValue(LoaderProperty, value);
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs _) { if (value is ArtworkImage image) image.Restart(); }
}

public sealed class ArtworkImage : Grid
{
    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(nameof(Address), typeof(string),
        typeof(ArtworkImage), new PropertyMetadata("", (value, _) => ((ArtworkImage)value).Restart()));
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(nameof(Stretch), typeof(Stretch),
        typeof(ArtworkImage), new PropertyMetadata(Stretch.UniformToFill, (value, args) => ((ArtworkImage)value).image.Stretch = (Stretch)args.NewValue));
    public static readonly DependencyProperty ShowRetryProperty = DependencyProperty.Register(nameof(ShowRetry), typeof(bool),
        typeof(ArtworkImage), new PropertyMetadata(true, (value, _) => ((ArtworkImage)value).UpdateRetryVisibility()));
    private static readonly DependencyPropertyKey RawPaletteBrushPropertyKey = DependencyProperty.RegisterReadOnly(nameof(RawPaletteBrush),
        typeof(Brush), typeof(ArtworkImage), new PropertyMetadata(Brushes.DimGray));
    public static readonly DependencyProperty RawPaletteBrushProperty = RawPaletteBrushPropertyKey.DependencyProperty;
    public Brush RawPaletteBrush => (Brush)GetValue(RawPaletteBrushProperty);
    private static readonly DependencyPropertyKey PaletteBrushPropertyKey = DependencyProperty.RegisterReadOnly(nameof(PaletteBrush),
        typeof(Brush), typeof(ArtworkImage), new PropertyMetadata(Brushes.DimGray));
    public static readonly DependencyProperty PaletteBrushProperty = PaletteBrushPropertyKey.DependencyProperty;
    public Brush PaletteBrush => (Brush)GetValue(PaletteBrushProperty);
    private static readonly DependencyPropertyKey TopPaletteBrushPropertyKey = DependencyProperty.RegisterReadOnly(nameof(TopPaletteBrush),
        typeof(Brush), typeof(ArtworkImage), new PropertyMetadata(Brushes.DimGray));
    public static readonly DependencyProperty TopPaletteBrushProperty = TopPaletteBrushPropertyKey.DependencyProperty;
    public Brush TopPaletteBrush => (Brush)GetValue(TopPaletteBrushProperty);
    private static readonly DependencyPropertyKey HasTopPalettePropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasTopPalette),
        typeof(bool), typeof(ArtworkImage), new PropertyMetadata(false));
    public static readonly DependencyProperty HasTopPaletteProperty = HasTopPalettePropertyKey.DependencyProperty;
    public bool HasTopPalette => (bool)GetValue(HasTopPaletteProperty);
    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly Button retry = new() { Content = "重试",
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed, Padding = new Thickness(4), FontSize = 11 };
    private IArtworkLease? lease;
    private CancellationTokenSource? request;
    private Task? requestTask;
    private PresentationLease? presentation;
    private long epoch;
    private bool loaded, attempted, failed, observingLayout;

    public string Address { get => (string)GetValue(AddressProperty); set => SetValue(AddressProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public bool ShowRetry { get => (bool)GetValue(ShowRetryProperty); set => SetValue(ShowRetryProperty, value); }
    public ArtworkImage()
    {
        ClipToBounds = true; Children.Add(image); Children.Add(retry);
        System.Windows.Automation.AutomationProperties.SetName(retry, "重试图片");
        retry.Click += (_, args) => { args.Handled = true; Restart(); };
        Loaded += (_, _) =>
        {
            loaded = true;
            UpdateVisibility();
        };
        Unloaded += (_, _) =>
        {
            loaded = false;
            if (observingLayout) { LayoutUpdated -= OnLayout; observingLayout = false; }
            var prior = presentation; presentation = null; prior?.Dispose();
            Retire();
        };
        IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    /// <summary>Prepare an imminent surface on the UI thread; its owner releases the lease when that presentation retires.</summary>
    public async Task<IDisposable> PrepareForPresentationAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess(); cancellationToken.ThrowIfCancellationRequested();
        var prior = presentation;
        var current = new PresentationLease(this); presentation = current; prior?.Dispose();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(presentation, current)) throw new OperationCanceledException();
                var stamp = epoch;
                UpdateVisibility();
                var pending = requestTask ?? Task.CompletedTask;
                if (request is { } reading)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, reading.Token);
                    try { await pending.WaitAsync(wait.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && stamp != epoch) { }
                }
                else await pending.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(presentation, current)) throw new OperationCanceledException();
                if (stamp == epoch) return current;
            }
        }
        catch { current.Dispose(); throw; }
    }
    private sealed class PresentationLease(ArtworkImage image) : IDisposable
    {
        private ArtworkImage? owner = image;
        public void Dispose()
        {
            var prior = owner; owner = null;
            if (prior is null || !ReferenceEquals(prior.presentation, this)) return;
            prior.presentation = null;
            if (!prior.InViewport()) prior.Retire();
            prior.UpdateVisibility();
        }
    }
    private bool HasDemand() => presentation is not null || InViewport();
    private void OnLayout(object? sender, EventArgs args) => UpdateVisibility();
    internal void Restart() { Retire(); UpdateVisibility(); }
    private void Retire()
    {
        epoch++;
        var priorRequest = request; request = null; requestTask = null; attempted = failed = false;
        var priorLease = lease; lease = null;
        image.Source = null; SetValue(RawPaletteBrushPropertyKey, Brushes.DimGray);
        SetValue(PaletteBrushPropertyKey, Brushes.DimGray);
        SetValue(TopPaletteBrushPropertyKey, Brushes.DimGray);
        SetValue(HasTopPalettePropertyKey, false);
        UpdateRetryVisibility();
        priorLease?.Dispose();
        // Cancellation may synchronously resume preparation; publish retirement before invoking it.
        priorRequest?.Cancel();
    }
    // Small raster sample of the already acquired cover: no second fetch or full-image copy.
    public static (Brush Raw, Brush Palette) SamplePalette(BitmapSource source)
    {
        var palettes = SamplePalettes(source);
        return (palettes.Raw, palettes.Palette);
    }
    internal static Brush SampleTopPalette(BitmapSource source) => SamplePalettes(source).Top;
    private static (Brush Raw, Brush Palette, Brush Top, bool HasTopPalette) SamplePalettes(BitmapSource source)
    {
        var small = new TransformedBitmap(source, new ScaleTransform(8d / source.PixelWidth, 8d / source.PixelHeight));
        var rgba = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int stride = rgba.PixelWidth * 4;
        var bytes = new byte[stride * rgba.PixelHeight]; rgba.CopyPixels(bytes, stride, 0);
        static (double Red, double Green, double Blue, long Weight, int Count) Average(
            byte[] pixels, int pixelWidth, int firstRow, int rowCount)
        {
            long r = 0, g = 0, b = 0, weight = 0;
            int first = firstRow * pixelWidth * 4;
            int last = Math.Min(pixels.Length, (firstRow + rowCount) * pixelWidth * 4);
            for (int i = first; i < last; i += 4)
            {
                int alpha = pixels[i + 3];
                b += pixels[i] * alpha; g += pixels[i + 1] * alpha;
                r += pixels[i + 2] * alpha; weight += alpha;
            }
            var count = Math.Max(0, (last - first) / 4);
            return weight == 0 ? (0, 0, 0, 0, count) :
                ((double)r / weight, (double)g / weight, (double)b / weight, weight, count);
        }
        static Brush Brush(double red, double green, double blue, double factor = 1, byte alpha = byte.MaxValue)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(red * factor),
                (byte)(green * factor), (byte)(blue * factor)));
            brush.Freeze(); return brush;
        }
        var all = Average(bytes, rgba.PixelWidth, 0, rgba.PixelHeight);
        if (all.Weight == 0) return (Brushes.DimGray, Brushes.DimGray, Brushes.DimGray, false);
        double scale = Math.Min(1, 105 / Math.Max(1, .2126 * all.Red + .7152 * all.Green + .0722 * all.Blue));
        var top = Average(bytes, rgba.PixelWidth, 0, Math.Max(1, (int)Math.Ceiling(rgba.PixelHeight * .375)));
        var topAlpha = top.Count == 0 ? (byte)0 : (byte)Math.Clamp(
            (int)Math.Round((double)top.Weight / top.Count, MidpointRounding.AwayFromZero), 0, 255);
        return (Brush(all.Red, all.Green, all.Blue), Brush(all.Red, all.Green, all.Blue, scale),
            top.Weight == 0 ? Brushes.DimGray : Brush(top.Red, top.Green, top.Blue, alpha: topAlpha),
            top.Weight > 0);
    }

    private bool InViewport()
    {
        if (!loaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter))
                {
                    Rect bounds = TransformToAncestor(element).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
                    if (!bounds.IntersectsWith(new Rect(0, 0, element.ActualWidth, element.ActualHeight))) return false;
                }
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
    private void UpdateVisibility()
    {
        bool observe = loaded && IsVisible;
        if (observe != observingLayout)
        {
            if (observe) LayoutUpdated += OnLayout;
            else LayoutUpdated -= OnLayout;
            observingLayout = observe;
        }
        if (!HasDemand())
        {
            // Retained views own successful pixels, including while layout is restored after showing.
            // Actual Unloaded/recycling and explicit presentation retirement release those resources.
            if (loaded)
            {
                if (request is not null || IsVisible && (attempted || lease is not null)) Retire();
            }
            else if (attempted || request is not null || lease is not null) Retire();
            return;
        }
        if (attempted || string.IsNullOrWhiteSpace(Address) || ArtworkScope.GetLoader(this) is not { } loader) return;
        attempted = true; var stamp = epoch; string address = Address;
        var current = new CancellationTokenSource(); request = current;
        requestTask = ReadAsync();
        async Task ReadAsync()
        {
            IArtworkLease? acquired = null;
            try
            {
                acquired = await loader.AcquireAsync(address, current.Token);
                if (stamp != epoch || current.IsCancellationRequested || !HasDemand()) return;
                lease = acquired; acquired = null; image.Source = lease.Image;
                var palettes = SamplePalettes(lease.Image);
                SetValue(RawPaletteBrushPropertyKey, palettes.Raw);
                SetValue(PaletteBrushPropertyKey, palettes.Palette);
                SetValue(TopPaletteBrushPropertyKey, palettes.Top);
                SetValue(HasTopPalettePropertyKey, palettes.HasTopPalette);
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (stamp == epoch && !current.IsCancellationRequested && HasDemand())
                {
                    failed = true;
                    UpdateRetryVisibility();
                }
            }
            finally
            {
                acquired?.Dispose();
                if (ReferenceEquals(request, current)) request = null;
                current.Dispose();
            }
        }
    }
    private void UpdateRetryVisibility() =>
        retry.Visibility = failed && ShowRetry ? Visibility.Visible : Visibility.Collapsed;
}
