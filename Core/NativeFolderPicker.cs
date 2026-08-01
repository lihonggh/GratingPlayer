using System.Runtime.InteropServices;
using System.Text;
using WinRT.Interop;

namespace GratingPlayer.Core;

/// <summary>
/// 未打包 WinUI 下 Storage.FolderPicker 常无响应。
/// 使用 SHBrowseForFolderW（经典选目录对话框）。
/// </summary>
internal static class NativeFolderPicker
{
    public static string? PickFolder(Microsoft.UI.Xaml.Window window, string title = "选择要播放的图片目录")
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        return Browse(hwnd, title);
    }

    private static string? Browse(IntPtr ownerHwnd, string title)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = ownerHwnd,
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
        };

        var pidl = SHBrowseForFolderW(ref bi);
        if (pidl == IntPtr.Zero)
            return null;

        try
        {
            var sb = new StringBuilder(260);
            return SHGetPathFromIDListW(pidl, sb) ? sb.ToString() : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x00000001;
    private const uint BIF_NEWDIALOGSTYLE = 0x00000040;
    private const uint BIF_EDITBOX = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);
}
