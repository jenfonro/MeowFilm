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
    private const int ThumbMaxSide = 80;
    private const double SmallDisplayMaxPixels = 96d;
    private static readonly SKSamplingOptions LinearMipmapSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly Guid IidBufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");
    private IntPtr stagingPtr;
    private readonly int byteCount, thumbWidth, thumbHeight, thumbByteCount;
    private WriteableBitmap? source, thumbSource;
    private (Brush Raw, Brush Palette, Brush Top)? paletteBrushes;
    private DispatcherQueue? dispatcher;
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    internal long Cost => byteCount + thumbByteCount;
    internal (Color Raw, Color Palette, Color Top, bool HasTopPalette) Palettes { get; }

    internal ArtworkPixels(int width, int height, IntPtr ownedNativePixels, int byteCount,
        int thumbWidth = 0, int thumbHeight = 0, int thumbByteCount = 0)
    {
        PixelWidth = width; PixelHeight = height; this.byteCount = byteCount;
        this.thumbWidth = thumbWidth; this.thumbHeight = thumbHeight; this.thumbByteCount = thumbByteCount;
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

    public BitmapSource GetSource(DispatcherQueue uiDispatcher, double renderedMaxPixels = 0)
    {
        CheckThread(uiDispatcher);
        if (source is null)
        {
            IntPtr ptr = Interlocked.Exchange(ref stagingPtr, IntPtr.Zero);
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("Artwork pixels are unavailable.");
            try
            {
                var main = CreateBitmap((byte*)ptr, PixelWidth, PixelHeight, byteCount);
                if (thumbByteCount > 0)
                    Volatile.Write(ref thumbSource, CreateBitmap((byte*)ptr + byteCount, thumbWidth, thumbHeight, thumbByteCount));
                Volatile.Write(ref source, main);
            }
            finally { NativeMemory.Free((void*)ptr); GC.SuppressFinalize(this); }
        }
        return thumbSource is not null && renderedMaxPixels > 0 && renderedMaxPixels <= SmallDisplayMaxPixels
            ? thumbSource
            : source!;
    }

    private static WriteableBitmap CreateBitmap(byte* src, int width, int height, int bytes)
    {
        var bitmap = new WriteableBitmap(width, height);
        var buffer = bitmap.PixelBuffer;
        if (buffer is not IWinRTObject { NativeObject.ThisPtr: var thisPtr } || thisPtr == IntPtr.Zero)
            throw new InvalidOperationException("PixelBuffer native pointer is unavailable.");
        Guid iid = IidBufferByteAccess;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(thisPtr, in iid, out IntPtr access));
        try
        {
            byte* dst = null;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, byte**, int>)(*(IntPtr**)access)[3])(access, &dst));
            Buffer.MemoryCopy(src, dst, buffer.Capacity, bytes);
        }
        finally { Marshal.Release(access); }
        buffer.Length = (uint)bytes;
        bitmap.Invalidate();
        return bitmap;
    }

    internal static IntPtr DecodeToNativeBgra(SKCodec codec, SKSizeI decodeSize, int width, int height,
        out int byteCount, out int thumbWidth, out int thumbHeight, out int thumbByteCount)
    {
        var decodedInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var targetInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        byteCount = checked(width * height * 4);
        if (Math.Max(width, height) > ThumbMaxSide)
        {
            double thumbScale = (double)ThumbMaxSide / Math.Max(width, height);
            thumbWidth = Math.Max(1, (int)Math.Round(width * thumbScale));
            thumbHeight = Math.Max(1, (int)Math.Round(height * thumbScale));
            thumbByteCount = checked(thumbWidth * thumbHeight * 4);
        }
        else
        {
            thumbWidth = thumbHeight = thumbByteCount = 0;
        }
        int totalBytes = checked(byteCount + thumbByteCount);
        if (decodeSize.Width == width && decodeSize.Height == height)
        {
            byte* staged = (byte*)NativeMemory.Alloc((nuint)totalBytes);
            try
            {
                if (codec.GetPixels(targetInfo, (IntPtr)staged) != SKCodecResult.Success)
                    throw new InvalidDataException("IMAGE_DECODE_FAILED");
                if (thumbByteCount > 0)
                    ScaleThumb(targetInfo, staged, byteCount, thumbWidth, thumbHeight);
                IntPtr taken = (IntPtr)staged; staged = null;
                return taken;
            }
            finally { if (staged is not null) NativeMemory.Free(staged); }
        }
        int decodedBytes = checked(decodeSize.Width * decodeSize.Height * 4);
        byte* decoded = (byte*)NativeMemory.Alloc((nuint)decodedBytes);
        try
        {
            if (codec.GetPixels(decodedInfo, (IntPtr)decoded) != SKCodecResult.Success)
                throw new InvalidDataException("IMAGE_DECODE_FAILED");
            byte* scaled = (byte*)NativeMemory.Alloc((nuint)totalBytes);
            try
            {
                using var srcPixmap = new SKPixmap(decodedInfo, (IntPtr)decoded, decodeSize.Width * 4);
                using var dstPixmap = new SKPixmap(targetInfo, (IntPtr)scaled, width * 4);
                if (!srcPixmap.ScalePixels(dstPixmap, LinearMipmapSampling))
                    throw new InvalidDataException("IMAGE_DECODE_FAILED");
                if (thumbByteCount > 0)
                    ScaleThumb(targetInfo, scaled, byteCount, thumbWidth, thumbHeight);
                IntPtr taken = (IntPtr)scaled; scaled = null;
                return taken;
            }
            finally { if (scaled is not null) NativeMemory.Free(scaled); }
        }
        finally { if (decoded is not null) NativeMemory.Free(decoded); }
    }

    private static void ScaleThumb(SKImageInfo mainInfo, byte* staged, int mainByteCount, int thumbWidth, int thumbHeight)
    {
        var thumbInfo = new SKImageInfo(thumbWidth, thumbHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var mainPixmap = new SKPixmap(mainInfo, (IntPtr)staged, mainInfo.Width * 4);
        using var thumbPixmap = new SKPixmap(thumbInfo, (IntPtr)(staged + mainByteCount), thumbWidth * 4);
        if (!mainPixmap.ScalePixels(thumbPixmap, LinearMipmapSampling))
            throw new InvalidDataException("IMAGE_DECODE_FAILED");
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
