using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace MeowFilm.Desktop.Artwork;

/// <summary>The owning App explicitly supplies its loader; no default singleton or URL fetcher.</summary>
public static class ArtworkScope
{
    public static readonly DependencyProperty LoaderProperty = DependencyProperty.RegisterAttached("Loader",
        typeof(IArtworkLoader), typeof(ArtworkScope), new PropertyMetadata(null, Changed));
    public static IArtworkLoader? GetLoader(DependencyObject view)
    {
        for (DependencyObject? current = view; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current.ReadLocalValue(LoaderProperty) != DependencyProperty.UnsetValue) return current.GetValue(LoaderProperty) as IArtworkLoader;
        return null;
    }
    public static void SetLoader(DependencyObject view, IArtworkLoader? value)
    {
        bool explicitNull = value is null && view.ReadLocalValue(LoaderProperty) == DependencyProperty.UnsetValue;
        view.SetValue(LoaderProperty, value);
        // A local null changes owner lookup even when the dependency property's
        // default is already null, so that transition has no Changed callback.
        if (explicitNull) Notify(view);
    }
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs _) => Notify(value);
    private static void Notify(DependencyObject view)
    {
        if (view is ArtworkImage image) image.ScopeChanged();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(view); i++)
        {
            var child = VisualTreeHelper.GetChild(view, i);
            if (child.ReadLocalValue(LoaderProperty) == DependencyProperty.UnsetValue) Notify(child);
        }
    }
}

public sealed class ArtworkLoadEventArgs(string address, bool succeeded) : EventArgs
{
    public string Address { get; } = address;
    public bool Succeeded { get; } = succeeded;
}

public sealed class ArtworkImage : Grid
{
    public event EventHandler<ArtworkLoadEventArgs>? LoadCompleted;
    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(nameof(Address), typeof(string),
        typeof(ArtworkImage), new PropertyMetadata("", (value, _) => ((ArtworkImage)value).Restart()));
    public static readonly DependencyProperty DisplayIdentityProperty = DependencyProperty.Register(nameof(DisplayIdentity), typeof(string),
        typeof(ArtworkImage), new PropertyMetadata("", (value, _) => ((ArtworkImage)value).Restart()));
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(nameof(Stretch), typeof(Stretch),
        typeof(ArtworkImage), new PropertyMetadata(Stretch.UniformToFill, (value, args) => ((ArtworkImage)value).image.Stretch = (Stretch)args.NewValue));
    public static readonly DependencyProperty ShowRetryProperty = DependencyProperty.Register(nameof(ShowRetry), typeof(bool),
        typeof(ArtworkImage), new PropertyMetadata(true, (value, _) => ((ArtworkImage)value).UpdateRetryVisibility()));
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source),
        typeof(BitmapSource), typeof(ArtworkImage), new PropertyMetadata(null,
            (value, args) => ((ArtworkImage)value).image.Source = (BitmapSource?)args.NewValue));
    /// <summary>Borrowed decoded pixels; cleared with the artwork lease, without another acquisition.</summary>
    public BitmapSource? Source => (BitmapSource?)GetValue(SourceProperty);
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(nameof(AspectRatio),
        typeof(double), typeof(ArtworkImage), new PropertyMetadata(0d));
    public double AspectRatio => (double)GetValue(AspectRatioProperty);
    public static readonly DependencyProperty RawPaletteBrushProperty = DependencyProperty.Register(nameof(RawPaletteBrush),
        typeof(Brush), typeof(ArtworkImage), new PropertyMetadata(null));
    public Brush RawPaletteBrush => (Brush)GetValue(RawPaletteBrushProperty);
    public static readonly DependencyProperty PaletteBrushProperty = DependencyProperty.Register(nameof(PaletteBrush),
        typeof(Brush), typeof(ArtworkImage), new PropertyMetadata(null));
    public Brush PaletteBrush => (Brush)GetValue(PaletteBrushProperty);
    public static readonly DependencyProperty TopPaletteBrushProperty = DependencyProperty.Register(nameof(TopPaletteBrush),
        typeof(Brush), typeof(ArtworkImage), new PropertyMetadata(null));
    public Brush TopPaletteBrush => (Brush)GetValue(TopPaletteBrushProperty);
    public static readonly DependencyProperty HasTopPaletteProperty = DependencyProperty.Register(nameof(HasTopPalette),
        typeof(bool), typeof(ArtworkImage), new PropertyMetadata(false));
    public bool HasTopPalette => (bool)GetValue(HasTopPaletteProperty);
    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly Button retry = new() { Content = "重试",
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed, Padding = new Thickness(4), FontWeight = Microsoft.UI.Text.FontWeights.Normal };
    private IArtworkLease? lease;
    private IArtworkLoader? loader;
    private readonly Brush fallback = new SolidColorBrush(Color.FromArgb(255, 105, 105, 105));
    private readonly RectangleGeometry boundsClip = new();
    private readonly List<(FrameworkElement Element, long Token)> visibilitySubscriptions = [];
    private string displayedIdentity = "";
    private CancellationTokenSource? request;
    private Task? requestTask;
    private PresentationLease? presentation;
    private long epoch;
    private bool loaded, attempted, canRetry, observingLayout;
    private XamlRoot? root;

    public string Address { get => (string)GetValue(AddressProperty); set => SetValue(AddressProperty, value); }
    /// <summary>Only a consumer with a stable display identity retains successful pixels during a resource change.</summary>
    public string DisplayIdentity { get => (string)GetValue(DisplayIdentityProperty); set => SetValue(DisplayIdentityProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public bool ShowRetry { get => (bool)GetValue(ShowRetryProperty); set => SetValue(ShowRetryProperty, value); }
    public ArtworkImage()
    {
        if (Application.Current?.Resources.TryGetValue("MeowFilm.Font.Size.Caption", out var fontSize) == true && fontSize is double size)
            retry.FontSize = size;
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        Clip = boundsClip;
        SetValue(RawPaletteBrushProperty, fallback); SetValue(PaletteBrushProperty, fallback); SetValue(TopPaletteBrushProperty, fallback);
        Children.Add(image); Children.Add(retry);
        AutomationProperties.SetName(retry, "重试图片");
        retry.Click += (_, _) => Restart();
        Loaded += (_, _) =>
        {
            loaded = true; root = XamlRoot;
            if (root is not null) root.Changed += RootChanged;
            ObserveVisibility();
            UpdateVisibility();
        };
        Unloaded += (_, _) =>
        {
            loaded = false;
            if (root is not null) root.Changed -= RootChanged; root = null;
            if (observingLayout) { LayoutUpdated -= OnLayout; observingLayout = false; }
            ClearVisibilitySubscriptions();
            var prior = presentation; presentation = null; prior?.Dispose();
            Retire();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateVisibility());
        EffectiveViewportChanged += (_, _) => UpdateVisibility();
        SizeChanged += (_, _) =>
        {
            boundsClip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            UpdateVisibility();
        };
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>Prepare an imminent surface on the UI thread; its owner releases the lease when that presentation retires.</summary>
    public async Task<IDisposable> PrepareForPresentationAsync(CancellationToken cancellationToken = default)
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Artwork preparation requires its UI thread.");
        cancellationToken.ThrowIfCancellationRequested();
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
    private void OnLayout(object? sender, object args) => UpdateVisibility();
    internal void ScopeChanged()
    {
        var current = ArtworkScope.GetLoader(this);
        if (ReferenceEquals(loader, current)) return;
        loader = current;
        Retire();
        UpdateVisibility();
    }
    internal void Restart()
    {
        Retire(lease is not null && DisplayIdentity.Length > 0 && displayedIdentity == DisplayIdentity && Address.Length > 0);
        UpdateVisibility();
    }
    private void Retire(bool retainImage = false)
    {
        epoch++;
        var priorRequest = request; request = null; requestTask = null; attempted = canRetry = false;
        var priorLease = retainImage ? null : lease;
        if (!retainImage)
        {
            lease = null; displayedIdentity = "";
            SetValue(SourceProperty, null); SetValue(AspectRatioProperty, 0d); SetValue(RawPaletteBrushProperty, fallback);
            SetValue(PaletteBrushProperty, fallback);
            SetValue(TopPaletteBrushProperty, fallback);
            SetValue(HasTopPaletteProperty, false);
        }
        ClearValue(ToolTipService.ToolTipProperty); ClearValue(AutomationProperties.HelpTextProperty);
        UpdateRetryVisibility();
        priorLease?.Dispose();
        // Cancellation may synchronously resume preparation; publish retirement before invoking it.
        priorRequest?.Cancel();
    }
    // Colors were sampled once by the cache's immutable raster, not once per control.
    public static (Brush Raw, Brush Palette) SamplePalette(ArtworkPixels source)
    {
        var palettes = source.Palettes;
        return (new SolidColorBrush(palettes.Raw), new SolidColorBrush(palettes.Palette));
    }
    internal static Brush SampleTopPalette(ArtworkPixels source) => new SolidColorBrush(source.Palettes.Top);

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateVisibility();
    private void ObserveVisibility()
    {
        ClearVisibilitySubscriptions();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is FrameworkElement element)
                visibilitySubscriptions.Add((element,
                    element.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateVisibility())));
    }
    private void ClearVisibilitySubscriptions()
    {
        foreach (var (element, token) in visibilitySubscriptions) element.UnregisterPropertyChangedCallback(VisibilityProperty, token);
        visibilitySubscriptions.Clear();
    }
    private bool IsVisibleInTree()
    {
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return XamlRoot?.IsHostVisible != false;
    }

    private bool InViewport()
    {
        if (!loaded || !IsVisibleInTree() || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement element && (element.Clip is not null || element is ScrollContentPresenter or ScrollViewer))
                {
                    Rect bounds = TransformToVisual(element).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
                    var clip = element.Clip?.Rect ?? new Rect(0, 0, element.ActualWidth, element.ActualHeight);
                    bounds.Intersect(clip);
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;
                }
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
    private void UpdateVisibility()
    {
        var currentLoader = ArtworkScope.GetLoader(this);
        if (!ReferenceEquals(loader, currentLoader)) { loader = currentLoader; Retire(); }
        bool visible = IsVisibleInTree();
        bool observe = loaded && visible;
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
                if (request is not null || visible && (attempted || lease is not null)) Retire();
            }
            else if (attempted || request is not null || lease is not null) Retire();
            return;
        }
        if (attempted || string.IsNullOrWhiteSpace(Address) || loader is not { } currentLoaderOwner) return;
        attempted = true; var stamp = epoch; string address = Address;
        var current = new CancellationTokenSource(); request = current;
        var work = ReadAsync();
        // A synchronous completion may select another address through a consumer.
        if (ReferenceEquals(request, current)) requestTask = work;
        async Task ReadAsync()
        {
            IArtworkLease? acquired = null;
            try
            {
                acquired = await currentLoaderOwner.AcquireAsync(address, current.Token);
                if (stamp != epoch || current.IsCancellationRequested || !HasDemand()) return;
                var source = acquired.Image.GetSource(DispatcherQueue);
                var palettes = acquired.Image.Palettes;
                var prior = lease;
                lease = acquired; acquired = null; displayedIdentity = DisplayIdentity;
                SetValue(SourceProperty, source); prior?.Dispose();
                SetValue(AspectRatioProperty, (double)lease.Image.PixelWidth / lease.Image.PixelHeight);
                SetValue(RawPaletteBrushProperty, new SolidColorBrush(palettes.Raw));
                SetValue(PaletteBrushProperty, new SolidColorBrush(palettes.Palette));
                SetValue(TopPaletteBrushProperty, new SolidColorBrush(palettes.Top));
                SetValue(HasTopPaletteProperty, palettes.HasTopPalette);
                LoadCompleted?.Invoke(this, new(address, true));
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (stamp == epoch && !current.IsCancellationRequested && HasDemand())
                {
                    string message;
                    (canRetry, message) = (error as ArtworkException)?.Code switch
                    {
                        "ARTWORK_FORMAT" => (false, "图片格式不支持或已损坏"),
                        "ARTWORK_LIMIT" => (false, "图片超出资源限制"),
                        "ARTWORK_ADDRESS" => (false, "图片地址不可用"),
                        "ARTWORK_REDIRECT" => (false, "图片重定向不可用"),
                        "ARTWORK_DENIED" => (false, "图片访问被拒绝"),
                        "ARTWORK_CLOSED" => (false, "图片服务已关闭"),
                        "ARTWORK_NETWORK" => (true, "图片网络加载失败"),
                        "ARTWORK_TIMEOUT" => (true, "图片加载超时"),
                        _ => (true, "图片加载失败")
                    };
                    ToolTipService.SetToolTip(this, message); AutomationProperties.SetHelpText(this, message);
                    UpdateRetryVisibility();
                    LoadCompleted?.Invoke(this, new(address, false));
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
        retry.Visibility = canRetry && ShowRetry ? Visibility.Visible : Visibility.Collapsed;
}
