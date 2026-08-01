using GratingPlayer.Core;
using GratingPlayer.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GratingPlayer;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public static PlayerWindow? ActivePlayer { get; set; }

    /// <summary>启动时所在显示器；仅在 OnLaunched 之后可用。</summary>
    public static DisplayArea LaunchDisplay { get; private set; } = null!;

    public static void CloseActivePlayer(bool silent = false)
    {
        if (ActivePlayer is not { } player)
            return;

        ActivePlayer = null;
        if (silent)
        {
            player.SuppressExitEvent = true;
            try
            {
                player.ForceClose();
            }
            catch
            {
                try { player.Close(); } catch { /* ignore */ }
            }
            return;
        }

        try
        {
            player.RequestExit(PlayerExitReason.UserExit);
        }
        catch
        {
            try { player.Close(); } catch { /* ignore */ }
        }
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GratingPlayer");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [App.UnhandledException] {e.Exception}\r\n\r\n");
        }
        catch
        {
            // ignore
        }

        // 记日志后吞掉，避免未处理异常直接闪退
        e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        // 切勿在类型字段初始化里访问 DisplayArea（WinRT 可能尚未就绪）
        LaunchDisplay = DisplayHelper.GetLaunchDisplayArea();

        MainWindow = new Window
        {
            Title = "照片放映器"
        };

        var rootFrame = new Frame();
        rootFrame.NavigationFailed += OnNavigationFailed;
        rootFrame.Navigate(typeof(MainPage), e.Arguments);
        MainWindow.Content = rootFrame;

        // 设置窗放到启动屏中央（DIP→物理像素，避免高 DPI 裁切）
        try
        {
            DisplayHelper.PlaceSettingsWindow(MainWindow, LaunchDisplay);
        }
        catch
        {
            // 定位失败不阻止启动
        }

        MainWindow.Activate();

        // XamlRoot 就绪后再按真实缩放校正一次尺寸
        if (MainWindow.Content is FrameworkElement fe)
        {
            fe.Loaded += (_, _) =>
            {
                if (MainWindow is null)
                    return;
                try
                {
                    DisplayHelper.PlaceSettingsWindow(MainWindow, LaunchDisplay);
                }
                catch
                {
                    // ignore
                }
            };
        }
    }

    private static void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
    }
}
