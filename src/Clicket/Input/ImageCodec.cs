using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicket.Core;

namespace Clicket.Input;

/// <summary>Image steps keep their picture as a base64 PNG inside the macro file; this converts it to and from pixels.</summary>
internal static class ImageCodec
{
    /// <summary>Largest picture accepted from a file, in pixels; bigger ones are almost surely a mistake and search slowly.</summary>
    public const int MaxPixels = 1_000_000;

    /// <summary>Null when there is no picture or it cannot be read.</summary>
    public static PixelImage? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            return FromSource(BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
        }
        catch (Exception e) when (e is FormatException || e is NotSupportedException || e is IOException || e is ArgumentException || e is InvalidOperationException)
        {
            Log.Warn("Image step picture could not be read: " + e.GetType().Name);
            return null;
        }
    }

    /// <summary>Reads an image file (PNG, JPEG, BMP, GIF, TIFF); null with a reason when it cannot be used.</summary>
    public static PixelImage? Load(string path, out string? problem)
    {
        problem = null;
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            {
                problem = "TooLarge";
                return null;
            }
            return FromSource(frame);
        }
        catch (Exception e) when (e is FileFormatException || e is NotSupportedException || e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException)
        {
            Log.Warn("Image file could not be read: " + e.GetType().Name);
            problem = "Unreadable";
            return null;
        }
    }

    /// <summary>
    /// The picture on the clipboard: a PNG (Snipping Tool, browsers), a copied image file, or a plain bitmap, in that
    /// order of preference because the bitmap form loses detail in some apps. Null with a reason when there is none.
    /// </summary>
    public static PixelImage? FromClipboard(out string? problem)
    {
        problem = null;
        try
        {
            if (System.Windows.Clipboard.ContainsData("PNG") && System.Windows.Clipboard.GetData("PNG") is Stream png)
                return Checked(BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad), out problem);
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                if (files.Count > 0 && files[0] != null) return Load(files[0]!, out problem);
            }
            if (System.Windows.Clipboard.ContainsImage())
            {
                var bitmap = System.Windows.Clipboard.GetImage();
                if (bitmap != null) return Checked(bitmap, out problem);
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException || e is NotSupportedException || e is IOException || e is ArgumentException || e is InvalidOperationException || e is FileFormatException)
        {
            Log.Warn("Clipboard picture could not be read: " + e.GetType().Name);
            problem = "Unreadable";
            return null;
        }
        problem = "NoClipboard";
        return null;
    }

    private static PixelImage? Checked(BitmapSource source, out string? problem)
    {
        problem = null;
        if ((long)source.PixelWidth * source.PixelHeight > MaxPixels)
        {
            problem = "TooLarge";
            return null;
        }
        return FromSource(source);
    }

    public static string Encode(PixelImage image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToSource(image)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    /// <summary>An opaque, frozen bitmap for previews.</summary>
    public static BitmapSource ToSource(PixelImage image)
    {
        var opaque = new byte[image.Width * image.Height * 4];
        for (var y = 0; y < image.Height; y++)
        {
            Buffer.BlockCopy(image.Pixels, y * image.Stride, opaque, y * image.Width * 4, image.Width * 4);
            for (var x = 0; x < image.Width; x++) opaque[(y * image.Width + x) * 4 + 3] = 255;
        }
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, opaque, image.Width * 4);
        source.Freeze();
        return source;
    }

    public static PixelImage FromSource(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new PixelImage(converted.PixelWidth, converted.PixelHeight, pixels, stride);
    }
}
