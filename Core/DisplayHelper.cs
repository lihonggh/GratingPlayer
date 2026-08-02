using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace GratingPlayer.Core;

public static class DisplayHelper
{
    public const int SettingsWidthDip = 689;
    public const int SettingsHeightDip = 802;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private enum MonitorDpiType
    {
        EffectiveDpi = 0,
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorInfoFPrimary = 0x00000001;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    /// <summary>根据鼠标所在位置解析当前显示器（启动时所在屏）。</summary>
    public static DisplayArea GetLaunchDisplayArea()
    {
        try
        {
            if (GetCursorPos(out var pt))
            {
                return DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest);
            }
        }
        catch
        {
            // WinRT 未就绪时回退
        }

        return TryGetPrimaryDisplay();
    }

    public static DisplayArea TryGetPrimaryDisplay()
    {
        try
        {
            return DisplayArea.Primary;
        }
        catch
        {
            // 最后手段：FindAll 第一项
        }

        try
        {
            var all = DisplayArea.FindAll();
            if (all.Count > 0)
                return all[0];
        }
        catch
        {
            // ignore
        }

        throw new InvalidOperationException("无法获取显示器信息。");
    }

    /// <summary>
    /// 按从左到右、从上到下稳定排序的显示器列表。
    /// 优先用 Win32 EnumDisplayMonitors（多屏可靠），再映射为 DisplayArea。
    /// </summary>
    public static IReadOnlyList<DisplayArea> GetOrderedDisplays()
    {
        var fromWin32 = EnumerateDisplayAreasViaWin32();
        if (fromWin32.Count > 0)
            return fromWin32;

        // 回退：WinAppSDK FindAll（部分环境多屏不全）
        try
        {
            return DisplayArea.FindAll()
                .OrderBy(d => d.OuterBounds.X)
                .ThenBy(d => d.OuterBounds.Y)
                .ToList();
        }
        catch
        {
            try
            {
                return [TryGetPrimaryDisplay()];
            }
            catch
            {
                return [];
            }
        }
    }

    private static List<DisplayArea> EnumerateDisplayAreasViaWin32()
    {
        var monitors = new List<(RECT Bounds, bool IsPrimary)>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT __, IntPtr ___) =>
            {
                var info = new MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<MONITORINFOEX>()
                };
                if (!GetMonitorInfo(hMonitor, ref info))
                    return true;

                monitors.Add((info.rcMonitor, (info.dwFlags & MonitorInfoFPrimary) != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            return [];
        }

        if (monitors.Count == 0)
            return [];

        var ordered = monitors
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();

        var result = new List<DisplayArea>(ordered.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in ordered)
        {
            try
            {
                var cx = m.Bounds.Left + Math.Max(1, (m.Bounds.Right - m.Bounds.Left) / 2);
                var cy = m.Bounds.Top + Math.Max(1, (m.Bounds.Bottom - m.Bounds.Top) / 2);
                var area = DisplayArea.GetFromPoint(new PointInt32(cx, cy), DisplayAreaFallback.Nearest);
                var key = $"{area.OuterBounds.X},{area.OuterBounds.Y},{area.OuterBounds.Width},{area.OuterBounds.Height}";
                if (!seen.Add(key))
                    continue;
                result.Add(area);
            }
            catch
            {
                // 跳过无法映射的显示器
            }
        }

        return result;
    }

    /// <summary>窗口当前所在显示器。</summary>
    public static DisplayArea GetWindowDisplay(Window window)
    {
        try
        {
            var appWindow = GetAppWindow(window);
            return DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        }
        catch
        {
            return GetLaunchDisplayArea();
        }
    }

    /// <summary>
    /// 解析播放目标屏：index=0 为窗口当前屏；1..N 为 <see cref="GetOrderedDisplays"/> 中的第 N 块。
    /// </summary>
    public static DisplayArea ResolvePlaybackDisplay(Window window, int playbackDisplayIndex)
    {
        if (playbackDisplayIndex <= 0)
            return GetWindowDisplay(window);

        var list = GetOrderedDisplays();
        var i = playbackDisplayIndex - 1;
        if ((uint)i < (uint)list.Count)
            return list[i];

        return GetWindowDisplay(window);
    }

    public static AppWindow GetAppWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    /// <summary>获取显示器缩放（相对 96 DPI）。AppWindow 尺寸单位是物理像素。</summary>
    public static double GetDisplayScale(DisplayArea display)
    {
        try
        {
            var center = new POINT
            {
                X = display.OuterBounds.X + display.OuterBounds.Width / 2,
                Y = display.OuterBounds.Y + display.OuterBounds.Height / 2,
            };
            var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, MonitorDpiType.EffectiveDpi, out var dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // ignore
        }

        return 1.0;
    }

    public static double GetWindowScale(Window window)
    {
        if (window.Content?.XamlRoot is { } root && root.RasterizationScale > 0)
            return root.RasterizationScale;

        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
        return GetDisplayScale(display);
    }

    /// <summary>将窗口放到指定显示器中央（设置窗）。宽高为 DIP，内部换算为物理像素。</summary>
    public static void PlaceWindowOnDisplay(Window window, DisplayArea display, int widthDip, int heightDip)
    {
        var appWindow = GetAppWindow(window);
        var scale = GetDisplayScale(display);
        // 优先用窗口已就绪的 XamlRoot 缩放
        if (window.Content?.XamlRoot is { RasterizationScale: > 0 } root)
            scale = root.RasterizationScale;

        var width = Math.Max(320, (int)Math.Ceiling(widthDip * scale));
        var height = Math.Max(360, (int)Math.Ceiling(heightDip * scale));

        var bounds = display.WorkArea;
        width = Math.Min(width, bounds.Width);
        height = Math.Min(height, bounds.Height);

        var x = bounds.X + Math.Max(0, (bounds.Width - width) / 2);
        var y = bounds.Y + Math.Max(0, (bounds.Height - height) / 2);
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    public static void PlaceSettingsWindow(Window window, DisplayArea display)
    {
        ConfigureSettingsPresenter(window);
        PlaceWindowOnDisplay(window, display, SettingsWidthDip, SettingsHeightDip);
    }

    /// <summary>设置窗固定尺寸，禁止最大化/拉伸。</summary>
    public static void ConfigureSettingsPresenter(Window window)
    {
        try
        {
            var appWindow = GetAppWindow(window);
            if (appWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped)
                appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

            if (appWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.SetBorderAndTitleBar(true, true);
                overlapped.IsResizable = false;
                overlapped.IsMaximizable = false;
                overlapped.IsMinimizable = true;
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 在指定显示器上进入全屏。
    /// 扩展屏/不同 DPI 下需先 Move 到目标屏再 Resize，最后 FullScreen，并用 Win32 钉边。
    /// </summary>
    public static void EnterFullscreenOnDisplay(Window window, DisplayArea display)
    {
        var appWindow = GetAppWindow(window);
        var bounds = display.OuterBounds;
        var hwnd = WindowNative.GetWindowHandle(window);

        // 先回到可定位的 overlapped，去掉边框标题，避免扩展屏全屏留缝
        appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (appWindow.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.SetBorderAndTitleBar(false, false);
            overlapped.IsResizable = false;
            overlapped.IsMaximizable = false;
            overlapped.IsMinimizable = false;
        }

        // PerMonitorV2：必须先落到目标屏，DPI 上下文才会切换；Move/Resize 分开更稳
        appWindow.Move(new PointInt32(bounds.X + 8, bounds.Y + 8));
        appWindow.Move(new PointInt32(bounds.X, bounds.Y));
        appWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));
        appWindow.MoveAndResize(bounds);

        // FullScreen 以“窗口当前所在屏”为准
        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        // 再钉一次物理外接矩形，消除扩展屏边角未盖满
        CoverDisplayWithWin32(hwnd, bounds);
    }

    /// <summary>再次强制窗口盖住目标屏外接矩形（全屏后补一次）。</summary>
    public static void ReassertFullscreenOnDisplay(Window window, DisplayArea display)
    {
        var appWindow = GetAppWindow(window);
        var bounds = display.OuterBounds;
        var hwnd = WindowNative.GetWindowHandle(window);

        if (appWindow.Presenter is not FullScreenPresenter)
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        CoverDisplayWithWin32(hwnd, bounds);
    }

    private static void CoverDisplayWithWin32(IntPtr hwnd, RectInt32 bounds)
    {
        if (hwnd == IntPtr.Zero)
            return;

        SetWindowPos(
            hwnd,
            HwndTop,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            SwpShowWindow | SwpFrameChanged);
    }

    public static void ExitFullscreen(Window window, DisplayArea display)
    {
        var appWindow = GetAppWindow(window);
        appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        ConfigureSettingsPresenter(window);
        PlaceSettingsWindow(window, display);
    }
}
