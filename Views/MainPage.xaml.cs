using GratingPlayer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GratingPlayer.Views;

public sealed partial class MainPage : Page
{
    private readonly AppSettings _settings;
    private ImageDirectoryMonitor? _monitor;

    private DispatcherTimer? _idleTimer;
    private bool _idleWatching;
    /// <summary>用户已点击「进入待机监控」，在取消前循环：空闲→播放→有操作→再等待。</summary>
    private bool _standbySession;
    /// <summary>用户已点击「进入新图监控」：仅播放监控开始后出现的新图片。</summary>
    private bool _newImageSession;
    private bool _suppressSettingsEvents;
    private int _schemeApplyToken;
    private bool _unlockPromptOpen;
    private DisplayArea? _lastReturnDisplay;
    private PlayerWindow? _subscribedPlayer;

    public MainPage()
    {
        InitializeComponent();
        StripCountBox.Minimum = 1;
        StripCountBox.Maximum = 200;
        _settings = AppSettings.Load();
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
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

        try
        {
            System.Diagnostics.Debug.WriteLine($"[{context}] {ex}");
        }
        catch
        {
            // ignore
        }
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Safe(nameof(MainPage_Loaded), () =>
        {
            RootGrid.Focus(FocusState.Programmatic);
            ApplySettingsToUi(rebuildSchemeList: true);
            DeferredRefreshPlaybackDisplayCombo();
            RefreshImageCount();
            RestartSettingsMonitor();
            StartPlaybackTrigger();
        });
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Safe(nameof(MainPage_Unloaded), () =>
        {
            StopAllSessions();
            _monitor?.Dispose();
            _monitor = null;
        });
    }

    private bool IsPlayerOpen => App.ActivePlayer is not null;

    /// <param name="rebuildSchemeList">
    /// false：仅同步选中项，不清空 ComboBox（用于 SelectionChanged 内切换，避免 COM 闪退）。
    /// </param>
    private void ApplySettingsToUi(bool rebuildSchemeList = true)
    {
        _suppressSettingsEvents = true;
        try
        {
            if (rebuildSchemeList)
                RefreshSchemeCombo();
            else
                SelectSchemeComboTag(_settings.ActiveSchemeId ?? string.Empty);

            FolderTextBox.Text = _settings.WatchFolder;
            IncludeSubdirsCheckBox.IsChecked = _settings.IncludeSubdirectories;
            WatchNewFilesCheckBox.IsChecked = _settings.WatchNewFiles;
            AppendEndRadio.IsChecked = _settings.NewFileAppendMode != NewFileAppendMode.NextPlayPosition;
            AppendNextRadio.IsChecked = _settings.NewFileAppendMode == NewFileAppendMode.NextPlayPosition;
            SelectComboByTag(PlayOrderCombo, _settings.PlayOrder.ToString());
            SelectComboByTag(PlayModeCombo, _settings.PlayMode.ToString());
            IdleSecondsBox.Value = _settings.IdleSeconds;
            UpdateIdleSecondsVisibility();
            SelectComboByTag(StripOrientationCombo, _settings.StripOrientation.ToString());
            StripCountBox.Minimum = 1;
            StripCountBox.Maximum = 200;
            StripCountBox.Value = _settings.StripCount;
            FlipDurationBox.Value = _settings.FlipDurationMs / 1000.0;
            DwellBox.Value = _settings.DwellSeconds;
            UpdateWatchAppendRadiosEnabled();
            UpdateSchemeModeUi();
        }
        finally
        {
            _suppressSettingsEvents = false;
        }

        UpdatePlayButtonUi();
    }

    private void RefreshSchemeCombo()
    {
        var tag = _settings.ActiveSchemeId ?? string.Empty;
        SchemeCombo.Items.Clear();
        SchemeCombo.Items.Add(new ComboBoxItem
        {
            Content = "自定义设置",
            Tag = ""
        });

        foreach (var scheme in _settings.Schemes
                     .OrderByDescending(s => s.CreatedAt)
                     .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            SchemeCombo.Items.Add(new ComboBoxItem
            {
                Content = $"方案：{scheme.Name}",
                Tag = scheme.Id
            });
        }

        SelectSchemeComboTag(tag);
    }

    private void SelectSchemeComboTag(string tag)
    {
        SelectComboByTag(SchemeCombo, tag);
        if (SchemeCombo.SelectedItem is null && SchemeCombo.Items.Count > 0)
            SchemeCombo.SelectedIndex = 0;
    }

    /// <summary>在下一帧重建方案下拉，避开 ComboBox SelectionChanged 重入。</summary>
    private void DeferredRefreshSchemeCombo()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Safe("DeferredRefreshSchemeCombo", () =>
            {
                _suppressSettingsEvents = true;
                try
                {
                    RefreshSchemeCombo();
                    UpdateSchemeModeUi();
                }
                finally
                {
                    _suppressSettingsEvents = false;
                }
            });
        });
    }

    private void DeferredRefreshPlaybackDisplayCombo()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded)
                return;
            Safe("DeferredRefreshPlaybackDisplayCombo", () =>
            {
                _suppressSettingsEvents = true;
                try
                {
                    RefreshPlaybackDisplayCombo(preserveSelection: true);
                }
                finally
                {
                    _suppressSettingsEvents = false;
                }
            });
        });
    }

    private void UpdateSchemeModeUi()
    {
        var custom = _settings.IsCustomMode;
        EditCurrentLink.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        SchemeLockOverlay.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        // 确保不出现悬浮提示（仅点击弹窗）
        ToolTipService.SetToolTip(SchemeLockOverlay, null);
        SetParamsEnabled(custom);
        UpdateWatchAppendRadiosEnabled();
    }

    private void SetParamsEnabled(bool enabled)
    {
        FolderTextBox.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
        IncludeSubdirsCheckBox.IsEnabled = enabled;
        WatchNewFilesCheckBox.IsEnabled = enabled;
        PlayOrderCombo.IsEnabled = enabled;
        PlayModeCombo.IsEnabled = enabled;
        IdleSecondsBox.IsEnabled = enabled;
        PlaybackDisplayCombo.IsEnabled = enabled;
        StripOrientationCombo.IsEnabled = enabled;
        StripCountBox.IsEnabled = enabled;
        FlipDurationBox.IsEnabled = enabled;
        DwellBox.IsEnabled = enabled;
        if (!enabled)
        {
            AppendEndRadio.IsEnabled = false;
            AppendNextRadio.IsEnabled = false;
        }

        // 静态标签随只读态一起变灰（含「动画参数」）
        var labelBrush = enabled
            ? ResolveBrush("InkMutedBrush")
            : ResolveBrush("InkDisabledBrush");
        FolderLabel.Foreground = labelBrush;
        PlayOrderLabel.Foreground = labelBrush;
        PlayModeLabel.Foreground = labelBrush;
        IdleSecondsLabel.Foreground = labelBrush;
        IdleSecondsUnit.Foreground = labelBrush;
        PlaybackDisplayLabel.Foreground = labelBrush;
        AnimParamsLabel.Foreground = labelBrush;
        StripTypeLabel.Foreground = labelBrush;
        StripCountLabel.Foreground = labelBrush;
        FlipDurationLabel.Foreground = labelBrush;
        FlipDurationUnit.Foreground = labelBrush;
        DwellLabel.Foreground = labelBrush;
        DwellUnit.Foreground = labelBrush;
        AppendParenLeft.Foreground = labelBrush;
        AppendParenRight.Foreground = labelBrush;
    }

    private static Microsoft.UI.Xaml.Media.Brush ResolveBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) &&
            value is Microsoft.UI.Xaml.Media.Brush brush)
            return brush;
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void UpdateWatchAppendRadiosEnabled()
    {
        var enable = _settings.IsCustomMode && WatchNewFilesCheckBox.IsChecked == true;
        AppendEndRadio.IsEnabled = enable;
        AppendNextRadio.IsEnabled = enable;
    }

    private Task ShowCustomModeToastAsync()
        => ShowModeToastAsync("已切换为「自定义设置」模式");

    private Task ShowSchemeToastAsync(string schemeName)
        => ShowModeToastAsync($"已切换为方案「{schemeName}」");

    private async Task ShowModeToastAsync(string message)
    {
        ModeToastText.Text = message;
        ModeToast.Visibility = Visibility.Visible;

        var transform = ModeToast.RenderTransform as Microsoft.UI.Xaml.Media.CompositeTransform
                        ?? new Microsoft.UI.Xaml.Media.CompositeTransform();
        ModeToast.RenderTransform = transform;
        ModeToast.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

        transform.ScaleX = 0.82;
        transform.ScaleY = 0.82;
        transform.TranslateY = 18;
        ModeToast.Opacity = 0;

        await RunToastStoryboardAsync(
            opacityFrom: 0, opacityTo: 1,
            scaleFrom: 0.82, scaleTo: 1.0,
            yFrom: 18, yTo: 0,
            durationMs: 220);

        await Task.Delay(700);

        await RunToastStoryboardAsync(
            opacityFrom: 1, opacityTo: 0,
            scaleFrom: 1.0, scaleTo: 1.06,
            yFrom: 0, yTo: -10,
            durationMs: 200);

        ModeToast.Visibility = Visibility.Collapsed;
        transform.ScaleX = 1;
        transform.ScaleY = 1;
        transform.TranslateY = 0;
    }

    private Task RunToastStoryboardAsync(
        double opacityFrom, double opacityTo,
        double scaleFrom, double scaleTo,
        double yFrom, double yTo,
        int durationMs)
    {
        var tcs = new TaskCompletionSource();
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var ease = new Microsoft.UI.Xaml.Media.Animation.CubicEase
        {
            EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
        };

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        var opacityAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = opacityFrom,
            To = opacityTo,
            Duration = duration,
            EasingFunction = ease
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opacityAnim, ModeToast);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        if (ModeToast.RenderTransform is Microsoft.UI.Xaml.Media.CompositeTransform transform)
        {
            void AddScale(string prop, double from, double to)
            {
                var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = duration,
                    EasingFunction = ease
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, transform);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, prop);
                sb.Children.Add(anim);
            }

            AddScale("ScaleX", scaleFrom, scaleTo);
            AddScale("ScaleY", scaleFrom, scaleTo);
            AddScale("TranslateY", yFrom, yTo);
        }

        sb.Completed += (_, _) => tcs.TrySetResult();
        sb.Begin();
        return tcs.Task;
    }

    private void SwitchToCustomMode(bool showToast)
    {
        Safe(nameof(SwitchToCustomMode), () =>
        {
            if (_settings.IsCustomMode)
            {
                UpdateSchemeModeUi();
                return;
            }

            _settings.ActiveSchemeId = null;
            _settings.Save();
            // 不在当前调用栈清空 ComboBox，延后刷新
            DeferredRefreshSchemeCombo();
            UpdateSchemeModeUi();

            if (showToast)
                _ = ShowCustomModeToastAsync();
        });
    }

    private void ApplySchemeById(string schemeId, bool fromComboSelection = false)
    {
        Safe(nameof(ApplySchemeById), () =>
        {
            var scheme = _settings.FindScheme(schemeId);
            if (scheme is null)
                return;

            var schemeName = scheme.Name;
            scheme.ApplyTo(_settings);
            _settings.ActiveSchemeId = scheme.Id;
            _settings.Save();

            // 禁止在 ComboBox SelectionChanged 调用栈内改 Items / IsEnabled
            var token = ++_schemeApplyToken;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token != _schemeApplyToken)
                    return;

                Safe("ApplySchemeById.Ui", () =>
                {
                    // 下拉切换时 Combo 已选中，切勿再改 SelectedIndex / Clear Items
                    ApplySettingsToUi(rebuildSchemeList: !fromComboSelection);
                    RefreshImageCount();
                    RestartSettingsMonitor();
                    StartPlaybackTrigger();
                    _ = ShowSchemeToastAsync(schemeName);
                });
            });
        });
    }

    private void SchemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 只读出选中项，立刻返回；任何 UI/状态改动都延后，避免 0x80070490
        if (!IsLoaded || _suppressSettingsEvents)
            return;

        if (SchemeCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;

        var token = ++_schemeApplyToken;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token != _schemeApplyToken)
                return;

            Safe("SchemeCombo_SelectionChanged.Deferred", () =>
            {
                if (!IsLoaded)
                    return;

                if (string.IsNullOrEmpty(tag))
                {
                    var wasScheme = !string.IsNullOrWhiteSpace(_settings.ActiveSchemeId);
                    if (wasScheme)
                    {
                        _settings.ActiveSchemeId = null;
                        _settings.Save();
                    }

                    UpdateSchemeModeUi();
                    if (wasScheme)
                        _ = ShowCustomModeToastAsync();
                    return;
                }

                if (string.Equals(_settings.ActiveSchemeId, tag, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateSchemeModeUi();
                    return;
                }

                ApplySchemeById(tag, fromComboSelection: true);
            });
        });
    }

    private void SchemeManageLink_Click(object sender, RoutedEventArgs e)
    {
        SafeAsync(nameof(SchemeManageLink_Click), async () =>
        {
            if (XamlRoot is null)
                return;

            var result = await SchemeDialogs.ShowManageAsync(XamlRoot, _settings, afterSave: false);
            AfterSchemeDialog(result);
        });
    }

    private void SaveSchemeLink_Click(object sender, RoutedEventArgs e)
    {
        SafeAsync(nameof(SaveSchemeLink_Click), async () =>
        {
            if (XamlRoot is null)
                return;

            if (_settings.IsCustomMode)
                PersistFromUi();
            else
                _settings.Save();

            var name = await SchemeDialogs.PromptNewSchemeNameAsync(
                XamlRoot,
                _settings.Schemes.Select(s => s.Name));
            if (string.IsNullOrWhiteSpace(name))
                return;

            var scheme = SettingsScheme.FromSettings(_settings, name);
            _settings.Schemes.Add(scheme);
            _settings.ActiveSchemeId = scheme.Id;
            _settings.Save();

            var result = await SchemeDialogs.ShowManageAsync(XamlRoot, _settings, afterSave: true);
            AfterSchemeDialog(result);
        });
    }

    private void EditCurrentLink_Click(object sender, RoutedEventArgs e)
    {
        Safe(nameof(EditCurrentLink_Click), () => SwitchToCustomMode(showToast: true));
    }

    private void SchemeLockOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        SafeAsync(nameof(SchemeLockOverlay_Tapped), PromptUnlockSchemeAsync);
    }

    private async Task PromptUnlockSchemeAsync()
    {
        if (_settings.IsCustomMode || XamlRoot is null || _unlockPromptOpen)
            return;

        _unlockPromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "你想修改当前设置吗？",
                Content = "当前为方案只读模式。切换到「自定义设置」后即可调整参数。",
                PrimaryButtonText = "修改",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                SwitchToCustomMode(showToast: true);
        }
        finally
        {
            _unlockPromptOpen = false;
        }
    }

    private void AfterSchemeDialog(SchemeDialogs.ManageResult result)
    {
        Safe(nameof(AfterSchemeDialog), () =>
        {
            if (!string.IsNullOrWhiteSpace(result.ApplySchemeId))
            {
                ApplySchemeById(result.ApplySchemeId);
                return;
            }

            if (result.Changed)
            {
                if (_settings.FindScheme(_settings.ActiveSchemeId) is null)
                    _settings.ActiveSchemeId = null;
                _settings.Save();
                DeferredRefreshSchemeCombo();
                ApplySettingsToUi(rebuildSchemeList: false);
            }
            else
            {
                DeferredRefreshSchemeCombo();
            }
        });
    }

    private void RefreshPlaybackDisplayCombo(bool preserveSelection)
    {
        try
        {
            var selected = preserveSelection
                ? _settings.PlaybackDisplayIndex
                : ReadPlaybackDisplayIndex();

            IReadOnlyList<DisplayArea> displays;
            try
            {
                displays = DisplayHelper.GetOrderedDisplays();
            }
            catch
            {
                displays = [];
            }

            if (selected > displays.Count)
                selected = 0;

            PlaybackDisplayCombo.ItemsSource = null;
            PlaybackDisplayCombo.Items.Clear();
            PlaybackDisplayCombo.Items.Add(new ComboBoxItem
            {
                Content = "当前屏幕",
                Tag = "0"
            });

            for (var i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                var isPrimary = false;
                var w = 0;
                var h = 0;
                try
                {
                    isPrimary = d.IsPrimary;
                    w = d.OuterBounds.Width;
                    h = d.OuterBounds.Height;
                }
                catch
                {
                    // ignore
                }

                var size = w > 0 && h > 0 ? $" · {w}×{h}" : string.Empty;
                PlaybackDisplayCombo.Items.Add(new ComboBoxItem
                {
                    Content = isPrimary
                        ? $"屏幕 {i + 1}（主屏{size}）"
                        : $"屏幕 {i + 1}（扩展屏{size}）",
                    Tag = (i + 1).ToString()
                });
            }

            SelectComboByTag(PlaybackDisplayCombo, selected.ToString());
            _settings.PlaybackDisplayIndex = selected;
        }
        catch
        {
            try
            {
                PlaybackDisplayCombo.ItemsSource = null;
                PlaybackDisplayCombo.Items.Clear();
                PlaybackDisplayCombo.Items.Add(new ComboBoxItem
                {
                    Content = "当前屏幕",
                    Tag = "0"
                });
                PlaybackDisplayCombo.SelectedIndex = 0;
                _settings.PlaybackDisplayIndex = 0;
            }
            catch
            {
                // ignore
            }
        }
    }

    private int ReadPlaybackDisplayIndex()
    {
        if (PlaybackDisplayCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            int.TryParse(tag, out var index) &&
            index >= 0)
            return index;
        return 0;
    }

    private void PlaybackDisplayCombo_DropDownOpened(object sender, object e)
    {
        Safe(nameof(PlaybackDisplayCombo_DropDownOpened), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;

            _suppressSettingsEvents = true;
            try
            {
                RefreshPlaybackDisplayCombo(preserveSelection: true);
            }
            finally
            {
                _suppressSettingsEvents = false;
            }
        });
    }

    private void PlaybackDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Safe(nameof(PlaybackDisplayCombo_SelectionChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;
            OnSettingsInteracted();
            PersistFromUi();
        });
    }

    private DisplayArea ResolveTargetPlaybackDisplay()
    {
        try
        {
            if (App.MainWindow is null)
                return App.LaunchDisplay ?? DisplayHelper.TryGetPrimaryDisplay();

            return DisplayHelper.ResolvePlaybackDisplay(App.MainWindow, _settings.PlaybackDisplayIndex);
        }
        catch
        {
            return DisplayHelper.GetLaunchDisplayArea();
        }
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is not ComboBoxItem { Tag: string t } ||
                !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                continue;

            // 已是目标项时不要再写 SelectedIndex，避免 ComboBox COM 异常
            if (combo.SelectedIndex != i)
                combo.SelectedIndex = i;
            return;
        }

        if (combo.Items.Count > 0 && combo.SelectedIndex != 0)
            combo.SelectedIndex = 0;
    }

    private void PersistFromUi()
    {
        // 方案只读模式下不把禁用控件写回，避免误改
        if (!_settings.IsCustomMode)
        {
            _settings.Save();
            return;
        }

        _settings.WatchFolder = FolderTextBox.Text?.Trim() ?? string.Empty;
        _settings.IncludeSubdirectories = IncludeSubdirsCheckBox.IsChecked == true;
        _settings.WatchNewFiles = WatchNewFilesCheckBox.IsChecked == true;
        _settings.NewFileAppendMode = ReadAppendMode();
        _settings.PlayOrder = ReadPlayOrder();
        _settings.PlayMode = ReadPlayMode();
        _settings.PlaybackDisplayIndex = ReadPlaybackDisplayIndex();
        _settings.IdleSeconds = FiniteOr(IdleSecondsBox.Value, _settings.IdleSeconds);
        _settings.StripOrientation = ReadStripOrientation();
        _settings.StripCount = (int)Math.Clamp(FiniteOr(StripCountBox.Value, _settings.StripCount), 1, 200);
        var flipSec = FiniteOr(FlipDurationBox.Value, _settings.FlipDurationMs / 1000.0);
        _settings.FlipDurationMs = (int)Math.Round(flipSec * 1000);
        _settings.DwellSeconds = FiniteOr(DwellBox.Value, _settings.DwellSeconds);
        _settings.ActiveSchemeId = null;
        _settings.Save();
    }

    private static double FiniteOr(double value, double fallback)
        => double.IsFinite(value) ? value : fallback;

    private NewFileAppendMode ReadAppendMode()
    {
        return AppendNextRadio.IsChecked == true
            ? NewFileAppendMode.NextPlayPosition
            : NewFileAppendMode.EndOfQueue;
    }

    private PlayOrder ReadPlayOrder()
    {
        if (PlayOrderCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<PlayOrder>(tag, out var order))
            return order;
        return PlayOrder.AsRead;
    }

    private PlayMode ReadPlayMode()
    {
        if (PlayModeCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<PlayMode>(tag, out var mode))
            return mode;
        return PlayMode.Auto;
    }

    private StripOrientation ReadStripOrientation()
    {
        if (StripOrientationCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<StripOrientation>(tag, out var orientation))
            return orientation;
        return StripOrientation.Vertical;
    }

    private void UpdateIdleSecondsVisibility()
    {
        var idle = ReadPlayMode() == PlayMode.IdleStandby;
        var vis = idle ? Visibility.Visible : Visibility.Collapsed;
        IdleSecondsLabel.Visibility = vis;
        IdleSecondsBox.Visibility = vis;
        IdleSecondsUnit.Visibility = vis;

        // 与其它行一致：行距 41。无空闲时播放屏幕在 214；有空闲则空闲占 214，后续整体下移
        const double row = 41.0;
        var d = idle ? row : 0.0;
        var playBoxTop = 214.0 + d;

        Canvas.SetTop(PlaybackDisplayCombo, playBoxTop);
        Canvas.SetTop(PlaybackDisplayLabel, playBoxTop + 5);

        Canvas.SetTop(AnimDivider, 257 + d);
        Canvas.SetTop(AnimParamsLabel, 271 + d);
        Canvas.SetTop(StripTypeLabel, 271 + d);
        Canvas.SetTop(StripOrientationCombo, 266 + d);
        Canvas.SetTop(StripCountLabel, 312 + d);
        Canvas.SetTop(StripCountBox, 307 + d);
        Canvas.SetTop(FlipDurationLabel, 353 + d);
        Canvas.SetTop(FlipDurationBox, 348 + d);
        Canvas.SetTop(FlipDurationUnit, 353 + d);
        Canvas.SetTop(DwellLabel, 394 + d);
        Canvas.SetTop(DwellBox, 389 + d);
        Canvas.SetTop(DwellUnit, 394 + d);
    }

    private void PlayModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Safe(nameof(PlayModeCombo_SelectionChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;

            OnSettingsInteracted();
            UpdateIdleSecondsVisibility();
            PersistFromUi();
            StopAllSessions();
            UpdatePlayButtonUi();
        });
    }

    private void IdleSecondsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        Safe(nameof(IdleSecondsBox_ValueChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;
            OnSettingsInteracted();
            PersistFromUi();
        });
    }

    private void StartPlaybackTrigger()
    {
        if (IsPlayerOpen)
            return;

        PersistFromUi();
        if (!_standbySession && !_newImageSession)
            StopIdleWatch();
        UpdatePlayButtonUi();
    }

    private void UpdatePlayButtonUi()
    {
        if (PrimaryActionButton is null)
            return;

        if (_settings.PlayMode == PlayMode.IdleStandby)
        {
            if (_standbySession || _idleWatching)
            {
                PrimaryActionButton.Content = "取消待机监控";
                StatusText.Text = "监控中：空闲达标后播放；播放中若有键鼠操作将退出并继续等待。";
            }
            else
            {
                PrimaryActionButton.Content = "进入待机监控";
                StatusText.Text = $"设定空闲 {_settings.IdleSeconds:0} 秒后进入播放；播放中有操作会退出并继续等待。";
            }

            HintText.Text = string.Empty;
            return;
        }

        if (_settings.PlayMode == PlayMode.NewImageOnly)
        {
            if (_newImageSession)
            {
                PrimaryActionButton.Content = "取消新图监控";
                StatusText.Text = "已进入全屏等待；仅播放监控开始后出现的新图片。";
            }
            else
            {
                PrimaryActionButton.Content = "进入新图监控";
                StatusText.Text = "第 1 张直接显示，之后每来一张翻转一次；监控前已有图片不会入队。";
            }

            HintText.Text = string.Empty;
            return;
        }

        PrimaryActionButton.Content = "播放";
        HintText.Text = string.Empty;
    }

    private void StartStandbySession()
    {
        if (IsPlayerOpen)
            return;

        PersistFromUi();
        if (!_settings.HasFolder)
        {
            HintText.Text = "请先选择图片目录";
            StatusText.Text = "未设置目录，无法进入待机监控。";
            return;
        }

        var count = ImageLoader.ScanFolder(
            _settings.WatchFolder,
            _settings.IncludeSubdirectories,
            _settings.PlayOrder).Count;
        if (count < 2)
        {
            HintText.Text = "图片不足 2 张，无法进入待机监控";
            StatusText.Text = $"当前仅 {count} 张，至少需要 2 张。";
            return;
        }

        _standbySession = true;
        StartIdleWatch();
        UpdatePlayButtonUi();
    }

    private void StopStandbySession()
    {
        _standbySession = false;
        StopIdleWatch();
        UpdatePlayButtonUi();
    }

    private void StopNewImageSession()
    {
        _newImageSession = false;
        UpdatePlayButtonUi();
    }

    private void StopAllSessions()
    {
        if (IsPlayerOpen)
            App.CloseActivePlayer(silent: false);

        _standbySession = false;
        _newImageSession = false;
        StopIdleWatch();
        UpdatePlayButtonUi();
    }

    private void StartNewImageSession()
    {
        if (IsPlayerOpen || _newImageSession)
            return;

        PersistFromUi();
        if (!_settings.HasFolder)
        {
            HintText.Text = "请先选择图片目录";
            StatusText.Text = "未设置目录，无法进入新图监控。";
            return;
        }

        var existing = ImageLoader.ScanFolder(
            _settings.WatchFolder,
            _settings.IncludeSubdirectories,
            _settings.PlayOrder);

        var placeholderPath = existing.Count > 0 ? existing[0] : null;
        _standbySession = false;
        StopIdleWatch();
        _newImageSession = true;

        OpenPlayerWindow(
            PlayerSessionMode.NewImageOnly,
            initialImages: null,
            knownExisting: existing,
            placeholderPath: placeholderPath,
            standbySession: false);
    }

    private void StartIdleWatch()
    {
        if (IsPlayerOpen || !_standbySession)
            return;

        _idleWatching = true;
        var need = Math.Max(5, _settings.IdleSeconds);
        HintText.Text = $"待机监控中：无操作 {need:0} 秒后进入播放…";
        StatusText.Text = "正在检测系统键鼠空闲。可点「取消待机监控」停止。";

        _idleTimer?.Stop();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _idleTimer.Tick -= IdleTimer_Tick;
        _idleTimer.Tick += IdleTimer_Tick;
        _idleTimer.Start();
        UpdatePlayButtonUi();
    }

    private void StopIdleWatch()
    {
        _idleTimer?.Stop();
        _idleTimer = null;
        _idleWatching = false;
    }

    private void IdleTimer_Tick(object? sender, object e)
    {
        Safe(nameof(IdleTimer_Tick), () =>
        {
            if (!_idleWatching || !_standbySession || IsPlayerOpen)
                return;

            var need = Math.Max(5, _settings.IdleSeconds);
            var idle = SystemIdleHelper.GetIdleSeconds();
            var remain = Math.Max(0, need - idle);
            HintText.Text = remain <= 0.05
                ? "空闲已达标，正在进入播放…"
                : $"待机监控中：还需空闲 {remain:0} 秒（已空闲 {idle:0} 秒）…";

            if (idle < need)
                return;

            StopIdleWatch();
            _ = TryEnterPlaybackAsync(fromStandby: true);
        });
    }

    private void RefreshImageCount()
    {
        var folder = FolderTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            ImageCountText.Text = "图片数量：0（未选择有效目录）";
            StatusText.Text = "请选择包含至少 2 张图片的目录。";
            return;
        }

        var count = ImageLoader.ScanFolder(
            folder,
            IncludeSubdirsCheckBox.IsChecked == true,
            ReadPlayOrder()).Count;
        ImageCountText.Text = $"图片数量：{count} 张";
        if (count < 2)
            StatusText.Text = "目录中图片少于 2 张，无法开始播放。";
        else
            StatusText.Text = $"已记忆目录，共 {count} 张图片。";
    }

    private void MonitorOption_Changed(object sender, RoutedEventArgs e)
    {
        Safe(nameof(MonitorOption_Changed), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;
            OnSettingsInteracted();
            PersistFromUi();
            RefreshImageCount();
            RestartSettingsMonitor();
        });
    }

    private void AppendModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        Safe(nameof(AppendModeRadio_Changed), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;
            OnSettingsInteracted();
            PersistFromUi();
        });
    }

    private void WatchNewFilesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        Safe(nameof(WatchNewFilesCheckBox_Changed), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;
            OnSettingsInteracted();
            UpdateWatchAppendRadiosEnabled();
            PersistFromUi();
            RefreshImageCount();
            RestartSettingsMonitor();
        });
    }

    private void PlayOrderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Safe(nameof(PlayOrderCombo_SelectionChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;
            OnSettingsInteracted();
            PersistFromUi();
            RefreshImageCount();
        });
    }

    private void RestartSettingsMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;

        if (!_settings.WatchNewFiles || !_settings.HasFolder || IsPlayerOpen)
            return;

        var known = ImageLoader.ScanFolder(
            _settings.WatchFolder,
            _settings.IncludeSubdirectories,
            PlayOrder.AsRead);

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
            if (files.Count == 0 || IsPlayerOpen)
                return;

            RefreshImageCount();
            StatusText.Text = $"监控到 {files.Count} 个新文件已稳定可用。";
        });
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        Safe(nameof(BrowseFolder_Click), () =>
        {
            if (App.MainWindow is null)
            {
                StatusText.Text = "窗口未就绪，无法打开目录选择。";
                return;
            }

            var path = NativeFolderPicker.PickFolder(App.MainWindow, "选择要播放的图片目录");
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "未选择目录。";
                return;
            }

            FolderTextBox.Text = path;
            ApplyFolderPathFromUi(showHint: true);
        });
    }

    private void FolderTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Safe(nameof(FolderTextBox_TextChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;

            PersistFromUi();
            RefreshImageCount();
        });
    }

    private void FolderTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Safe(nameof(FolderTextBox_LostFocus), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;

            var trimmed = FolderTextBox.Text?.Trim() ?? string.Empty;
            if (!string.Equals(FolderTextBox.Text, trimmed, StringComparison.Ordinal))
            {
                _suppressSettingsEvents = true;
                try
                {
                    FolderTextBox.Text = trimmed;
                }
                finally
                {
                    _suppressSettingsEvents = false;
                }
            }

            ApplyFolderPathFromUi(showHint: false);
        });
    }

    private void ApplyFolderPathFromUi(bool showHint)
    {
        PersistFromUi();
        RefreshImageCount();
        RestartSettingsMonitor();
        StartPlaybackTrigger();
        if (showHint)
            HintText.Text = "目录已更新，可点击开始播放。";
    }

    private void StripOrientationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Safe(nameof(StripOrientationCombo_SelectionChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents)
                return;
            OnSettingsInteracted();
            PersistFromUi();
        });
    }

    private void StripCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        Safe(nameof(StripCountBox_ValueChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents) return;
            OnSettingsInteracted();
            PersistFromUi();
        });
    }

    private void FlipDurationBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        Safe(nameof(FlipDurationBox_ValueChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents) return;
            OnSettingsInteracted();
            PersistFromUi();
        });
    }

    private void DwellBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        Safe(nameof(DwellBox_ValueChanged), () =>
        {
            if (!IsLoaded || _suppressSettingsEvents) return;
            OnSettingsInteracted();
            PersistFromUi();
        });
    }

    private void OnSettingsInteracted()
    {
        // 参数区在方案模式下已禁用；若将来有入口触发，统一切回自定义
        if (!_settings.IsCustomMode)
            SwitchToCustomMode(showToast: true);
    }

    private void PlayNow_Click(object sender, RoutedEventArgs e)
    {
        SafeAsync(nameof(PlayNow_Click), async () =>
        {
            PersistFromUi();

            if (_settings.PlayMode == PlayMode.IdleStandby)
            {
                if (_standbySession || _idleWatching)
                {
                    StopAllSessions();
                    HintText.Text = "已取消待机监控";
                    StatusText.Text = "可再次点击「进入待机监控」重新开始。";
                    UpdatePlayButtonUi();
                    return;
                }

                StopNewImageSession();
                StartStandbySession();
                return;
            }

            if (_settings.PlayMode == PlayMode.NewImageOnly)
            {
                if (_newImageSession)
                {
                    StopAllSessions();
                    HintText.Text = "已取消新图监控";
                    StatusText.Text = "可再次点击「进入新图监控」重新开始。";
                    UpdatePlayButtonUi();
                    return;
                }

                StopStandbySession();
                StartNewImageSession();
                return;
            }

            StopAllSessions();
            await TryEnterPlaybackAsync(fromStandby: false);
        });
    }

    private Task TryEnterPlaybackAsync(bool fromStandby)
    {
        PersistFromUi();
        RefreshImageCount();

        if (!_settings.HasFolder)
        {
            HintText.Text = "未设置目录，无法播放。";
            StatusText.Text = "请先选择图片目录。";
            if (fromStandby && _standbySession)
                StartIdleWatch();
            else
                UpdatePlayButtonUi();
            return Task.CompletedTask;
        }

        var images = ImageLoader.ScanFolder(
            _settings.WatchFolder,
            _settings.IncludeSubdirectories,
            _settings.PlayOrder);

        if (images.Count < 2)
        {
            HintText.Text = "图片不足 2 张，无法播放。";
            StatusText.Text = $"当前仅 {images.Count} 张，至少需要 2 张。";
            if (fromStandby && _standbySession)
                StartIdleWatch();
            else
                UpdatePlayButtonUi();
            return Task.CompletedTask;
        }

        OpenPlayerWindow(
            PlayerSessionMode.Carousel,
            initialImages: images,
            knownExisting: null,
            placeholderPath: null,
            standbySession: fromStandby && _standbySession);

        return Task.CompletedTask;
    }

    private void OpenPlayerWindow(
        PlayerSessionMode mode,
        IReadOnlyList<string>? initialImages,
        IReadOnlyList<string>? knownExisting,
        string? placeholderPath,
        bool standbySession)
    {
        if (App.MainWindow is null)
            return;

        PersistFromUi();
        UnsubscribePlayer();

        App.CloseActivePlayer(silent: true);

        var returnDisplay = DisplayHelper.GetWindowDisplay(App.MainWindow);
        var targetDisplay = ResolveTargetPlaybackDisplay();
        _lastReturnDisplay = returnDisplay;

        _monitor?.Dispose();
        _monitor = null;

        var player = new PlayerWindow();
        App.ActivePlayer = player;
        _subscribedPlayer = player;
        player.Exited += OnPlayerExited;

        try
        {
            DisplayHelper.GetAppWindow(App.MainWindow).Hide();
        }
        catch
        {
            // ignore
        }

        player.BeginSession(
            mode,
            _settings,
            targetDisplay,
            returnDisplay,
            initialImages,
            knownExisting,
            placeholderPath,
            standbySession);

        UpdatePlayButtonUi();
    }

    private void UnsubscribePlayer()
    {
        if (_subscribedPlayer is null)
            return;

        _subscribedPlayer.Exited -= OnPlayerExited;
        _subscribedPlayer = null;
    }

    private void OnPlayerExited(PlayerExitReason reason)
    {
        Safe(nameof(OnPlayerExited), () =>
        {
            UnsubscribePlayer();
            App.ActivePlayer = null;

            DisplayArea returnDisplay;
            try
            {
                returnDisplay = _lastReturnDisplay ?? App.LaunchDisplay ?? DisplayHelper.GetLaunchDisplayArea();
            }
            catch
            {
                returnDisplay = App.LaunchDisplay;
            }

            _lastReturnDisplay = null;
            RestoreMainWindow(returnDisplay, reason);
        });
    }

    private void RestoreMainWindow(DisplayArea returnDisplay, PlayerExitReason reason)
    {
        if (App.MainWindow is { } mw)
        {
            try
            {
                DisplayHelper.GetAppWindow(mw).Show();
                DisplayHelper.PlaceSettingsWindow(mw, returnDisplay);
                mw.Activate();
            }
            catch
            {
                // ignore
            }
        }

        RestartSettingsMonitor();
        RefreshImageCount();
        RootGrid.Focus(FocusState.Programmatic);

        if (reason == PlayerExitReason.ActivityInterrupt && _standbySession && _settings.PlayMode == PlayMode.IdleStandby)
        {
            StatusText.Text = "检测到操作，已退出播放，继续等待下次空闲…";
            StartIdleWatch();
        }
        else if (reason == PlayerExitReason.UserExit)
        {
            if (_settings.PlayMode == PlayMode.NewImageOnly)
                _newImageSession = false;
            else if (_settings.PlayMode == PlayMode.IdleStandby)
                _standbySession = false;

            StopIdleWatch();
            StatusText.Text = "已返回设置。可修改配置后再次播放，或关闭窗口退出。";
        }
        else
        {
            StatusText.Text = "已返回设置。";
        }

        UpdatePlayButtonUi();
    }
}
