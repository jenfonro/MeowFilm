using MeowFilm.Core.Debug;
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
        typeof(ArtworkService), typeof(ArtworkScope), new PropertyMetadata(null, Changed));
    public static ArtworkService? GetLoader(DependencyObject view)
    {
        for (DependencyObject? current = view; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current.ReadLocalValue(LoaderProperty) != DependencyProperty.UnsetValue) return current.GetValue(LoaderProperty) as ArtworkService;
        return null;
    }
    public static void SetLoader(DependencyObject view, ArtworkService? value)
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
        ArtworkItemsScope.LoaderChanged(view);
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

public sealed class ArtworkImage : Grid, IDebugImageVisual
{
    public event EventHandler<ArtworkLoadEventArgs>? LoadCompleted;
    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(nameof(Address), typeof(string),
        typeof(ArtworkImage), new PropertyMetadata("", (value, _) => ((ArtworkImage)value).Restart()));
    public static readonly DependencyProperty DisplayIdentityProperty = DependencyProperty.Register(nameof(DisplayIdentity), typeof(string),
        typeof(ArtworkImage), new PropertyMetadata("", (value, _) => ((ArtworkImage)value).Restart()));
    public static readonly DependencyProperty ResourceSlotProperty = DependencyProperty.Register(nameof(ResourceSlot), typeof(string),
        typeof(ArtworkImage), new PropertyMetadata("cover", (value, _) => ((ArtworkImage)value).Restart()));
    public string ResourceSlot { get => (string)GetValue(ResourceSlotProperty); set => SetValue(ResourceSlotProperty, value); }
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
    private Button? retry;
    [ThreadStatic] private static Brush? sharedFallback;
    private static Brush FallbackBrush => sharedFallback ??= new SolidColorBrush(Color.FromArgb(255, 105, 105, 105));
    private static Brush TransparentBrush => DesktopVisual.TransparentBrush;
    private IArtworkLease? lease;
    private ArtworkService? loader;
    private ArtworkItemsScope.Binding? itemsBinding;
    private readonly RectangleGeometry boundsClip = new();
    private readonly DesktopViewport viewport;
    private string displayedIdentity = "";
    private CancellationTokenSource? request;
    private Task? requestTask;
    private PresentationLease? presentation;
    private long epoch;
    private bool loaded, attempted, canRetry, nativeRecycled;
    private string debugState = "idle";
    private string? lastErrorCode;

    public string Address { get => (string)GetValue(AddressProperty); set => SetValue(AddressProperty, value); }
    /// <summary>Only a consumer with a stable display identity retains successful pixels during a resource change.</summary>
    public string DisplayIdentity { get => (string)GetValue(DisplayIdentityProperty); set => SetValue(DisplayIdentityProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public bool ShowRetry { get => (bool)GetValue(ShowRetryProperty); set => SetValue(ShowRetryProperty, value); }
    public ArtworkImage()
    {
        Background = TransparentBrush;
        Clip = boundsClip;
        var fallback = FallbackBrush;
        SetValue(RawPaletteBrushProperty, fallback); SetValue(PaletteBrushProperty, fallback); SetValue(TopPaletteBrushProperty, fallback);
        Children.Add(image);
        viewport = new DesktopViewport(this, UpdateVisibility, active: false);
        Loaded += (_, _) => MountOrRebind();
        Unloaded += (_, _) =>
        {
            if (IsLoaded) { MountOrRebind(); return; }
            loaded = false; itemsBinding?.Recycle();
            viewport.Reset(active: false);
            var prior = presentation; presentation = null; prior?.Dispose();
            Retire(); ReplaceItemsBinding(null);
        };
        DataContextChanged += (_, _) =>
        {
            if (!loaded || itemsBinding?.Current != false) return;
            RefreshItemsBinding(); UpdateVisibility();
        };
        SizeChanged += (_, _) =>
        {
            boundsClip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            if (lease is { } current && DispatcherQueue.HasThreadAccess)
            {
                var next = current.Image.GetSource(DispatcherQueue, GetRenderedMaxPixels());
                if (!ReferenceEquals(Source, next)) SetValue(SourceProperty, next);
            }
        };
    }
    private bool WantsViewport => !nativeRecycled && loaded && !string.IsNullOrWhiteSpace(Address);
    private void ReplaceItemsBinding(ArtworkItemsScope.Binding? next)
    {
        if (ReferenceEquals(itemsBinding, next)) return;
        itemsBinding?.Detach();
        itemsBinding = next;
    }
    private void MountOrRebind()
    {
        loaded = true; nativeRecycled = false;
        var currentLoader = ArtworkScope.GetLoader(this);
        if (itemsBinding is not { Current: true }) ReplaceItemsBinding(ArtworkItemsScope.Find(this));
        if (!ReferenceEquals(loader, currentLoader))
        {
            if (itemsBinding?.MatchesResource(Address, DisplayIdentity, ResourceSlot) != false)
                itemsBinding?.ChangeResource(currentLoader, Address, DisplayIdentity, ResourceSlot);
            loader = currentLoader;
            viewport.Reset(active: false);
            Retire();
        }
        UpdateVisibility();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>Prepare an imminent surface on the UI thread; its owner releases the lease when that presentation retires.</summary>
    public async Task<IDisposable> PrepareForPresentationAsync(CancellationToken cancellationToken = default)
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Artwork preparation requires its UI thread.");
        cancellationToken.ThrowIfCancellationRequested();
        ScopeChanged();
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
            if (!DesktopViewport.IsInViewport(prior)) { prior.viewport.Reset(prior.WantsViewport); prior.Retire(); }
            prior.UpdateVisibility();
        }
    }
    private bool HasDemand() => presentation is not null || loaded && viewport.Admitted && viewport.IsVisible;
    internal void ScopeChanged()
    {
        var current = ArtworkScope.GetLoader(this);
        if (ReferenceEquals(loader, current)) return;
        if (itemsBinding?.MatchesResource(Address, DisplayIdentity, ResourceSlot) != false)
            itemsBinding?.ChangeResource(current, Address, DisplayIdentity, ResourceSlot);
        loader = current;
        if (loaded) ReplaceItemsBinding(ArtworkItemsScope.Find(this));
        viewport.Reset(WantsViewport);
        Retire();
        UpdateVisibility();
    }
    internal void Restart()
    {
        RefreshItemsBinding();
        bool matches = itemsBinding?.MatchesResource(Address, DisplayIdentity, ResourceSlot) != false;
        if (matches) itemsBinding?.ChangeResource(loader, Address, DisplayIdentity, ResourceSlot);
        viewport.Reset(WantsViewport);
        Retire(lease is not null && (matches
            ? DisplayIdentity.Length > 0 && displayedIdentity == DisplayIdentity && Address.Length > 0
            : itemsBinding?.RetainsIdentity(displayedIdentity, ResourceSlot) == true));
        UpdateVisibility();
    }
    internal void NativeItemChanged(bool recycled)
    {
        nativeRecycled = recycled;
        if (recycled)
        {
            itemsBinding?.Recycle(); viewport.Reset(active: false); Retire();
            return;
        }
        RefreshItemsBinding();
        UpdateVisibility();
    }
    private void RefreshItemsBinding()
    {
        if (!loaded || itemsBinding?.Current != false) return;
        var prior = itemsBinding;
        var current = ArtworkItemsScope.Find(this);
        bool retainImage = lease is not null && current is not null && prior.SameItem(current) &&
            current.RetainsIdentity(displayedIdentity, ResourceSlot);
        prior.Recycle(); Retire(retainImage); ReplaceItemsBinding(current);
    }
    private void Retire(bool retainImage = false)
    {
        epoch++;
        var priorRequest = request; request = null; requestTask = null; attempted = canRetry = false;
        lastErrorCode = null;
        var priorLease = retainImage ? null : lease;
        if (!retainImage)
        {
            lease = null; displayedIdentity = "";
            debugState = "idle";
            var fallback = FallbackBrush;
            SetValue(SourceProperty, null); SetValue(AspectRatioProperty, 0d); SetValue(RawPaletteBrushProperty, fallback);
            SetValue(PaletteBrushProperty, fallback);
            SetValue(TopPaletteBrushProperty, fallback);
            SetValue(HasTopPaletteProperty, false);
        }
        else if (lease is not null)
        {
            debugState = "loaded";
        }
        ClearValue(AutomationProperties.HelpTextProperty);
        UpdateRetryVisibility();
        priorLease?.Dispose();
        // Cancellation may synchronously resume preparation; publish retirement before invoking it.
        priorRequest?.Cancel();
    }

    private void UpdateVisibility()
    {
        if (nativeRecycled) return;
        if (!loaded && IsLoaded)
        {
            loaded = true;
            ReplaceItemsBinding(ArtworkItemsScope.Find(this));
            loader ??= ArtworkScope.GetLoader(this);
        }
        RefreshItemsBinding();
        viewport.Sync(WantsViewport);
        if (!HasDemand())
        {
            // A mounted, previously viewed slot keeps its result across scrolling and hiding.
            // Unloaded/recycling, identity changes and presentation retirement own release.
            if (loaded)
            {
                if (request is not null || viewport.IsVisible && (attempted || lease is not null)) Retire();
            }
            else if (attempted || request is not null || lease is not null) Retire();
            return;
        }
        if (attempted || string.IsNullOrWhiteSpace(Address) || loader is not { } currentLoaderOwner ||
            itemsBinding?.MatchesResource(Address, DisplayIdentity, ResourceSlot) == false) return;
        if (lease is null && itemsBinding?.BorrowPrevious(Address, DisplayIdentity, ResourceSlot) is { } previous)
            Present(previous);
        attempted = true; var stamp = epoch; string address = Address;
        lastErrorCode = null;
        if (lease is null) debugState = "loading";
        var current = new CancellationTokenSource(); request = current;
        var work = ReadAsync();
        // A synchronous completion may select another address through a consumer.
        if (ReferenceEquals(request, current)) requestTask = work;
        async Task ReadAsync()
        {
            IArtworkLease? acquired = null;
            try
            {
                acquired = itemsBinding is { } binding
                    ? await binding.AcquireAsync(currentLoaderOwner, address, DisplayIdentity, ResourceSlot, current.Token)
                    : await currentLoaderOwner.AcquireAsync(address, current.Token);
                if (stamp != epoch || current.IsCancellationRequested || !HasDemand()) return;
                Present(acquired); acquired = null;
                LoadCompleted?.Invoke(this, new(address, true));
            }
            catch (OperationCanceledException)
            {
                if (stamp == epoch && lease is null) debugState = "idle";
            }
            catch (Exception error)
            {
                if (stamp == epoch && !current.IsCancellationRequested && HasDemand())
                {
                    debugState = "failed";
                    lastErrorCode = (error as ArtworkException)?.Code ?? error.GetType().Name;
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
                    AutomationProperties.SetHelpText(this, message);
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
    private double GetRenderedMaxPixels()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || XamlRoot is not { } xamlRoot) return 0;
        double width = ActualWidth, height = ActualHeight;
        DependencyObject? child = this;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this);
             parent is not null;
             child = parent, parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Viewbox vb && child is FrameworkElement fe &&
                vb.ActualWidth > 0 && vb.ActualHeight > 0 && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                double sx = vb.ActualWidth / fe.ActualWidth;
                double sy = vb.ActualHeight / fe.ActualHeight;
                double factor = vb.Stretch switch
                {
                    Stretch.Fill => 1d,
                    Stretch.UniformToFill => Math.Max(sx, sy),
                    Stretch.None => 1d,
                    _ => Math.Min(sx, sy)
                };
                if (vb.Stretch == Stretch.Fill) { width *= sx; height *= sy; }
                else { width *= factor; height *= factor; }
            }
        }
        return Math.Max(width, height) * xamlRoot.RasterizationScale;
    }

    private void Present(IArtworkLease acquired)
    {
        var source = acquired.Image.GetSource(DispatcherQueue, GetRenderedMaxPixels());
        var palettes = acquired.Image.Palettes;
        var prior = lease;
        lease = acquired; displayedIdentity = DisplayIdentity;
        debugState = "loaded"; lastErrorCode = null;
        SetValue(SourceProperty, source); prior?.Dispose();
        SetValue(AspectRatioProperty, (double)lease.Image.PixelWidth / lease.Image.PixelHeight);
        var brushes = lease.Image.GetPaletteBrushes(DispatcherQueue);
        SetValue(RawPaletteBrushProperty, brushes.Raw);
        SetValue(PaletteBrushProperty, brushes.Palette);
        SetValue(TopPaletteBrushProperty, brushes.Top);
        SetValue(HasTopPaletteProperty, palettes.HasTopPalette);
    }
    public DebugImageSnapshot DescribeDebugImage()
    {
        var src = Source;
        int pixelWidth = lease?.Image.PixelWidth ?? src?.PixelWidth ?? 0;
        int pixelHeight = lease?.Image.PixelHeight ?? src?.PixelHeight ?? 0;
        string state = debugState;
        if (src is not null && pixelWidth > 0 && pixelHeight > 0 && state == "idle")
            state = "loaded";
        else if (request is not null && src is null && state == "idle")
            state = "loading";
        return new DebugImageSnapshot(
            State: state,
            Address: Address ?? "",
            Slot: string.IsNullOrWhiteSpace(ResourceSlot) ? null : ResourceSlot,
            Admitted: viewport.Admitted,
            HasSource: src is not null,
            PixelWidth: pixelWidth,
            PixelHeight: pixelHeight,
            CanRetry: canRetry,
            ErrorCode: lastErrorCode);
    }
    private void UpdateRetryVisibility()
    {
        bool show = canRetry && ShowRetry;
        if (!show)
        {
            if (retry is not null) retry.Visibility = Visibility.Collapsed;
            return;
        }
        if (retry is null)
        {
            retry = new Button
            {
                Content = "重试",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4),
                FontWeight = Microsoft.UI.Text.FontWeights.Normal
            };
            if (Application.Current?.Resources.TryGetValue("MeowFilm.Font.Size.Caption", out var fontSize) == true && fontSize is double size)
                retry.FontSize = size;
            AutomationProperties.SetName(retry, "重试图片");
            retry.Click += (_, _) => { itemsBinding?.Retry(ResourceSlot); Restart(); };
            Children.Add(retry);
        }
        retry.Visibility = Visibility.Visible;
    }
}
