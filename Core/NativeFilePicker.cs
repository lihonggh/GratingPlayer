using System.Runtime.InteropServices;
using System.Text;
using WinRT.Interop;

namespace GratingPlayer.Core;

/// <summary>
/// 与 <see cref="NativeFolderPicker"/> 相同：取主窗口 HWND，调用经典系统对话框。
/// </summary>
internal static class NativeFilePicker
{
    public static string? PickAudioFile(Microsoft.UI.Xaml.Window window, string title = "选择音频文件")
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        return OpenFile(hwnd, title);
    }

    private static string? OpenFile(IntPtr ownerHwnd, string title)
    {
        var filterPtr = AllocFilter(
            "音频文件", "*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac;*.ogg;*.oga;*.opus",
            "全部文件", "*.*");
        var filePtr = Marshal.AllocHGlobal(MaxPathChars * 2);
        var titlePtr = Marshal.StringToHGlobalUni(title);

        try
        {
            // 清空文件名缓冲区
            for (var i = 0; i < MaxPathChars * 2; i++)
                Marshal.WriteByte(filePtr, i, 0);

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = ownerHwnd,
                lpstrFilter = filterPtr,
                nFilterIndex = 1,
                lpstrFile = filePtr,
                nMaxFile = MaxPathChars,
                lpstrTitle = titlePtr,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR
            };

            if (!GetOpenFileNameW(ref ofn))
                return null;

            return Marshal.PtrToStringUni(filePtr);
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(titlePtr);
        }
    }

    /// <summary>构造 GetOpenFileName 所需的双空终止过滤器缓冲区。</summary>
    private static IntPtr AllocFilter(params string[] nameAndSpecPairs)
    {
        if (nameAndSpecPairs.Length == 0 || nameAndSpecPairs.Length % 2 != 0)
            throw new ArgumentException("过滤器需成对提供名称与扩展名。", nameof(nameAndSpecPairs));

        var bytes = new List<byte>();
        foreach (var part in nameAndSpecPairs)
        {
            bytes.AddRange(Encoding.Unicode.GetBytes(part));
            bytes.Add(0);
            bytes.Add(0);
        }

        // 额外的终止空字符
        bytes.Add(0);
        bytes.Add(0);

        var ptr = Marshal.AllocHGlobal(bytes.Count);
        Marshal.Copy(bytes.ToArray(), 0, ptr, bytes.Count);
        return ptr;
    }

    private const int MaxPathChars = 520;
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_HIDEREADONLY = 0x00000004;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME lpofn);
}
