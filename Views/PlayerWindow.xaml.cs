using GratingPlayer.Controls;
using GratingPlayer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;

namespace GratingPlayer.Views;

public sealed partial class PlayerWindow : Window
{
    private readonly List<FlipStrip> _strips = [];
    private readonly List<string> _images = [];
    private readonly object _imagesLock = new();
    private ImageDirectoryMonitor? _monitor;

    private AppSettings _settings = new();
    private PlayerSessionMode _mode;
    private bool _standbySession;
    private DisplayArea _playbackDisplay = null!;
    private DisplayArea _returnDisplay = null!;

    private DispatcherTimer? _activityTimer;
    private bool _paused;
    private int _newImageDisplayIndex = -1;
    private bool _newImagePresenting;
    private CancellationTokenSource? _playCts;
    private int _index;
    private BitmapImage? _currentBitmap;
    private BitmapImage? _nextBitmap;
    private bool _boardSized;
    private string? _placeholderPath;
    private bool _exitRaised;
    private bool _sessionRunning;
    private MediaPlayer? _musicPlayer;
    /// <summary>按次数播放时，还剩几遍（含当前正在播的一遍结束后的剩余）。</summary>
    private int _musicPlaysRemaining;

    internal bool SuppressExitEvent { get; set; }

    public event Action<PlayerExitReason>? Exited;

    public DisplayArea ReturnDisplay => _returnDisplay;

    public PlayerWindow()
    {
        InitializeComponent();
        Title = "照片放映器";

        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), handledEventsToo: true);

        // Esc / 空格仅走 KeyDown，不注册 KeyboardAccelerator，避免弹出 Esc 悬浮键位提示

        Closed += (_, _) =>
        {
            Safe("PlayerWindow.Closed", () =>
            {
                StopBackgroundMusic();
                if (App.ActivePlayer == this)
                    App.ActivePlayer = null;
            });
        };
    }

    private void Safe(string context, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogUiException(context, ex);
        }
    }

    private async void SafeAsync(string context, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            LogUiException(context, ex);
        }
    }

    private static void LogUiException(string context, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GratingPlayer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {ex}\r\n\r\n");
        }
        catch
        {
            // ignore
        }
    }

    public void BeginSession(
        PlayerSessionMode mode,
        AppSettings settings,
        DisplayArea targetDisplay,
        DisplayArea returnDisplay,
        IReadOnlyList<string>? initialImages,
        IReadOnlyList<string>? knownExisting,
        string? placeholderPath,
        bool standbySession)
    {
        _mode = mode;
        _settings = settings;
        _playbackDisplay = targetDisplay;
        _returnDisplay = returnDisplay;
        _standbySession = standbySession;
        _placeholderPath = placeholderPath;
        _sessionRunning = true;
        _paused = false;
        _newImageDisplayIndex = -1;
        _newImagePresenting = false;

        lock (_imagesLock)
        {
            _images.Clear();
            if (mode == PlayerSessionMode.Carousel && initialImages is not null)
            {
                _images.AddRange(initialImages);
                _index = 0;
            }
        }

        // 先激活再全屏：扩展屏需窗口先存在，FullScreen 才会落在正确显示器
        Activate();
        DisplayHelper.EnterFullscreenOnDisplay(this, targetDisplay);
        EnsurePlaybackFocus();
        _ = ReassertFullscreenAfterLayoutAsync(targetDisplay);

        // 音乐与画面分线：不阻塞开播。新图+「1次」改在每张新图展示时触发。
        if (ShouldStartMusicWithSession())
            _ = StartBackgroundMusicAsync(loop: IsMusicLoop());

        if (mode == PlayerSessionMode.Carousel)
        {
            RestartPlaybackMonitor();
            if (_standbySession && _settings.PlayMode == PlayMode.IdleStandby)
                StartActivityWatch();
            _ = RunCarouselSessionAsync();
            return;
        }

        _monitor?.Dispose();
        _monitor = new ImageDirectoryMonitor(DispatcherQueue);
        _monitor.StableFilesAdded += OnStableFilesAdded;
        _monitor.Start(
            _settings.WatchFolder,
            _settings.IncludeSubdirectories,
            TimeSpan.FromSeconds(_settings.FileStableSeconds),
            knownExisting ?? []);

        _ = RunNewImageSessionAsync();
    }

    public void ForceClose()
    {
        _sessionRunning = false;
        StopActivityWatch();
        _playCts?.Cancel();
        _playCts = null;
        _monitor?.Dispose();
        _monitor = null;
        StopBackgroundMusic();
        Close();
    }

    public void RequestExit(PlayerExitReason reason)
    {
        if (_exitRaised)
            return;

        _exitRaised = true;
        _sessionRunning = false;
        StopActivityWatch();
        _playCts?.Cancel();
        _playCts = null;
        _monitor?.Dispose();
        _monitor = null;

        StopBackgroundMusic();

        if (!SuppressExitEvent)
            Exited?.Invoke(reason);

        Close();
    }

    private bool IsMusicLoop() => _settings.MusicPlayCount != MusicPlayCount.Times;

    private int GetMusicRepeatTimes()
        => Math.Clamp(_settings.MusicRepeatTimes <= 0 ? 1 : _settings.MusicRepeatTimes, 1, 1000);

    /// <summary>
    /// 连续/待机：进入会话即播音乐。
    /// 新图+循环：会话开始即循环。
    /// 新图+指定次数：等每张新图开始展示时再按次数播放。
    /// </summary>
    private bool ShouldStartMusicWithSession()
    {
        if (_mode == PlayerSessionMode.Carousel)
            return true;
        return IsMusicLoop();
    }

    /// <summary>不阻塞画面。循环或按次数启播；退出会话时立刻停，未播完的次数作废。</summary>
    private async Task StartBackgroundMusicAsync(bool loop)
    {
        StopBackgroundMusic();

        var path = _settings.MusicFilePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!_sessionRunning)
                return;

            _musicPlaysRemaining = loop ? 0 : GetMusicRepeatTimes();

            var player = new MediaPlayer
            {
                IsLoopingEnabled = loop,
                AudioCategory = MediaPlayerAudioCategory.Media,
                Volume = 1
            };
            player.MediaEnded += MusicPlayer_MediaEnded;
            player.Source = MediaSource.CreateFromStorageFile(file);
            _musicPlayer = player;

            if (!_sessionRunning || _paused)
                return;

            player.Play();
        }
        catch (Exception ex)
        {
            LogUiException(nameof(StartBackgroundMusicAsync), ex);
            StopBackgroundMusic();
        }
    }

    private void MusicPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        // 退出播放后不再续播剩余次数
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                Safe(nameof(MusicPlayer_MediaEnded), () =>
                {
                    if (!ReferenceEquals(sender, _musicPlayer))
                        return;
                    if (!_sessionRunning || sender.IsLoopingEnabled)
                        return;

                    _musicPlaysRemaining--;
                    if (_musicPlaysRemaining <= 0 || _paused)
                        return;

                    try
                    {
                        sender.PlaybackSession.Position = TimeSpan.Zero;
                        sender.Play();
                    }
                    catch (Exception ex)
                    {
                        LogUiException("MusicPlayer_MediaEnded.Replay", ex);
                    }
                });
            });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>新图 + 指定次数：每张新图开始展示时按设定次数重头播放。</summary>
    private void PlayMusicTimesForNewImage()
    {
        if (IsMusicLoop())
            return;
        _ = StartBackgroundMusicAsync(loop: false);
    }

    private void StopBackgroundMusic()
    {
        _musicPlaysRemaining = 0;
        var player = _musicPlayer;
        _musicPlayer = null;
        if (player is null)
            return;

        try
        {
            player.MediaEnded -= MusicPlayer_MediaEnded;
            player.Pause();
            player.Source = null;
            player.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private async Task ReassertFullscreenAfterLayoutAsync(DisplayArea display)
    {
        try
        {
            // DPI/布局切换后补钉两次，解决扩展屏边角未盖满
            await Task.Delay(50);
            if (!_sessionRunning)
                return;
            DisplayHelper.ReassertFullscreenOnDisplay(this, display);
            await Task.Delay(100);
            if (!_sessionRunning)
                return;
            DisplayHelper.ReassertFullscreenOnDisplay(this, display);
            await EnsureBoardReadyAsync();
        }
        catch
        {
            // ignore
        }
    }

    private async Task RunCarouselSessionAsync()
    {
        await Task.Delay(80);
        await EnsureBoardReadyAsync();
        EnsurePlaybackFocus();

        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        await RunCarouselAsync(_playCts.Token);
    }

    private async Task RunNewImageSessionAsync()
    {
        await Task.Delay(80);
        await EnsureBoardReadyAsync();
        await ShowNewImageWaitingPlaceholderAsync();
        EnsurePlaybackFocus();
    }

    private (double Width, double Height) GetBoardDipSize()
    {
        // XAML 尺寸是 DIP；OuterBounds 是物理像素，需按缩放换算
        var scale = Content?.XamlRoot is { RasterizationScale: > 0 } root
            ? root.RasterizationScale
            : DisplayHelper.GetDisplayScale(_playbackDisplay);
        if (scale < 0.5)
            scale = 1;

        var bounds = _playbackDisplay.OuterBounds;
        var w = Math.Max(2, bounds.Width / scale);
        var h = Math.Max(2, bounds.Height / scale);

        // 布局就绪后优先用实际可视尺寸
        if (RootGrid.ActualWidth >= 2 && RootGrid.ActualHeight >= 2)
        {
            w = RootGrid.ActualWidth;
            h = RootGrid.ActualHeight;
        }
        else if (BoardCanvas.ActualWidth >= 2 && BoardCanvas.ActualHeight >= 2)
        {
            w = BoardCanvas.ActualWidth;
            h = BoardCanvas.ActualHeight;
        }

        return (w, h);
    }

    private async Task EnsureBoardReadyAsync()
    {
        var deadline = Environment.TickCount64 + 3000;
        while ((RootGrid.ActualWidth < 2 || RootGrid.ActualHeight < 2)
               && Environment.TickCount64 < deadline)
        {
            await Task.Delay(16);
        }

        var (w, h) = GetBoardDipSize();
        BoardCanvas.Width = w;
        BoardCanvas.Height = h;
        _boardSized = w >= 2 && h >= 2;
    }

    private async Task RunCarouselAsync(CancellationToken token)
    {
        try
        {
            string firstPath;
            string secondPath;
            lock (_imagesLock)
            {
                if (_images.Count < 2)
                    return;
                firstPath = _images[0];
                secondPath = _images[1];
            }

            _currentBitmap = await LoadBitmapAsync(firstPath);
            if (_currentBitmap is null)
                return;

            var firstNext = await LoadBitmapAsync(secondPath);
            if (firstNext is null)
                return;

            RebuildBoard(_currentBitmap, firstNext);
            _nextBitmap = firstNext;

            while (!token.IsCancellationRequested && _sessionRunning)
            {
                while (_paused)
                    await Task.Delay(200, token);

                var dwellMs = (int)(_settings.DwellSeconds * 1000);
                await Task.Delay(dwellMs, token);

                while (_paused)
                    await Task.Delay(200, token);

                string fromPath;
                string toPath;
                int nextIndex;
                lock (_imagesLock)
                {
                    if (_images.Count < 2)
                        continue;

                    if (_index >= _images.Count)
                        _index = 0;

                    nextIndex = (_index + 1) % _images.Count;
                    fromPath = _images[_index];
                    toPath = _images[nextIndex];
                }

                _currentBitmap = await LoadBitmapAsync(fromPath) ?? _currentBitmap;
                _nextBitmap = await LoadBitmapAsync(toPath);
                if (_currentBitmap is null || _nextBitmap is null)
                {
                    _index = nextIndex;
                    continue;
                }

                RebuildBoard(_currentBitmap, _nextBitmap);
                await RunFlipWaveAsync(
                    leftToRight: nextIndex % 2 == 1,
                    TimeSpan.FromMilliseconds(_settings.FlipDurationMs),
                    TimeSpan.FromMilliseconds(_settings.StaggerMs),
                    token);

                _index = nextIndex;
                _currentBitmap = _nextBitmap;
                DispatcherQueue.TryEnqueue(EnsurePlaybackFocus);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RebuildBoard(BitmapImage front, BitmapImage back)
    {
        var (w, h) = GetBoardDipSize();

        var count = Math.Clamp(_settings.StripCount, 1, 200);
        var orientation = _settings.StripOrientation;
        BoardCanvas.Children.Clear();
        _strips.Clear();

        BoardCanvas.Width = w;
        BoardCanvas.Height = h;
        BoardCanvas.Orientation = orientation == StripOrientation.Horizontal
            ? Orientation.Vertical
            : Orientation.Horizontal;

        for (var i = 0; i < count; i++)
        {
            var strip = new FlipStrip();
            strip.Configure(front, back, i, count, w, h, orientation);
            _strips.Add(strip);
            BoardCanvas.Children.Add(strip);
        }
    }

    private async Task RunFlipWaveAsync(
        bool leftToRight,
        TimeSpan duration,
        TimeSpan stagger,
        CancellationToken ct)
    {
        var flipDir = leftToRight ? 1 : -1;
        var n = _strips.Count;
        var tasks = new List<Task>(n);
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var orderIndex = leftToRight ? i : (n - 1 - i);
            var delay = TimeSpan.FromMilliseconds(stagger.TotalMilliseconds * orderIndex);
            tasks.Add(FlipAfterDelayAsync(_strips[i], delay, duration, flipDir, ct));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task FlipAfterDelayAsync(
        FlipStrip strip,
        TimeSpan delay,
        TimeSpan duration,
        int flipDirection,
        CancellationToken ct)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
        ct.ThrowIfCancellationRequested();
        await strip.FlipAsync(duration, flipDirection);
    }

    private static async Task<BitmapImage?> LoadBitmapAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bmp = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical
            };

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var pw = decoder.PixelWidth;
            var ph = decoder.PixelHeight;
            const uint maxEdge = 1920;
            var longEdge = Math.Max(pw, ph);
            if (longEdge > maxEdge)
            {
                if (pw >= ph)
                    bmp.DecodePixelWidth = (int)maxEdge;
                else
                    bmp.DecodePixelHeight = (int)maxEdge;
            }

            stream.Seek(0);
            await bmp.SetSourceAsync(stream);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private async Task ShowNewImageWaitingPlaceholderAsync()
    {
        if (!string.IsNullOrWhiteSpace(_placeholderPath))
            await ShowStillAsync(_placeholderPath);
        else
            ClearBoard();

        SetNewImageHint("等待新图片… · Esc 结束监控");
    }

    private async Task PresentPendingNewImagesAsync()
    {
        if (_newImagePresenting)
            return;

        _newImagePresenting = true;
        try
        {
            while (_sessionRunning && _mode == PlayerSessionMode.NewImageOnly)
            {
                string? nextPath;
                int nextIndex;
                bool isFirst;
                lock (_imagesLock)
                {
                    nextIndex = _newImageDisplayIndex + 1;
                    if (nextIndex >= _images.Count)
                        break;
                    nextPath = _images[nextIndex];
                    isFirst = _newImageDisplayIndex < 0;
                }

                if (string.IsNullOrWhiteSpace(nextPath))
                {
                    _newImageDisplayIndex = nextIndex;
                    continue;
                }

                await EnsureBoardReadyAsync();

                // 新图 + 指定次数：每张新图开始展示时按次数播放（与画面并行；退出则立刻停）
                PlayMusicTimesForNewImage();

                if (isFirst)
                {
                    await ShowStillAsync(nextPath);
                    _newImageDisplayIndex = nextIndex;
                    _index = nextIndex;
                    SetNewImageHint("已展示第 1 张新图，等待下一张翻转 · Esc 结束");
                }
                else
                {
                    var ok = await FlipOnceToAsync(nextPath, leftToRight: nextIndex % 2 == 1);
                    if (!_sessionRunning || _mode != PlayerSessionMode.NewImageOnly)
                        break;
                    if (ok)
                    {
                        _newImageDisplayIndex = nextIndex;
                        _index = nextIndex;
                        SetNewImageHint($"已翻转展示第 {nextIndex + 1} 张，等待下一张 · Esc 结束");
                    }
                    else
                    {
                        _newImageDisplayIndex = nextIndex;
                    }
                }
            }
        }
        finally
        {
            _newImagePresenting = false;

            var more = false;
            lock (_imagesLock)
                more = _newImageDisplayIndex + 1 < _images.Count;
            if (more && _sessionRunning && _mode == PlayerSessionMode.NewImageOnly)
                _ = PresentPendingNewImagesAsync();
        }
    }

    private void SetNewImageHint(string text)
    {
        // 播放层不展示悬浮提示，仅保留接口以免调用处改动过大
        _ = text;
    }

    private async Task ShowStillAsync(string path)
    {
        var bmp = await LoadBitmapAsync(path);
        if (bmp is null)
            return;
        _currentBitmap = bmp;
        _nextBitmap = bmp;
        RebuildBoard(bmp, bmp);
    }

    private async Task<bool> FlipOnceToAsync(string toPath, bool leftToRight)
    {
        var toBmp = await LoadBitmapAsync(toPath);
        if (toBmp is null)
            return false;

        var fromBmp = _currentBitmap ?? toBmp;
        RebuildBoard(fromBmp, toBmp);
        _nextBitmap = toBmp;

        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        var token = _playCts.Token;

        try
        {
            await RunFlipWaveAsync(
                leftToRight,
                TimeSpan.FromMilliseconds(_settings.FlipDurationMs),
                TimeSpan.FromMilliseconds(_settings.StaggerMs),
                token);
            _currentBitmap = toBmp;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void ClearBoard()
    {
        BoardCanvas.Children.Clear();
        _strips.Clear();
    }

    private void StartActivityWatch()
    {
        StopActivityWatch();
        if (_settings.PlayMode != PlayMode.IdleStandby || !_standbySession)
            return;

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _activityTimer.Tick += ActivityTimer_Tick;
        _activityTimer.Start();
    }

    private void StopActivityWatch()
    {
        if (_activityTimer is not null)
        {
            _activityTimer.Tick -= ActivityTimer_Tick;
            _activityTimer.Stop();
            _activityTimer = null;
        }
    }

    private void ActivityTimer_Tick(object? sender, object e)
    {
        Safe(nameof(ActivityTimer_Tick), () =>
        {
            if (!_sessionRunning || !_standbySession || _settings.PlayMode != PlayMode.IdleStandby)
                return;

            if (SystemIdleHelper.GetIdleSeconds() < 0.35)
                RequestExit(PlayerExitReason.ActivityInterrupt);
        });
    }

    private void RestartPlaybackMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;

        if (!_settings.WatchNewFiles || !_settings.HasFolder)
            return;

        List<string> known;
        lock (_imagesLock)
            known = [.. _images];

        _monitor = new ImageDirectoryMonitor(DispatcherQueue);
        _monitor.StableFilesAdded += OnStableFilesAdded;
        _monitor.Start(
            _settings.WatchFolder,
            _settings.IncludeSubdirectories,
            TimeSpan.FromSeconds(_settings.FileStableSeconds),
            known);
    }

    private void OnStableFilesAdded(IReadOnlyList<string> files)
    {
        Safe(nameof(OnStableFilesAdded), () =>
        {
            if (files.Count == 0 || !_sessionRunning)
                return;

            if (_mode == PlayerSessionMode.NewImageOnly)
            {
                var added = AppendPathsToEnd(files);
                if (added > 0)
                    SafeAsync(nameof(OnNewImageSessionFilesAddedAsync), () => OnNewImageSessionFilesAddedAsync(added));
                return;
            }

            AppendToPlayQueue(files);
        });
    }

    private async Task OnNewImageSessionFilesAddedAsync(int added)
    {
        int pending;
        lock (_imagesLock)
            pending = Math.Max(0, _images.Count - (_newImageDisplayIndex + 1));

        if (pending > 0)
            SetNewImageHint($"检测到 +{added} 张新图，待展示 {pending} 张 · Esc 结束");

        await PresentPendingNewImagesAsync();
    }

    private int AppendPathsToEnd(IReadOnlyList<string> files)
    {
        var added = 0;
        lock (_imagesLock)
        {
            foreach (var path in files)
            {
                if (_images.Contains(path, StringComparer.OrdinalIgnoreCase))
                    continue;
                _images.Add(path);
                added++;
            }
        }

        return added;
    }

    private void AppendToPlayQueue(IReadOnlyList<string> files)
    {
        var added = 0;
        lock (_imagesLock)
        {
            if (_settings.NewFileAppendMode == NewFileAppendMode.NextPlayPosition)
            {
                var insertAt = Math.Min(_index + 1, _images.Count);
                foreach (var path in files)
                {
                    if (_images.Contains(path, StringComparer.OrdinalIgnoreCase))
                        continue;
                    _images.Insert(insertAt, path);
                    insertAt++;
                    added++;
                }
            }
            else
            {
                foreach (var path in files)
                {
                    if (_images.Contains(path, StringComparer.OrdinalIgnoreCase))
                        continue;
                    _images.Add(path);
                    added++;
                }
            }
        }

        _ = added;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        Safe(nameof(RootGrid_KeyDown), () =>
        {
            if (!_sessionRunning)
                return;

            if (e.Key == VirtualKey.Escape)
            {
                RequestExit(PlayerExitReason.UserExit);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Space)
            {
                TogglePause();
                e.Handled = true;
            }
        });
    }

    private void EnsurePlaybackFocus()
    {
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void TogglePause()
    {
        if (!_sessionRunning || _mode == PlayerSessionMode.NewImageOnly)
            return;

        _paused = !_paused;
        try
        {
            if (_musicPlayer is not null)
            {
                if (_paused)
                    _musicPlayer.Pause();
                else
                    _musicPlayer.Play();
            }
        }
        catch
        {
            // ignore
        }

        EnsurePlaybackFocus();
    }


    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        Safe(nameof(RootGrid_RightTapped), () =>
        {
            if (!_sessionRunning)
                return;

            EnsurePlaybackFocus();
        });
    }

    private void ExitSoftwareMenu_Click(object sender, RoutedEventArgs e)
    {
        Safe(nameof(ExitSoftwareMenu_Click), () => RequestExit(PlayerExitReason.UserExit));
    }

}
