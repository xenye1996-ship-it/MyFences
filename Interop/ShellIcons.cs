using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyFences.Interop;

internal static class ShellIcons
{
    internal static ImageSource? Get(string path)
    {
        nint list = 0, icon = 0;
        try
        {
            if (Native.SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<Native.FileInfo>(), 0x4000) == 0) return null;
            Guid iid = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
            Marshal.ThrowExceptionForHR(Native.SHGetImageList(2, ref iid, out list));
            icon = Native.ImageList_GetIcon(list, info.Index, 1);
            if (icon == 0) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            return null;
        }
        finally
        {
            if (icon != 0) Native.DestroyIcon(icon);
            if (list != 0) Marshal.Release(list);
        }
    }
}
