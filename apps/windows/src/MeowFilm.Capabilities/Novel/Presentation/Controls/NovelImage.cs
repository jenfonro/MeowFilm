using System.IO;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public sealed class NovelImage : Grid, IDebugImageVisual
{
    public static readonly DependencyProperty SourcesProperty = DependencyProperty.Register(
        nameof(Sources), typeof(NovelSourceService), typeof(NovelImage),
        new PropertyMetadata(null, (d, _) => ((NovelImage)d).OnTargetChanged()));

    public static readonly DependencyProperty BookProperty = DependencyProperty.Register(
        nameof(Book), typeof(NovelBook), typeof(NovelImage),
        new PropertyMetadata(null, (d, _) => ((NovelImage)d).OnTargetChanged()));

    public static readonly DependencyProperty SlotProperty = DependencyProperty.Register(
        nameof(Slot), typeof(string), typeof(NovelImage),
        new PropertyMetadata("cover"));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(Stretch), typeof(NovelImage),
        new PropertyMetadata(Stretch.UniformToFill, (d, args) => ((NovelImage)d).image.Stretch = (Stretch)args.NewValue));

    public static readonly DependencyProperty ShowPlaceholderProperty = DependencyProperty.Register(
        nameof(ShowPlaceholder), typeof(bool), typeof(NovelImage),
        new PropertyMetadata(false, (d, _) => ((NovelImage)d).UpdatePlaceholder()));

    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly Border placeholderSurface = new() { Visibility = Visibility.Collapsed };
    private readonly DesktopViewport viewport;
    private NovelSourceService? currentSources;
    private NovelBook? currentBook;
    private (string Source, long Revision, string Url, string Cover)? currentIdentity;
    private CancellationTokenSource? request;
    private bool loaded, admitted, attempted, canRetry;
    private int pixelWidth, pixelHeight;
    private string? errorCode;

    public NovelSourceService? Sources { get => (NovelSourceService?)GetValue(SourcesProperty); set => SetValue(SourcesProperty, value); }
    public NovelBook? Book { get => (NovelBook?)GetValue(BookProperty); set => SetValue(BookProperty, value); }
    public string Slot { get => (string)GetValue(SlotProperty); set => SetValue(SlotProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public bool ShowPlaceholder { get => (bool)GetValue(ShowPlaceholderProperty); set => SetValue(ShowPlaceholderProperty, value); }
    public ImageSource? Source { get => image.Source; private set => image.Source = value; }

    public NovelImage()
    {
        var label = new TextBlock
        {
            Text = "暂无封面",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.UseResource(StyleProperty, "Capability.Muted");
        placeholderSurface.UseResource(StyleProperty, "Novel.Discovery.Surface");
        placeholderSurface.Child = label;
        Children.Add(placeholderSurface);
        Children.Add(image);

        viewport = new DesktopViewport(this, UpdateVisibility);
        Loaded += (_, _) => { loaded = true; UpdateVisibility(); };
        Unloaded += (_, _) => { if (IsLoaded) return; loaded = admitted = attempted = false; Stop(); };
    }

    internal static (string Source, long Revision, string Url, string Cover) CoverIdentity(NovelBook book) =>
        (book.InstallationId, book.Revision, book.BookUrl, book.CoverUrl);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    private bool HasDemand => loaded && admitted && viewport.IsVisible;

    private void OnTargetChanged()
    {
        var nextSources = Sources;
        var nextBook = Book;
        if (nextBook is not null)
            AutomationProperties.SetName(this, nextBook.Name + "封面");
        else
            ClearValue(AutomationProperties.NameProperty);

        var nextIdentity = nextSources is not null && nextBook is not null
            ? ((string, long, string, string)?)CoverIdentity(nextBook)
            : null;

        UpdatePlaceholder();
        if (ReferenceEquals(currentSources, nextSources) && currentIdentity == nextIdentity)
        {
            currentBook = nextBook;
            return;
        }

        currentSources = nextSources;
        currentBook = nextBook;
        currentIdentity = nextIdentity;
        attempted = false;
        errorCode = null;
        canRetry = false;
        Stop();
        UpdateVisibility();
    }

    private void UpdatePlaceholder()
    {
        bool show = ShowPlaceholder && (Book is null || Book.CoverUrl.Length == 0) && Source is null;
        placeholderSurface.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateVisibility()
    {
        if (viewport?.IsInViewport == true) admitted = true;
        if (HasDemand) Start();
        else if (Source is null && request is not null) { Stop(); attempted = false; }
    }

    private void Start()
    {
        if (attempted || currentSources is not { } sources || currentBook is not { } book || book.CoverUrl.Length == 0) return;
        attempted = true;
        errorCode = null;
        canRetry = false;
        string slot = string.IsNullOrWhiteSpace(Slot) ? "cover" : Slot;
        var owner = request = new();
        _ = Load();
        async Task Load()
        {
            try
            {
                await sources.VisualAsync(book, async ct =>
                {
                    string? reference = null;
                    try
                    {
                        var source = await sources.RequireAsync(book, ct);
                        reference = await sources.ImageAsync(source, book.CoverUrl, book.BookUrl, slot, ct);
                        var bytes = await sources.ReadImageAsync(reference, ct);
                        var bitmap = await Task.Run(() => NovelImagePixels.Decode(bytes, 240), ct);
                        ct.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(request, owner) || !HasDemand) return;
                        pixelWidth = bitmap.PixelWidth;
                        pixelHeight = bitmap.PixelHeight;
                        Source = bitmap.CreateSource();
                        UpdatePlaceholder();
                        // The mounted page retains an admitted cover across scrolling.
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    finally { if (reference is not null) await sources.ReleaseImageAsync(reference); }
                }, owner.Token);
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(request, owner))
                {
                    Source = null;
                    pixelWidth = pixelHeight = 0;
                    UpdatePlaceholder();
                }
            }
            catch (Exception error)
            {
                if (ReferenceEquals(request, owner))
                {
                    Source = null;
                    pixelWidth = pixelHeight = 0;
                    (errorCode, canRetry) = ClassifyError(error);
                    UpdatePlaceholder();
                }
            }
            finally
            {
                if (ReferenceEquals(request, owner)) request = null;
                owner.Dispose();
            }
        }
    }

    private static (string Code, bool Retryable) ClassifyError(Exception error) => error switch
    {
        BridgeException bridge => (bridge.Code, bridge.Code is not ("SOURCE_DISABLED" or "STALE_SOURCE" or "INVALID_REQUEST" or "CLOSED")),
        InvalidDataException data => (string.IsNullOrWhiteSpace(data.Message) ? "IMAGE_DECODE_FAILED" : data.Message, false),
        _ => ("IMAGE_LOAD_FAILED", true)
    };

    private void Stop()
    {
        var old = request;
        request = null;
        old?.Cancel();
        Source = null;
        pixelWidth = pixelHeight = 0;
        UpdatePlaceholder();
    }

    public DebugImageSnapshot DescribeDebugImage()
    {
        string address = currentBook?.CoverUrl ?? Book?.CoverUrl ?? "";
        string state = string.IsNullOrEmpty(address) ? "empty"
            : Source is not null ? "loaded"
            : request is not null ? "loading"
            : errorCode is not null ? "failed"
            : admitted ? "pending"
            : "deferred";
        return new DebugImageSnapshot(
            State: state,
            Address: address,
            Slot: string.IsNullOrWhiteSpace(Slot) ? "cover" : Slot,
            Admitted: admitted,
            HasSource: Source is not null,
            PixelWidth: pixelWidth,
            PixelHeight: pixelHeight,
            CanRetry: canRetry,
            ErrorCode: errorCode);
    }
}
