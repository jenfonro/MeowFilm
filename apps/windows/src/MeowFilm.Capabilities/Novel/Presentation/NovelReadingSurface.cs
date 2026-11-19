using System.IO;
using System.Globalization;
using SkiaSharp;
using System.Windows;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using System.Windows.Media;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using MeowFilm.Desktop;
using Xaml = Microsoft.UI.Xaml;
using XamlMedia = Microsoft.UI.Xaml.Media;
using XamlAnimation = Microsoft.UI.Xaml.Media.Animation;
using WinSize = Windows.Foundation.Size;

namespace MeowFilm.Capabilities;

/// <summary>Native paragraph/UTF-16 layout. Only viewport images retain decoded pixels and runtime leases.</summary>
public sealed class NovelReadingSurface : Microsoft.UI.Xaml.Controls.Grid, IDisposable
{
    private sealed record Row(int Block, int Offset, double Y, double Height, TextLine? Line = null, string Image = "", double Width = 0, double ImageOffset = 0, double ImageHeight = 0);
    private sealed class ImageLease
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal BitmapSource? Bitmap;
        internal string? Reference;
        internal int DecodeWidth;
        internal Task Task = System.Threading.Tasks.Task.CompletedTask;
    }
    private readonly NovelSourceService sources;
    private readonly TextFormatter formatter = TextFormatter.Create();
    private readonly List<Row> rows = [];
    private readonly List<double> pages = [];
    private readonly Dictionary<int, ImageLease> images = [];
    private readonly Dictionary<int, (int Width, int Height)> dimensions = [];
    private readonly HashSet<Task> releasing = [];
    private double[] contentPositions = [0];
    private CancellationTokenSource? backgroundRequest;
    private readonly HashSet<Task> backgrounds = [];
    private NovelBookSession? session;
    private NovelAppearance appearance = new();
    private NovelAnchor anchor = new();
    private double offset, totalHeight, textWidth;
    private int page;
    private bool dirty = true, disposed, resourcesPaused;
    private Brush background = Brushes.White, foreground = Brushes.Black;
    private BitmapSource? backgroundImage;
    private readonly Microsoft.UI.Xaml.Controls.Image frame = new() { Stretch = XamlMedia.Stretch.Fill, IsHitTestVisible = false };
    private readonly Microsoft.UI.Xaml.Controls.Image previous = new() { Stretch = XamlMedia.Stretch.Fill, IsHitTestVisible = false };
    private readonly XamlMedia.TranslateTransform frameTransform = new(), previousTransform = new();
    private readonly NovelPresentation.VisibilityWatch visibility;
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private readonly long contrastToken;
    private readonly NovelPageRaster raster = new();
    private XamlAnimation.Storyboard? turnAnimation;
    private Xaml.XamlRoot? xamlRoot;
    private bool renderQueued;
    private int pendingTurn;
    private uint? pressedPointer;
    private double PixelsPerDip => XamlRoot?.RasterizationScale ?? 1;
    private Point? pressed;
    private bool dragged;
    private NovelAnchor? pendingAnchor;
    private long contentGeneration;
    private bool restoreRatio;
    public event EventHandler? PositionChanged;
    public event EventHandler? LayoutCompleted;
    public event EventHandler? ControlsRequested;
    public event EventHandler? UserInteracted;
    public Func<int, Task>? CrossChapterAsync { get; set; }
    public Action<Func<Task>>? RunInteraction { get; set; }
    public Action<string>? ReportError { get; set; }
    public NovelReadingSurface(NovelSourceService sources)
    {
        this.sources = sources; IsTabStop = true;
        Background = new XamlMedia.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        previous.RenderTransform = previousTransform; frame.RenderTransform = frameTransform;
        Children.Add(previous); Children.Add(frame);
        sources.Changed += SourcesChanged;
        AutomationProperties.SetName(this, "小说正文");
        visibility = new(this, visible =>
        {
            if (disposed) return;
            if (!visible) { ClearFrame(); ReleaseAllImages(); backgroundRequest?.Cancel(); backgroundImage = null; }
            else { ResumeImages(); InvalidateVisual(); }
        });
        Loaded += (_, _) =>
        {
            DetachRoot();
            xamlRoot = XamlRoot;
            if (xamlRoot is not null) xamlRoot.Changed += RootChanged;
            Reflow();
        };
        Unloaded += (_, _) => DetachRoot();
        ActualThemeChanged += (_, _) => Reflow();
        contrastToken = RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) => Reflow());
        uiSettings.ColorValuesChanged += HighContrastChanged;
        SizeChanged += (_, _) =>
        {
            Clip = new XamlMedia.RectangleGeometry { Rect = new(0, 0, ActualWidth, ActualHeight) };
            Reflow();
        };
        PointerPressed += Pressed; PointerReleased += Released; PointerMoved += Moved;
        PointerWheelChanged += WheelChanged;
        PointerCaptureLost += (_, _) => { pressed = null; pressedPointer = null; dragged = false; };
        KeyDown += KeyPressed;
    }
    private void HighContrastChanged(Windows.UI.ViewManagement.UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (!disposed) Reflow(); });
    private void RootChanged(Xaml.XamlRoot sender, Xaml.XamlRootChangedEventArgs args)
    { if (Math.Abs(PixelsPerDip - raster.Scale) > .001) Reflow(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    private void DetachRoot()
    { if (xamlRoot is not null) xamlRoot.Changed -= RootChanged; xamlRoot = null; }
    private void SourcesChanged(object? sender, EventArgs e)
    {
        if (session is null || sources.Sources.Any(s => s.Enabled &&
            s.InstallationId == session.Book.InstallationId && s.Revision == session.Book.Revision)) return;
        contentGeneration++; ReleaseAllImages(); InvalidateVisual();
    }
    public void Present(NovelBookSession value, NovelAppearance settings)
    {
        settings = settings.ForReading();
        bool contentChanged = !ReferenceEquals(session, value) || session?.Chapter?.ChapterUrl != anchor.ChapterUrl ||
            !ReferenceEquals(displayedBlocks, value.Blocks);
        session = value;
        if (contentChanged)
        {
            contentGeneration++; ReleaseAllImages(); dimensions.Clear();
            pendingAnchor = value.Entry.Anchor ?? new(); displayedBlocks = value.Blocks; dirty = true; restoreRatio = value.ConsumeRatioRestore();
            contentPositions = new double[value.Blocks.Count + 1];
            for (int i = 0; i < value.Blocks.Count; i++)
                contentPositions[i + 1] = contentPositions[i] + (value.Blocks[i].Type == "text" ? Math.Max(1, value.Blocks[i].Text.Length) : 1);
        }
        if (appearance != settings)
        {
            if (!contentChanged) pendingAnchor = CaptureAnchor();
            appearance = settings; dirty = true; LoadBackground();
        }
        if (dirty) { StopTurn(); InvalidateMeasure(); InvalidateVisual(); }
    }
    private IReadOnlyList<NovelBlock>? displayedBlocks;
    public NovelAnchor CaptureAnchor() => pendingAnchor ?? anchor;
    private void CaptureViewportAnchor()
    {
        if (session?.Chapter is not { } chapter) return;
        var first = rows.FirstOrDefault(r => r.Block >= 0 && r.Y + r.Height > offset + appearance.Margin + .1) ?? rows.LastOrDefault(r => r.Block >= 0);
        double fraction = first is { Image.Length: > 0 } image
            ? Math.Clamp((offset + appearance.Margin - ImageStart(image.Block)) / image.ImageHeight, 0, 1) : 0;
        anchor = new(chapter.ChapterUrl, chapter.Name, chapter.Index, first?.Block ?? 0, first?.Offset ?? 0,
            first is null ? 0 : ContentRatio(first.Block, first.Offset, fraction));
    }
    // Ratio uses stable content weights, not layout pixels. Together with blockIndex
    // it encodes an image-relative position without extending the public anchor.
    private double ContentRatio(int block, int utf16, double imageFraction = 0) => contentPositions[^1] == 0 ? 0 :
        Math.Clamp((contentPositions[block] + (session!.Blocks[block].Type == "text" ? utf16 : imageFraction)) / contentPositions[^1], 0, 1);
    private double ImageStart(int block) => rows.First(r => r.Block == block).Y;
    private NovelAnchor AnchorAtRatio(double ratio)
    {
        if (session?.Chapter is not { } chapter || session.Blocks.Count == 0) return new();
        double position = Math.Clamp(ratio, 0, 1) * contentPositions[^1];
        int block = Math.Min(session.Blocks.Count - 1, Math.Max(0, Array.FindLastIndex(contentPositions, value => value <= position)));
        int utf16 = session.Blocks[block].Type == "text" ? Math.Clamp((int)(position - contentPositions[block]), 0, session.Blocks[block].Text.Length) : 0;
        return new(chapter.ChapterUrl, chapter.Name, chapter.Index, block, utf16, Math.Clamp(ratio, 0, 1));
    }
    private double PositionOf(NovelAnchor saved)
    {
        if (session is null || session.Blocks.Count == 0) return 0;
        int block = Math.Clamp(saved.BlockIndex, 0, session.Blocks.Count - 1);
        var target = rows.LastOrDefault(r => r.Block == block && r.Offset <= saved.Utf16Offset) ??
            rows.LastOrDefault(r => r.Block >= 0 && r.Block <= block);
        if (target is null) return 0;
        if (target.Image.Length > 0)
        {
            double fraction = Math.Clamp(saved.Ratio * contentPositions[^1] - contentPositions[block], 0, 1);
            return ImageStart(block) + fraction * target.ImageHeight - appearance.Margin;
        }
        return saved.Ratio == 0 && block == 0 && saved.Utf16Offset == 0 ? 0 : target.Y - appearance.Margin;
    }
    public string PageStatus => appearance.Mode == "scroll" ? $"{CaptureAnchor().Ratio:P0}" : $"{page + 1} / {Math.Max(1, pages.Count)}";
    public void Seek(double ratio)
    {
        UserInteracted?.Invoke(this, EventArgs.Empty); EnsureLayout();
        double target = PositionOf(AnchorAtRatio(ratio));
        if (appearance.Mode == "scroll") offset = Math.Clamp(target, 0, Math.Max(0, totalHeight - ActualHeight));
        else { page = Math.Max(0, pages.FindLastIndex(p => p <= target + .1)); offset = pages[page]; }
        PublishPosition();
    }
    public async Task<bool> TurnAsync(int delta)
    {
        EnsureLayout();
        if (appearance.Mode == "scroll")
        {
            double next = Math.Clamp(offset + delta * Math.Max(1, ActualHeight * .85), 0, Math.Max(0, totalHeight - ActualHeight));
            if (Math.Abs(next - offset) > .1) { offset = next; PublishPosition(); return true; }
        }
        else if (page + delta >= 0 && page + delta < pages.Count)
        {
            AnimateTurn(delta); page += delta; offset = pages[page]; PublishPosition(); return true;
        }
        if (session?.Chapter is { } chapter && chapter.Index + delta >= 0 && chapter.Index + delta < session.Chapters.Count && CrossChapterAsync is not null)
        { await CrossChapterAsync(chapter.Index + delta); return true; }
        return false;
    }
    public bool AdvanceScroll(double distance)
    {
        EnsureLayout();
        double next = Math.Clamp(offset + distance, 0, Math.Max(0, totalHeight - ActualHeight));
        if (next <= offset + .01) return false;
        offset = next; PublishPosition(); return true;
    }
    private void PublishPosition()
    {
        CaptureViewportAnchor(); LoadVisibleImages(); InvalidateVisual(); PositionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Reflow()
    {
        if (disposed) return;
        StopTurn(); pendingAnchor ??= CaptureAnchor(); dirty = true; InvalidateMeasure(); InvalidateVisual();
    }
    protected override WinSize MeasureOverride(WinSize availableSize)
    {
        var size = new WinSize(double.IsInfinity(availableSize.Width) ? 700 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);
        base.MeasureOverride(size); return size;
    }
    internal void UpdateReadingLayout() { UpdateLayout(); EnsureLayout(); }
    private void EnsureLayout()
    {
        if (!dirty || ActualWidth < 1 || ActualHeight < 1) return;
        var saved = pendingAnchor ?? CaptureAnchor(); pendingAnchor = null;
        foreach (var row in rows) row.Line?.Dispose(); rows.Clear(); pages.Clear();
        textWidth = Math.Max(1, Math.Min(appearance.ContentWidth, ActualWidth - appearance.Margin * 2));
        double height = Math.Max(1, ActualHeight - appearance.Margin * 2), y = appearance.Margin, pageStart = 0;
        var face = new Typeface(new FontFamily(appearance.FontFamily), FontStyles.Normal,
            appearance.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        foreground = Desktop.DesktopTheme.GetIsHighContrast(this) ? SystemColors.WindowTextBrush : ParseBrush(appearance.Foreground);
        background = Desktop.DesktopTheme.GetIsHighContrast(this) ? SystemColors.WindowBrush : ParseBrush(appearance.Background);
        var textProperties = new RunStyle(face, appearance.FontSize, foreground, PixelsPerDip);
        var paragraph = new ParagraphStyle(textProperties, appearance.FontSize * appearance.LineHeight);
        if (session?.ContentTitle is { Length: > 0 } title)
        {
            var titleProperties = new RunStyle(new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                appearance.FontSize * 1.15, foreground, PixelsPerDip);
            Text(title, -1, titleProperties, new ParagraphStyle(titleProperties, appearance.FontSize * appearance.LineHeight * 1.15));
            y += appearance.ParagraphSpacing;
        }
        int blockIndex = -1;
        foreach (var block in session?.Blocks ?? [])
        {
            blockIndex++;
            if (block.Type == "image")
            {
                var size = dimensions.GetValueOrDefault(blockIndex);
                double imageWidth = textWidth, imageHeight = size.Width > 0 ? textWidth * size.Height / size.Width : Math.Min(height, textWidth * .65);
                if (appearance.Mode != "scroll" && imageHeight > height && session?.ImageStyle != "FULL")
                { imageWidth *= height / imageHeight; imageHeight = height; }
                double used = 0;
                do
                {
                    double slice = appearance.Mode == "scroll" ? imageHeight : Math.Min(height, imageHeight - used);
                    Add(new(blockIndex, 0, y, slice, Image: block.Src, Width: imageWidth, ImageOffset: used, ImageHeight: imageHeight));
                    y += slice; used += slice;
                } while (used < imageHeight - .1);
                y += appearance.ParagraphSpacing; continue;
            }
            if (block.Type == "text") { Text(block.Text, blockIndex, textProperties, paragraph); y += appearance.ParagraphSpacing; }
        }
        void Text(string text, int index, RunStyle properties, ParagraphStyle style)
        {
            var source = new ParagraphSource(text, properties);
            int start = 0; TextLineBreak? lineBreak = null;
            while (start < text.Length)
            {
                var line = formatter.FormatLine(source, start, textWidth, style, lineBreak);
                lineBreak?.Dispose(); lineBreak = line.GetTextLineBreak();
                double lineHeight = Math.Max(line.Height, style.LineHeight);
                Add(new(index, start, y, lineHeight, line)); y += lineHeight; start += Math.Max(1, line.Length);
            }
            lineBreak?.Dispose();
        }
        if (pages.Count == 0) pages.Add(0);
        totalHeight = Math.Max(ActualHeight, y + appearance.Margin);
        if (restoreRatio) saved = AnchorAtRatio(saved.Ratio);
        double targetY = PositionOf(saved);
        bool restoreFromRatio = restoreRatio; restoreRatio = false;
        if (appearance.Mode == "scroll") offset = Math.Clamp(targetY, 0, Math.Max(0, totalHeight - ActualHeight));
        else { page = Math.Max(0, pages.FindLastIndex(p => p <= targetY + .1)); offset = pages[page]; }
        dirty = false;
        if (restoreFromRatio) CaptureViewportAnchor();
        else if (session?.Chapter is { } current)
        {
            int block = Math.Clamp(saved.BlockIndex, 0, Math.Max(0, session.Blocks.Count - 1));
            int textOffset = block < session.Blocks.Count && session.Blocks[block].Type == "text" ? Math.Min(saved.Utf16Offset, session.Blocks[block].Text.Length) : 0;
            anchor = saved with { ChapterUrl = current.ChapterUrl, ChapterTitle = current.Name, ChapterIndex = current.Index,
                BlockIndex = block, Utf16Offset = textOffset };
        }
        LoadVisibleImages();
        LayoutCompleted?.Invoke(this, EventArgs.Empty);
        void Add(Row row)
        {
            if (pages.Count == 0) pages.Add(0);
            if (appearance.Mode != "scroll" && row.Y + row.Height > pageStart + ActualHeight - appearance.Margin && row.Y > pageStart + appearance.Margin)
            {
                pageStart = row.Y - appearance.Margin; pages.Add(pageStart);
            }
            rows.Add(row);
        }
    }
    private void InvalidateVisual()
    {
        if (renderQueued || disposed) return;
        renderQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            renderQueued = false;
            if (disposed || resourcesPaused || !IsLoaded || !DesktopVisual.IsVisible(this)) return;
            RenderFrame();
            if (pendingTurn != 0) StartTurn();
        });
    }
    private void RenderFrame()
    {
        if (disposed || resourcesPaused) return;
        EnsureLayout();
        if (ActualWidth < 1 || ActualHeight < 1) return;
        frame.Source = raster.Render(new(ActualWidth, ActualHeight), PixelsPerDip, DrawPage);
    }
    private void DrawPage(DrawingContext context)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        context.DrawRectangle(background, null, bounds);
        if (backgroundImage is not null && !Desktop.DesktopTheme.GetIsHighContrast(this)) context.DrawImage(backgroundImage, bounds);
        context.PushClip(new RectangleGeometry(bounds));
        double x = (ActualWidth - textWidth) / 2;
        double end = appearance.Mode != "scroll" && page + 1 < pages.Count ? pages[page + 1] + appearance.Margin : offset + ActualHeight - appearance.Margin;
        context.PushClip(new RectangleGeometry(new Rect(0, appearance.Margin, ActualWidth, Math.Max(0, ActualHeight - appearance.Margin * 2))));
        foreach (var row in rows.Where(r => r.Y + r.Height > offset + appearance.Margin && r.Y < end))
        {
            if (row.Line is { } line) line.Draw(context, new Point(x, row.Y - offset), InvertAxes.None);
            else if (images.TryGetValue(row.Block, out var image) && image.Bitmap is { } bitmap)
            {
                context.PushClip(new RectangleGeometry(new Rect(x, row.Y - offset, textWidth, row.Height)));
                context.DrawImage(bitmap, new Rect((ActualWidth - row.Width) / 2, row.Y - offset - row.ImageOffset, row.Width, row.ImageHeight));
                context.Pop();
            }
            else if (row.Image.Length > 0)
                context.DrawText(new FormattedText(session?.Blocks[row.Block].Alt ?? "图片", CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), appearance.FontSize, foreground,
                    PixelsPerDip), new Point(x, row.Y - offset));
        }
        context.Pop(); context.Pop();
    }
    private void StopTurn()
    {
        turnAnimation?.Stop(); turnAnimation = null; pendingTurn = 0;
        previous.Source = null; frameTransform.X = previousTransform.X = 0;
    }
    private void ClearFrame()
    {
        StopTurn(); frame.Source = null; raster.Clear();
    }
    private void AnimateTurn(int delta)
    {
        StopTurn();
        if (disposed || resourcesPaused || !IsLoaded || !DesktopVisual.IsVisible(this) ||
            appearance.Mode is "none" or "scroll" || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled || ActualWidth < 1) return;
        RenderFrame();
        previous.Source = raster.Snapshot(); pendingTurn = delta;
    }
    private void StartTurn()
    {
        int direction = pendingTurn; pendingTurn = 0;
        var animation = new XamlAnimation.Storyboard(); turnAnimation = animation;
        Add(frameTransform, direction * ActualWidth, 0);
        if (appearance.Mode != "cover") Add(previousTransform, 0, -direction * ActualWidth);
        animation.Completed += (_, _) => { if (ReferenceEquals(turnAnimation, animation)) StopTurn(); };
        animation.Begin();
        void Add(XamlMedia.TranslateTransform transform, double from, double to)
        {
            var change = new XamlAnimation.DoubleAnimation { From = from, To = to, Duration = new Xaml.Duration(TimeSpan.FromMilliseconds(180)) };
            XamlAnimation.Storyboard.SetTarget(change, transform); XamlAnimation.Storyboard.SetTargetProperty(change, "X");
            animation.Children.Add(change);
        }
    }
    private void LoadBackground()
    {
        backgroundRequest?.Cancel(); backgroundImage = null;
        if (appearance.BackgroundImage.Length == 0 || !DesktopVisual.IsVisible(this) || resourcesPaused || disposed) return;
        string path = appearance.BackgroundImage; var owner = backgroundRequest = new();
        var operation = Load(); backgrounds.Add(operation); _ = Observe();
        async Task Observe() { try { await operation; } finally { backgrounds.Remove(operation); } }
        async Task Load()
        {
            try
            {
                await sources.VisualAsync(async ct =>
                {
                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    var bitmap = await Task.Run(() => Decode(bytes, 0), ct); ct.ThrowIfCancellationRequested();
                    if (ReferenceEquals(owner, backgroundRequest)) { backgroundImage = bitmap; InvalidateVisual(); }
                }, owner.Token);
            }
            catch (OperationCanceledException) { }
            catch { if (!owner.IsCancellationRequested) ReportError?.Invoke("阅读背景图片无法打开"); }
            finally { if (ReferenceEquals(owner, backgroundRequest)) backgroundRequest = null; owner.Dispose(); }
        }
    }
    private void LoadVisibleImages()
    {
        if (session is null || !DesktopVisual.IsVisible(this) || disposed || resourcesPaused || dirty) return;
        if (!sources.Sources.Any(s => s.Enabled && s.InstallationId == session.Book.InstallationId &&
            s.Revision == session.Book.Revision)) return;
        var visible = rows.Where(r => r.Image.Length > 0 && r.Y + r.Height >= offset && r.Y < offset + ActualHeight).Select(r => r.Block).ToHashSet();
        int decodeWidth = (int)Math.Ceiling(textWidth * PixelsPerDip);
        foreach (int index in images.Keys.Where(i => !visible.Contains(i) || images[i].DecodeWidth < decodeWidth).ToArray()) ReleaseImage(index);
        foreach (int index in visible.Where(i => !images.ContainsKey(i)))
        {
            var lease = new ImageLease { DecodeWidth = decodeWidth }; images.Add(index, lease); long generation = contentGeneration;
            var book = session.Book; string url = session.Blocks[index].Src;
            lease.Task = LoadTracked();
            async Task LoadTracked()
            {
                try { await sources.VisualAsync(book, Load, lease.Cancellation.Token); }
                catch (OperationCanceledException) { }
                catch { if (!lease.Cancellation.IsCancellationRequested) ReportError?.Invoke("正文图片加载失败"); }
            }
            async Task Load(CancellationToken ct)
            {
                try
                {
                    var source = await sources.RequireAsync(book, ct);
                    lease.Reference = await sources.ImageAsync(source, url, book.BookUrl, "content", ct);
                    var bytes = await sources.ReadImageAsync(lease.Reference, ct);
                    var bitmap = await Task.Run(() => Decode(bytes, decodeWidth), ct);
                    ct.ThrowIfCancellationRequested();
                    if (generation != contentGeneration || !images.TryGetValue(index, out var current) || !ReferenceEquals(current, lease)) return;
                    lease.Bitmap = bitmap; dimensions[index] = (bitmap.PixelWidth, bitmap.PixelHeight); Reflow();
                }
                catch (OperationCanceledException) { }
                catch { if (!lease.Cancellation.IsCancellationRequested) ReportError?.Invoke("正文图片加载失败"); }
                finally
                {
                    if (ct.IsCancellationRequested && lease.Reference is { } reference)
                    { lease.Reference = null; await sources.ReleaseImageAsync(reference); }
                }
            }
        }
    }
    internal static BitmapSource Decode(byte[] bytes, int width)
    {
        using var stream = new MemoryStream(bytes);
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (width > 0) bitmap.DecodePixelWidth = width;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException)
        {
            using var data = SKData.CreateCopy(bytes); using var codec = SKCodec.Create(data);
            if (codec is null) throw new InvalidDataException("IMAGE_DECODE_FAILED");
            double scale = width <= 0 ? 1 : Math.Min(1, (double)width / codec.Info.Width);
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(codec.Info.Width * scale)), Math.Max(1, (int)Math.Round(codec.Info.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var pixels = new byte[info.BytesSize];
            try
            {
                if (codec.GetPixels(info, pixels) != SKCodecResult.Success) throw new InvalidDataException("IMAGE_DECODE_FAILED");
                var image = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Bgra32, null, pixels, info.RowBytes);
                image.Freeze(); return image;
            }
            finally { Array.Clear(pixels); }
        }
    }
    private void ReleaseImage(int index)
    {
        if (!images.Remove(index, out var lease)) return;
        lease.Cancellation.Cancel(); lease.Bitmap = null;
        var task = Release(); releasing.Add(task); _ = Observe();
        async Task Observe() { try { await task; } finally { releasing.Remove(task); } }
        async Task Release()
        {
            try
            {
                try { await lease.Task; }
                finally { if (lease.Reference is { } reference) await sources.ReleaseImageAsync(reference); }
            }
            catch { ReportError?.Invoke("图片资源释放失败"); }
            finally { lease.Cancellation.Dispose(); }
        }
    }
    private void ReleaseAllImages() { foreach (int index in images.Keys.ToArray()) ReleaseImage(index); }
    public async Task DrainImagesAsync()
    {
        resourcesPaused = true; ClearFrame(); backgroundRequest?.Cancel(); backgroundImage = null; ReleaseAllImages();
        await Task.WhenAll(releasing.Concat(backgrounds).ToArray());
    }
    internal void ResumeImages() { resourcesPaused = false; LoadBackground(); LoadVisibleImages(); InvalidateVisual(); }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        if (pressedPointer is not null) return;
        Focus(Xaml.FocusState.Pointer);
        if (!CapturePointer(e.Pointer)) return;
        pressedPointer = e.Pointer.PointerId;
        dragged = false; pressed = new(point.Position.X, point.Position.Y); UserInteracted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (pressedPointer != e.Pointer.PointerId || pressed is not { } start) return;
        var position = e.GetCurrentPoint(this).Position; var end = new Point(position.X, position.Y);
        bool moved = dragged; pressed = null; pressedPointer = null; ReleasePointerCapture(e.Pointer);
        if (!moved)
        {
            if (Math.Abs(end.X - start.X) > 45) Turn(end.X < start.X ? 1 : -1);
            else HandleClick(end);
        }
        e.Handled = true;
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (appearance.Mode != "scroll" || pressedPointer != e.Pointer.PointerId || pressed is not { } start) return;
        var position = e.GetCurrentPoint(this).Position; var point = new Point(position.X, position.Y);
        if (Math.Abs(point.Y - start.Y) < 3 && !dragged) return;
        ScrollDrag(start.Y - point.Y); pressed = point; dragged = true; e.Handled = true;
    }
    private void ScrollDrag(double distance)
    {
        EnsureLayout(); offset = Math.Clamp(offset + distance, 0, Math.Max(0, totalHeight - ActualHeight)); PublishPosition();
    }
    private void HandleClick(Point point)
    {
        UserInteracted?.Invoke(this, EventArgs.Empty);
        if (point.X < ActualWidth / 3) Turn(-1);
        else if (point.X > ActualWidth * 2 / 3) Turn(1);
        else ControlsRequested?.Invoke(this, EventArgs.Empty);
    }
    private void WheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        UserInteracted?.Invoke(this, EventArgs.Empty);
        if (appearance.Mode == "scroll")
        {
            double before = offset; offset = Math.Clamp(offset - delta, 0, Math.Max(0, totalHeight - ActualHeight));
            if (Math.Abs(offset - before) < .1) Turn(delta < 0 ? 1 : -1); else PublishPosition();
        }
        else Turn(delta < 0 ? 1 : -1);
        e.Handled = true;
    }
    private void KeyPressed(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Right or VirtualKey.PageDown or VirtualKey.Space or VirtualKey.Down) { UserInteracted?.Invoke(this, EventArgs.Empty); Turn(1); e.Handled = true; }
        if (e.Key is VirtualKey.Left or VirtualKey.PageUp or VirtualKey.Up) { UserInteracted?.Invoke(this, EventArgs.Empty); Turn(-1); e.Handled = true; }
        if (e.Key == VirtualKey.Escape) { ControlsRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
    }
    private void Turn(int direction)
    {
        if (RunInteraction is { } run) run(async () => { await TurnAsync(direction); });
        else _ = Observe();
        async Task Observe() { try { await TurnAsync(direction); } catch (OperationCanceledException) { } catch { ReportError?.Invoke("章节加载失败"); } }
    }
    private static Brush ParseBrush(string value) => (Brush)new BrushConverter().ConvertFromInvariantString(value)!;
    public void Dispose()
    {
        if (disposed) return; disposed = true; sources.Changed -= SourcesChanged;
        visibility.Dispose(); DetachRoot(); ClearFrame();
        uiSettings.ColorValuesChanged -= HighContrastChanged;
        UnregisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, contrastToken);
        contentGeneration++; backgroundRequest?.Cancel(); ReleaseAllImages();
        foreach (var row in rows) row.Line?.Dispose(); rows.Clear(); formatter.Dispose(); backgroundImage = null;
    }
    private sealed class RunStyle : TextRunProperties
    {
        private readonly Typeface typeface;
        private readonly double size;
        private readonly Brush brush;
        internal RunStyle(Typeface typeface, double size, Brush brush, double pixelsPerDip)
        { this.typeface = typeface; this.size = size; this.brush = brush; PixelsPerDip = pixelsPerDip; }
        public override Typeface Typeface => typeface;
        public override double FontRenderingEmSize => size;
        public override double FontHintingEmSize => size;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => brush;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.CurrentCulture;
        public override TextEffectCollection? TextEffects => null;
    }
    private sealed class ParagraphStyle(TextRunProperties style, double lineHeight) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => lineHeight;
        public override bool FirstLineInParagraph => false;
        public override TextRunProperties DefaultTextRunProperties => style;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }
    private sealed class ParagraphSource(string text, RunStyle style) : TextSource
    {
        public override TextRun GetTextRun(int index) => index >= text.Length ? new TextEndOfParagraph(1) :
            new TextCharacters(text, index, text.Length - index, style);
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int index) => new(index,
            new(CultureInfo.CurrentCulture, new CharacterBufferRange(text, 0, Math.Min(index, text.Length))));
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }
}

// WPF owns only this offscreen drawing target and TextFormatter's lines above.
// Neither DrawingVisual nor RenderTargetBitmap is attached to a window or a XAML tree.
internal sealed class NovelPageRaster
{
    private RenderTargetBitmap? target;
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? source;
    private byte[] transfer = [];
    internal double Scale { get; private set; } = 1;

    internal Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap Render(WinSize size, double scale, Action<DrawingContext> draw)
    {
        int width = Math.Max(1, (int)Math.Ceiling(size.Width * scale)), height = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        if (target is null || target.PixelWidth != width || target.PixelHeight != height || Math.Abs(Scale - scale) > .001)
        {
            Scale = scale;
            target = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            source = new(width, height); transfer = new byte[checked(width * height * 4)];
        }
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) draw(context);
        target.Clear(); target.Render(visual); target.CopyPixels(transfer, checked(width * 4), 0);
        var pixels = source!;
        using (var buffer = pixels.PixelBuffer.AsStream()) buffer.Write(transfer);
        pixels.Invalidate();
        return pixels;
    }

    internal Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? Snapshot()
    {
        if (source is null) return null;
        var snapshot = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(source.PixelWidth, source.PixelHeight);
        using (var buffer = snapshot.PixelBuffer.AsStream()) buffer.Write(transfer);
        snapshot.Invalidate();
        return snapshot;
    }

    internal void Clear() { target = null; source = null; transfer = []; }
}
