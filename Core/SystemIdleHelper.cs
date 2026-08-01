using System.Runtime.InteropServices;

namespace GratingPlayer.Core;

/// <summary>读取系统级键鼠最后输入时间，用于待机屏判定。</summary>
public static class SystemIdleHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    /// <summary>系统当前空闲秒数（自最后一次键鼠输入起）。</summary>
    public static double GetIdleSeconds()
    {
        var info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
            return 0;

        var idleMs = GetTickCount() - info.dwTime;
        return idleMs / 1000.0;
    }
}
