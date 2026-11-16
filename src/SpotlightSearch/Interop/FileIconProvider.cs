using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpotlightSearch.Interop;

/// <summary>
/// Extracted from Services/FileIconProvider.cs, reduced to what the search window
/// actually needs: one bitmap per path, cached, decoded off the UI thread.
///
/// IShellItemImageFactory is preferred over SHGetFileInfo because it renders at the
/// requested pixel size (crisp on HiDPI) and also resolves shell: monikers, which is
/// how Store/UWP app icons are addressed.
/// </summary>
internal static class FileIconProvider
{
    private const int IconPixels = 64;
    private const int MaxCacheEntries = 192;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static readonly Guid ShellItemImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public static ImageSource? GetIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out ImageSource? cached)) return cached;
        }

        ImageSource? icon = Load(path);

        lock (Gate)
        {
            if (Cache.Count >= MaxCacheEntries) Cache.Clear();
            Cache[path] = icon;
        }

        return icon;
    }

    private static ImageSource? Load(string path)
    {
        return FromShellItem(path) ?? FromShellFileInfo(path);
    }

    private static ImageSource? FromShellItem(string path)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid interfaceId = ShellItemImageFactoryId;
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out object item);
            if (item is not NativeMethods.IShellItemImageFactory factory) return null;

            factory.GetImage(new NativeMethods.Size(IconPixels, IconPixels),
                NativeMethods.SiigbfBiggerSizeOk | NativeMethods.SiigbfIconOnly, out bitmap);
            if (bitmap == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
        }
    }

    private static ImageSource? FromShellFileInfo(string path)
    {
        var info = new NativeMethods.ShFileInfo();
        try
        {
            uint flags = NativeMethods.ShgfiIcon | NativeMethods.ShgfiLargeIcon;
            uint attributes = NativeMethods.FileAttributeNormal;

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                // Unknown target: ask the shell for the icon of the extension only.
                string extension = Path.GetExtension(path);
                if (extension.Length == 0) return null;
                path = extension;
                flags |= NativeMethods.ShgfiUseFileAttributes;
            }

            long ok = NativeMethods.SHGetFileInfo(
                path, attributes, ref info, (uint)Marshal.SizeOf<NativeMethods.ShFileInfo>(), flags).ToInt64();

            if (ok == 0 || info.hIcon == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (info.hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(info.hIcon);
        }
    }
}
