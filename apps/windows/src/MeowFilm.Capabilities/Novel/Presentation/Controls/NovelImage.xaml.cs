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

public sealed partial class NovelImage : Grid, IDebugImageVisual
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
        new PropertyMetadata(Stretch.UniformToFill, (d, args) => ((NovelImage)d).CoverImage.Stretch = (Stretch)args.NewValue));

    public static readonly DependencyProperty ShowPlaceholderProperty = DependencyProperty.Register(
        nameof(ShowPlaceholder), typeof(bool), typeof(NovelImage),
        new PropertyMetadata(false, (d, _) => ((NovelImage)d).UpdatePlaceholder()));

    private readonly DesktopViewport viewport;
    private NovelSourceService? currentSources;
    private NovelBook? currentBook;
    private (string Source, long Revision, string Url, string Cover)? currentIdentity;
    private CancellationTokenSource? request;
    private bool loaded, attempted, canRetry;
    private int pixelWidth, pixelHeight;
    private string? errorCode;

    public NovelSourceService? Sources { get => (NovelSourceService?)GetValue(SourcesProperty); set => SetValue(SourcesProperty, value); }
    public NovelBook? Book { get => (NovelBook?)GetValue(BookProperty); set => SetValue(BookProperty, value); }
    public string Slot { get => (string)GetValue(SlotProperty); set => SetValue(SlotProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public bool ShowPlaceholder { get => (bool)GetValue(ShowPlaceholderProperty); set => SetValue(ShowPlaceholderProperty, value); }
    public ImageSource? Source { get => CoverImage.Source; private set => CoverImage.Source = value; }

    public NovelImage()
    {
        InitializeComponent();
        viewport = new DesktopViewport(this, UpdateVisibility, active: false);
        Loaded += (_, _) => { loaded = true; UpdateVisibility(); };
        Unloaded += (_, _) => { if (IsLoaded) return; loaded = attempted = false; viewport.Reset(active: false); Stop(); };
    }

    private bool WantsViewport => loaded && currentSources is not null && currentBook is { CoverUrl.Length: > 0 };

    internal static (string Source, long Revision, string Url, string Cover) CoverIdentity(NovelBook book) =>
        (book.InstallationId, book.Revision, book.BookUrl, book.CoverUrl);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    private bool HasDemand => loaded && viewport.Admitted && viewport.IsVisible;

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
        viewport.Reset(WantsViewport);
        UpdateVisibility();
    }

    private void UpdatePlaceholder()
    {
        bool show = ShowPlaceholder && (Book is null || Book.CoverUrl.Length == 0) && Source is null;
        PlaceholderSurface.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateVisibility()
    {
        viewport.Sync(WantsViewport);
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
        if (sources.TryGetCachedCover(book, slot, out var cached))
        {
            pixelWidth = cached.PixelWidth;
            pixelHeight = cached.PixelHeight;
            Source = cached.CreateSource();
            UpdatePlaceholder();
            return;
        }
        var owner = request = new();
        _ = Load();
        async Task Load()
        {
            try
            {
                var bitmap = await sources.LoadCoverAsync(book, slot, owner.Token);
                if (!ReferenceEquals(request, owner) || !HasDemand) return;
                pixelWidth = bitmap.PixelWidth;
                pixelHeight = bitmap.PixelHeight;
                Source = bitmap.CreateSource();
                UpdatePlaceholder();
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
            : viewport.Admitted ? "pending"
            : "deferred";
        return new DebugImageSnapshot(
            State: state,
            Address: address,
            Slot: string.IsNullOrWhiteSpace(Slot) ? "cover" : Slot,
            Admitted: viewport.Admitted,
            HasSource: Source is not null,
            PixelWidth: pixelWidth,
            PixelHeight: pixelHeight,
            CanRetry: canRetry,
            ErrorCode: errorCode);
    }
}
