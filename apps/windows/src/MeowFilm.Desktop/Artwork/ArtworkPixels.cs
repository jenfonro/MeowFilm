using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.UI;
using WinRT;

namespace MeowFilm.Desktop.Artwork;

/// <summary>Immutable premultiplied BGRA pixels staged in unmanaged memory off the .NET LOH.</summary>
public sealed unsafe class ArtworkPixels
{
    private static readonly Guid IidBufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");
    private IntPtr stagingPtr;
    private readonly int byteCount;
    private WriteableBitmap? source;
    private (Brush Raw, Brush Palette, Brush Top)? paletteBrushes;
    private DispatcherQueue? dispatcher;
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    internal long Cost => byteCount;
    internal (Color Raw, Color Palette, Color Top, bool HasTopPalette) Palettes { get; }

    internal ArtworkPixels(int width, int height, IntPtr ownedNativePixels, int byteCount)
    {
        PixelWidth = width; PixelHeight = height; this.byteCount = byteCount;
        stagingPtr = ownedNativePixels;
        Palettes = Sample(new ReadOnlySpan<byte>((void*)ownedNativePixels, byteCount));
    }

    internal void ReleaseStaging()
    {
        IntPtr prior = Interlocked.Exchange(ref stagingPtr, IntPtr.Zero);
        if (prior != IntPtr.Zero) { NativeMemory.Free((void*)prior); GC.SuppressFinalize(this); }
    }

    ~ArtworkPixels()
    {
        IntPtr prior = Interlocked.Exchange(ref stagingPtr, IntPtr.Zero);
        if (prior != IntPtr.Zero) NativeMemory.Free((void*)prior);
    }

    private void CheckThread(DispatcherQueue uiDispatcher)
    {
        if (!uiDispatcher.HasThreadAccess) throw new InvalidOperationException("Artwork presentation requires its UI thread.");
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Artwork cannot share a XAML source between UI threads.");
        dispatcher ??= uiDispatcher;
    }

    public BitmapSource GetSource(DispatcherQueue uiDispatcher)
    {
        CheckThread(uiDispatcher);
        if (source is not null) return source;
        IntPtr ptr = Interlocked.Exchange(ref stagingPtr, IntPtr.Zero);
        if (ptr == IntPtr.Zero) throw new InvalidOperationException("Artwork pixels are unavailable.");
        try
        {
            var bitmap = new WriteableBitmap(PixelWidth, PixelHeight);
            var buffer = bitmap.PixelBuffer;
            if (buffer is not IWinRTObject { NativeObject.ThisPtr: var thisPtr } || thisPtr == IntPtr.Zero)
                throw new InvalidOperationException("PixelBuffer native pointer is unavailable.");
            Guid iid = IidBufferByteAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(thisPtr, in iid, out IntPtr access));
            try
            {
                byte* dst = null;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, byte**, int>)(*(IntPtr**)access)[3])(access, &dst));
                Buffer.MemoryCopy((void*)ptr, dst, buffer.Capacity, byteCount);
            }
            finally { Marshal.Release(access); }
            buffer.Length = (uint)byteCount;
            bitmap.Invalidate();
            Volatile.Write(ref source, bitmap);
            return bitmap;
        }
        finally { NativeMemory.Free((void*)ptr); GC.SuppressFinalize(this); }
    }

    internal static IntPtr DecodeToNativeBgra(SKCodec codec, SKSizeI decodeSize, int width, int height, out int byteCount)
    {
        var decodedInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int decodedBytes = checked(decodeSize.Width * decodeSize.Height * 4);
        byte* decoded = (byte*)NativeMemory.Alloc((nuint)decodedBytes);
        try
        {
            if (codec.GetPixels(decodedInfo, (IntPtr)decoded) != SKCodecResult.Success)
                throw new InvalidDataException("IMAGE_DECODE_FAILED");
            if (decodeSize.Width == width && decodeSize.Height == height)
            {
                byteCount = decodedBytes;
                IntPtr taken = (IntPtr)decoded; decoded = null;
                return taken;
            }
            var targetInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            byteCount = checked(width * height * 4);
            byte* scaled = (byte*)NativeMemory.Alloc((nuint)byteCount);
            try
            {
                using var srcPixmap = new SKPixmap(decodedInfo, (IntPtr)decoded, decodeSize.Width * 4);
                using var dstPixmap = new SKPixmap(targetInfo, (IntPtr)scaled, width * 4);
                if (!srcPixmap.ScalePixels(dstPixmap, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
                    throw new InvalidDataException("IMAGE_DECODE_FAILED");
                IntPtr taken = (IntPtr)scaled; scaled = null;
                return taken;
            }
            finally { if (scaled is not null) NativeMemory.Free(scaled); }
        }
        finally { if (decoded is not null) NativeMemory.Free(decoded); }
    }

    internal (Brush Raw, Brush Palette, Brush Top) GetPaletteBrushes(DispatcherQueue uiDispatcher)
    {
        CheckThread(uiDispatcher);
        return paletteBrushes ??= (new SolidColorBrush(Palettes.Raw), new SolidColorBrush(Palettes.Palette), new SolidColorBrush(Palettes.Top));
    }

    private (Color Raw, Color Palette, Color Top, bool HasTopPalette) Sample(ReadOnlySpan<byte> data)
    {
        (double Red, double Green, double Blue, long Weight, int Count) Average(ReadOnlySpan<byte> span, int rows)
        {
            long r = 0, g = 0, b = 0, weight = 0; int count = PixelWidth * rows;
            for (int offset = 0; offset < count * 4; offset += 4)
            { b += span[offset]; g += span[offset + 1]; r += span[offset + 2]; weight += span[offset + 3]; }
            return weight == 0 ? (0, 0, 0, 0, count) : (r * 255d / weight, g * 255d / weight, b * 255d / weight, weight, count);
        }
        static Color ColorOf(double red, double green, double blue, double factor = 1, byte alpha = 255) =>
            Color.FromArgb(alpha, (byte)Math.Clamp(red * factor, 0, 255), (byte)Math.Clamp(green * factor, 0, 255), (byte)Math.Clamp(blue * factor, 0, 255));
        var fallback = Color.FromArgb(255, 105, 105, 105);
        var all = Average(data, PixelHeight);
        if (all.Weight == 0) return (fallback, fallback, fallback, false);
        double scale = Math.Min(1, 105 / Math.Max(1, .2126 * all.Red + .7152 * all.Green + .0722 * all.Blue));
        var top = Average(data, Math.Max(1, (int)Math.Ceiling(PixelHeight * .375)));
        byte alpha = (byte)Math.Clamp((int)Math.Round((double)top.Weight / top.Count, MidpointRounding.AwayFromZero), 0, 255);
        return (ColorOf(all.Red, all.Green, all.Blue), ColorOf(all.Red, all.Green, all.Blue, scale),
            top.Weight == 0 ? fallback : ColorOf(top.Red, top.Green, top.Blue, alpha: alpha), top.Weight > 0);
    }
}
