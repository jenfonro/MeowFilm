using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

// The HWND shape follows the existing XAML ink. It neither presents a second
// image nor reads the animated highlight back on each playback frame.
internal sealed class MusicDesktopLyricsInputRegion : IDisposable
{
    private readonly nint window;
    private readonly FrameworkElement root;
    private readonly Func<bool> wholeWindow;
    private string? requested;
    private long revision;
    private bool queued, running, disposed, full = true;
    private Task work = Task.CompletedTask;
    private TaskCompletionSource completion = Completed();

    internal MusicDesktopLyricsInputRegion(nint window, FrameworkElement root, Func<bool> wholeWindow)
    {
        this.window = window; this.root = root; this.wholeWindow = wholeWindow;
        root.Loaded += LayoutChanged; root.Unloaded += Unloaded; root.LayoutUpdated += LayoutChanged;
    }
    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult(); return result;
    }
    private void LayoutChanged(object? sender, object args) => Refresh();
    private void Unloaded(object sender, RoutedEventArgs args) { revision++; requested = null; completion.TrySetResult(); }

    internal void Refresh()
    {
        if (disposed) return;
        if (!root.IsLoaded || root.XamlRoot?.IsHostVisible != true)
        { revision++; requested = null; completion.TrySetResult(); return; }
        if (wholeWindow())
        {
            if (requested is null && full) { completion.TrySetResult(); return; }
            revision++; requested = null; Reset(); completion.TrySetResult(); return;
        }
        var signature = Signature();
        if (signature == requested) return;
        requested = signature; revision++;
        // Do not leave an old glyph shape clipping new text while its native
        // layout and outline are being completed.
        Reset();
        if (completion.Task.IsCompleted) completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (queued || running) return;
        queued = true;
        if (!root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            queued = false;
            if (disposed) return;
            running = true;
            work = UpdateLatestAsync();
            try { await work; }
            catch (Exception error) { completion.TrySetException(error); throw; }
            finally { running = false; }
        })) completion.TrySetException(new InvalidOperationException("Desktop lyric input queue stopped."));
    }
    internal async Task SynchronizeAsync()
    {
        root.UpdateLayout(); Refresh();
        await completion.Task;
        await work;
    }
    private async Task UpdateLatestAsync()
    {
        long captured;
        do
        {
            captured = revision;
            await UpdateAsync();
        } while (!disposed && captured != revision && root.IsLoaded && root.XamlRoot?.IsHostVisible == true && !wholeWindow());
    }
    private async Task UpdateAsync()
    {
        long current = revision;
        if (!CanApply(current)) { completion.TrySetResult(); return; }
        // Keep the whole surface available until the new glyph layout is ready.
        Reset();
        MusicLyricOutline[] outlines;
        do
        {
            root.UpdateLayout();
            outlines = Visuals(root).OfType<UIElement>()
                .Where(view => MeowFilm.Desktop.DesktopVisual.IsVisible(view) && EffectiveOpacity(view) > 0)
                .Select(view => view switch
            {
                MusicDesktopLyricTextView text => text.Outline,
                MusicLyricWordView word => word.Outline,
                _ => null
            }).OfType<MusicLyricOutline>().ToArray();
            await Task.WhenAll(outlines.Select(outline => outline.EnsureCurrentAsync()));
            if (!CanApply(current)) { Refresh(); return; }
            // Publishing a new outline image can itself change arranged bounds.
            // Only map coverage after that layout and every resulting raster finish.
            root.UpdateLayout();
        } while (outlines.Any(outline => !outline.IsCurrent));
        if (requested != Signature()) { requested = null; Refresh(); return; }
        double scale = root.XamlRoot!.RasterizationScale;
        int width = (int)Math.Ceiling(root.ActualWidth * scale), height = (int)Math.Ceiling(root.ActualHeight * scale);
        var coverage = new byte[checked(width * height)];
        var sources = Visuals(root).Select(view => view switch
        {
            MusicDesktopLyricTextView text => text.Outline.InputInk,
            MusicLyricWordView word => word.Outline.InputInk,
            _ => null
        }).OfType<MusicLyricOutline.InkSnapshot>().Where(ink => ink.Surface.IsLoaded &&
            MeowFilm.Desktop.DesktopVisual.IsVisible(ink.Surface) && EffectiveOpacity(ink.Surface) > 0).ToArray();
        foreach (var ink in sources)
        {
            var source = ink.Surface;
            int inkWidth = ink.Width, inkHeight = ink.Height;
            if (!CanApply(current)) { Refresh(); return; }
            var transform = source.TransformToVisual(root);
            var inverse = transform.Inverse;
            var bounds = transform.TransformBounds(new Rect(0, 0, source.ActualWidth, source.ActualHeight));
            for (int y = Math.Max(0, (int)Math.Floor(bounds.Top * scale)); y < Math.Min(height, (int)Math.Ceiling(bounds.Bottom * scale)); y++)
            for (int x = Math.Max(0, (int)Math.Floor(bounds.Left * scale)); x < Math.Min(width, (int)Math.Ceiling(bounds.Right * scale)); x++)
            {
                var local = inverse.TransformPoint(new Point((x + .5) / scale, (y + .5) / scale));
                int sx = (int)Math.Floor(local.X * inkWidth / source.ActualWidth);
                int sy = (int)Math.Floor(local.Y * inkHeight / source.ActualHeight);
                if (sx < 0 || sy < 0 || sx >= inkWidth || sy >= inkHeight) continue;
                byte alpha = ink.Alpha[sy * inkWidth + sx];
                int at = y * width + x;
                coverage[at] = Math.Max(coverage[at], alpha);
            }
        }
        if (!CanApply(current)) { Refresh(); return; }
        if (requested != Signature()) { requested = null; Refresh(); return; }
        Apply(coverage, width, height);
        completion.TrySetResult();
    }
    private bool CanApply(long current) => !disposed && current == revision && root.IsLoaded &&
        root.XamlRoot?.IsHostVisible == true && !wholeWindow();
    private string Signature()
    {
        var key = new StringBuilder().Append(root.ActualWidth).Append('|').Append(root.ActualHeight)
            .Append('|').Append(root.XamlRoot!.RasterizationScale);
        foreach (var text in Visuals(root).OfType<TextBlock>())
        {
            if (!MeowFilm.Desktop.DesktopVisual.IsVisible(text)) continue;
            double opacity = EffectiveOpacity(text);
            if (opacity <= 0) continue;
            var bounds = text.TransformToVisual(root).TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight));
            key.Append('|').Append(text.Text).Append('|').Append(text.FontFamily.Source).Append('|').Append(text.FontSize)
                .Append('|').Append(text.Language).Append('|').Append(text.FlowDirection).Append('|').Append(bounds)
                .Append('|').Append(opacity).Append('|').Append((text.Foreground as SolidColorBrush)?.Color.A);
        }
        return key.ToString();
    }
    private double EffectiveOpacity(DependencyObject view)
    {
        double opacity = 1;
        for (DependencyObject? owner = view; owner is not null; owner = VisualTreeHelper.GetParent(owner))
        {
            if (owner is UIElement element) opacity *= element.Opacity;
            if (ReferenceEquals(owner, root)) break;
        }
        return opacity;
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject view)
    {
        yield return view;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(view); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(view, index))) yield return child;
    }
    private void Reset()
    {
        if (full) return;
        if (SetWindowRgn(window, 0, true) == 0) throw new Win32Exception();
        full = true;
    }
    private void Apply(byte[] coverage, int width, int height)
    {
        var origin = new NativePoint();
        var xaml = root.XamlRoot!;
        var islandWindow = Microsoft.UI.Win32Interop.GetWindowFromWindowId(xaml.ContentIslandEnvironment.AppWindowId);
        if (!GetWindowRect(window, out var bounds) || !ClientToScreen(islandWindow, ref origin)) throw new Win32Exception();
        var visualOrigin = root.TransformToVisual(null).TransformPoint(new Point());
        int offsetX = (int)Math.Round(origin.X + visualOrigin.X * xaml.RasterizationScale) - bounds.Left;
        int offsetY = (int)Math.Round(origin.Y + visualOrigin.Y * xaml.RasterizationScale) - bounds.Top;
        var rectangles = new List<NativeRect>();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width;)
        {
            if (coverage[y * width + x] == 0) { x++; continue; }
            int left = x++;
            while (x < width && coverage[y * width + x] != 0) x++;
            rectangles.Add(new(left + offsetX, y + offsetY, x + offsetX, y + offsetY + 1));
        }
        int header = Marshal.SizeOf<RegionHeader>(), bytes = rectangles.Count * Marshal.SizeOf<NativeRect>();
        nint data = Marshal.AllocHGlobal(header + bytes);
        nint region = 0;
        try
        {
            Marshal.StructureToPtr(new RegionHeader
            {
                Size = (uint)header, Type = 1, Count = (uint)rectangles.Count, Bytes = (uint)bytes,
                Bounds = rectangles.Count == 0 ? new(0, 0, 0, 0) : new(rectangles.Min(rect => rect.Left),
                    rectangles[0].Top, rectangles.Max(rect => rect.Right), rectangles[^1].Bottom)
            }, data, false);
            for (int index = 0; index < rectangles.Count; index++) Marshal.StructureToPtr(rectangles[index], data + header + index * 16, false);
            region = rectangles.Count == 0 ? CreateRectRgn(0, 0, 0, 0) : ExtCreateRegion(0, (uint)(header + bytes), data);
            if (region == 0 || SetWindowRgn(window, region, true) == 0) throw new Win32Exception();
            region = 0; // successful SetWindowRgn transfers ownership to USER32
            full = false;
        }
        finally { if (region != 0) DeleteObject(region); Marshal.FreeHGlobal(data); }
    }
    public void Dispose()
    {
        disposed = true; revision++; completion.TrySetResult();
        root.Loaded -= LayoutChanged; root.Unloaded -= Unloaded; root.LayoutUpdated -= LayoutChanged;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct RegionHeader { internal uint Size, Type, Count, Bytes; internal NativeRect Bounds; }
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint ExtCreateRegion(nint transform, uint size, nint data);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
