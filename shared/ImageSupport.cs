using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RightToolbox.Images;

/// <summary>
/// Shared WPF imaging helpers for the image plugins in this repository.
/// Linked into each package by its .csproj, so every published package stays self-contained.
/// </summary>
public static class ImageSupport
{
    public static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static BitmapFrame Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    /// <summary>Normalizes to Bgra32 so drawing, cropping and encoding behave predictably.</summary>
    public static BitmapSource Flatten(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32) return source;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    public static BitmapSource Resize(BitmapSource source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == source.PixelWidth && height == source.PixelHeight) return source;
        var bitmap = new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
        RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.HighQuality);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource FitWithin(BitmapSource source, int maxEdge)
    {
        if (maxEdge <= 0) return source;
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxEdge) return source;
        var scale = (double)maxEdge / longest;
        return Resize(source, (int)Math.Round(source.PixelWidth * scale), (int)Math.Round(source.PixelHeight * scale));
    }

    public static BitmapSource Crop(BitmapSource source, int left, int top, int width, int height)
    {
        left = Math.Clamp(left, 0, Math.Max(0, source.PixelWidth - 1));
        top = Math.Clamp(top, 0, Math.Max(0, source.PixelHeight - 1));
        width = Math.Clamp(width, 1, source.PixelWidth - left);
        height = Math.Clamp(height, 1, source.PixelHeight - top);
        var cropped = new CroppedBitmap(source, new Int32Rect(left, top, width, height));
        cropped.Freeze();
        return cropped;
    }

    public static BitmapSource Compose(int width, int height, double dpi, Action<DrawingContext> draw)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) draw(context);
        var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    public static FormattedText Text(string text, double size, Color color, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei, Segoe UI"), FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.0);

    public static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static void WriteBytes(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>EXIF DateTimeOriginal when the file has one; null when absent or unreadable.</summary>
    public static DateTime? ReadTakenAt(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
            if (decoder.Metadata is not BitmapMetadata metadata) return null;
            var raw = metadata.DateTaken;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Decodes at reduced width so hashing thousands of photos stays fast and memory-light.</summary>
    public static BitmapSource LoadThumbnail(string path, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            image.StreamSource = stream;
            image.DecodePixelWidth = Math.Max(8, decodeWidth);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
        }
        image.Freeze();
        return image;
    }

    /// <summary>64-bit difference hash: robust to resizing and re-compression, used for near-duplicate detection.</summary>
    public static ulong DifferenceHash(BitmapSource source)
    {
        const int Width = 9;
        const int Height = 8;
        BitmapSource gray = source.Format == PixelFormats.Gray8
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        var scaled = new TransformedBitmap(gray, new ScaleTransform((double)Width / gray.PixelWidth, (double)Height / gray.PixelHeight));
        RenderOptions.SetBitmapScalingMode(scaled, BitmapScalingMode.Linear);
        var pixels = new byte[Width * Height];
        scaled.CopyPixels(pixels, Width, 0);

        var hash = 0UL;
        var bit = 0;
        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < Width - 1; column++)
            {
                if (pixels[row * Width + column] > pixels[row * Width + column + 1]) hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    public static int HammingDistance(ulong left, ulong right) => System.Numerics.BitOperations.PopCount(left ^ right);

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
    /// <summary>WPF imaging needs an STA thread; JSON Lines I/O has no such requirement, so every image plugin runs its body here.</summary>
    public static T RunSta<T>(Func<T> action)
    {
        var result = default(T)!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
