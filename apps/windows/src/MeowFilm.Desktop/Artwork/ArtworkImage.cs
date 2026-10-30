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
    private static readonly DependencyPropertyKey RawPaletteBrushPropertyKey = DependencyProperty.RegisterReadOnly(nameof(RawPaletteBrush),
        typeof(Brush), typeof(ArtworkImage), new PropertyMetadata(Brushes.DimGray));
    public static readonly DependencyProperty RawPaletteBrushProperty = RawPaletteBrushPropertyKey.DependencyProperty;
    public Brush RawPaletteBrush => (Brush)GetValue(RawPaletteBrushProperty);
    private static readonly DependencyPropertyKey PaletteBrushPropertyKey = DependencyProperty.RegisterReadOnly(nameof(PaletteBrush),
        typeof(Brush), typeof(ArtworkImage), new PropertyMetadata(Brushes.DimGray));
    public static readonly DependencyProperty PaletteBrushProperty = PaletteBrushPropertyKey.DependencyProperty;
    public Brush PaletteBrush => (Brush)GetValue(PaletteBrushProperty);
    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly Button retry = new() { Content = "重试", ToolTip = "图片加载失败",
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed, Padding = new Thickness(4), FontSize = 11 };
    private IArtworkLease? lease;
    private CancellationTokenSource? request;
    private Task? requestTask;
    private PresentationLease? presentation;
    private long epoch;
    private bool loaded, attempted, observingLayout;

    public string Address { get => (string)GetValue(AddressProperty); set => SetValue(AddressProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public ArtworkImage()
    {
        ClipToBounds = true; Children.Add(image); Children.Add(retry);
        System.Windows.Automation.AutomationProperties.SetName(retry, "重试图片");
        retry.Click += (_, args) => { args.Handled = true; Restart(); };
        Loaded += (_, _) =>
        {
            loaded = true;
            if (!observingLayout) { LayoutUpdated += OnLayout; observingLayout = true; }
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
            prior.presentation = null; prior.UpdateVisibility();
        }
    }
    private bool HasDemand() => presentation is not null || InViewport();
    private void OnLayout(object? sender, EventArgs args) => UpdateVisibility();
    internal void Restart() { Retire(); UpdateVisibility(); }
    private void Retire()
    {
        epoch++;
        var priorRequest = request; request = null; requestTask = null; attempted = false;
        var priorLease = lease; lease = null;
        image.Source = null; SetValue(RawPaletteBrushPropertyKey, Brushes.DimGray);
        SetValue(PaletteBrushPropertyKey, Brushes.DimGray); retry.Visibility = Visibility.Collapsed;
        priorLease?.Dispose();
        // Cancellation may synchronously resume preparation; publish retirement before invoking it.
        priorRequest?.Cancel();
    }
    // Small raster sample of the already acquired cover: no second fetch or full-image copy.
    internal static (Brush Raw, Brush Palette) SamplePalette(BitmapSource source)
    {
        var small = new TransformedBitmap(source, new ScaleTransform(8d / source.PixelWidth, 8d / source.PixelHeight));
        var rgba = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int stride = rgba.PixelWidth * 4;
        var bytes = new byte[stride * rgba.PixelHeight]; rgba.CopyPixels(bytes, stride, 0);
        long r = 0, g = 0, b = 0, weight = 0;
        for (int i = 0; i < bytes.Length; i += 4)
        { int alpha = bytes[i + 3]; b += bytes[i] * alpha; g += bytes[i + 1] * alpha; r += bytes[i + 2] * alpha; weight += alpha; }
        if (weight == 0) return (Brushes.DimGray, Brushes.DimGray);
        double red = (double)r / weight, green = (double)g / weight, blue = (double)b / weight;
        double scale = Math.Min(1, 105 / Math.Max(1, .2126 * red + .7152 * green + .0722 * blue));
        SolidColorBrush Brush(double factor)
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(red * factor), (byte)(green * factor), (byte)(blue * factor)));
            brush.Freeze(); return brush;
        }
        return (Brush(1), Brush(scale));
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
        if (!HasDemand()) { if (attempted || request is not null || lease is not null) Retire(); return; }
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
                var palette = SamplePalette(lease.Image);
                SetValue(RawPaletteBrushPropertyKey, palette.Raw);
                SetValue(PaletteBrushPropertyKey, palette.Palette);
            }
            catch (OperationCanceledException) { }
            catch { if (stamp == epoch && !current.IsCancellationRequested && HasDemand()) retry.Visibility = Visibility.Visible; }
            finally
            {
                acquired?.Dispose();
                if (ReferenceEquals(request, current)) request = null;
                current.Dispose();
            }
        }
    }
}
