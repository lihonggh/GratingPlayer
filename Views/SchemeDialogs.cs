using GratingPlayer.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GratingPlayer.Views;

internal static class SchemeDialogs
{
    public sealed class ManageResult
    {
        public string? ApplySchemeId { get; init; }
        public bool Changed { get; init; }
    }

    private abstract record ManagePending;
    private sealed record PendingRename(string Id, string Name) : ManagePending;
    private sealed record PendingDelete(string Id, string Name) : ManagePending;
    private sealed record PendingClearAll : ManagePending;

    public static async Task<string?> PromptNewSchemeNameAsync(XamlRoot root, IEnumerable<string> existingNames)
    {
        var name = await PromptSchemeNameAsync(root, "存储到新方案", string.Empty, "请输入方案名称");
        if (name is null)
            return null;

        if (string.IsNullOrWhiteSpace(name))
        {
            await ShowMessageAsync(root, "方案名称不能为空。");
            return null;
        }

        if (existingNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowMessageAsync(root, $"已存在同名方案「{name}」。");
            return null;
        }

        return name;
    }

    public static async Task<ManageResult> ShowManageAsync(XamlRoot root, AppSettings settings, bool afterSave)
    {
        string? applyId = null;
        var changed = afterSave;
        var showSavedTitle = afterSave;

        while (true)
        {
            var pending = await ShowManageListAsync(root, settings, showSavedTitle, id => applyId = id);
            showSavedTitle = false;

            if (pending is PendingRename rename)
            {
                var existing = settings.Schemes
                    .Where(s => !string.Equals(s.Id, rename.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Name);
                var newName = await PromptSchemeNameAsync(root, "修改方案名称", rename.Name, "请输入新的方案名称：");
                if (string.IsNullOrWhiteSpace(newName) ||
                    string.Equals(newName, rename.Name, StringComparison.Ordinal))
                    continue;

                if (existing.Any(n => string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    await ShowMessageAsync(root, $"已存在同名方案「{newName}」。");
                    continue;
                }

                var target = settings.Schemes.FirstOrDefault(s =>
                    string.Equals(s.Id, rename.Id, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    continue;

                target.Name = newName;
                changed = true;
                settings.Save();
                continue;
            }

            if (pending is PendingDelete del)
            {
                var ok = await ConfirmAsync(
                    root,
                    "删除方案？",
                    $"确定删除方案「{del.Name}」吗？此操作不可撤销。",
                    "删除",
                    danger: true);
                if (!ok)
                    continue;

                settings.Schemes.RemoveAll(s =>
                    string.Equals(s.Id, del.Id, StringComparison.OrdinalIgnoreCase));
                if (string.Equals(settings.ActiveSchemeId, del.Id, StringComparison.OrdinalIgnoreCase))
                    settings.ActiveSchemeId = null;
                changed = true;
                settings.Save();
                continue;
            }

            if (pending is PendingClearAll)
            {
                var count = settings.Schemes.Count;
                if (count == 0)
                    continue;

                var ok = await ConfirmAsync(
                    root,
                    "清空全部方案？",
                    $"将删除全部 {count} 个方案，此操作不可撤销。\n确定继续吗？",
                    "清空全部",
                    danger: true);
                if (!ok)
                    continue;

                settings.Schemes.Clear();
                settings.ActiveSchemeId = null;
                changed = true;
                settings.Save();
                continue;
            }

            return new ManageResult { ApplySchemeId = applyId, Changed = changed };
        }
    }

    /// <summary>紧凑名称录入：整体窄、输入框拉满内容区。</summary>
    private static async Task<string?> PromptSchemeNameAsync(
        XamlRoot root,
        string title,
        string initialName,
        string hint)
    {
        const double contentWidth = 300;

        var hintBlock = new TextBlock
        {
            Text = hint,
            FontSize = 14,
            Foreground = Brush("InkMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var box = new TextBox
        {
            Text = initialName,
            PlaceholderText = "请输入方案名称",
            MaxLength = 64,
            FontSize = 14,
            Width = contentWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var panel = new StackPanel
        {
            Spacing = 0,
            Width = contentWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(hintBlock);
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMinWidth"] = 340.0;
        dialog.Resources["ContentDialogMaxWidth"] = 360.0;

        dialog.Opened += (_, _) => DispatcherQueueTryFocus(box);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return null;

        return box.Text?.Trim();
    }

    private static async Task<bool> ConfirmAsync(
        XamlRoot root,
        string title,
        string message,
        string okText,
        bool danger)
    {
        const double contentWidth = 260;

        var body = new TextBlock
        {
            Text = message,
            FontSize = 14,
            Foreground = Brush("InkMutedBrush"),
            TextWrapping = TextWrapping.WrapWholeWords,
            Width = contentWidth
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = body,
            PrimaryButtonText = okText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMinWidth"] = 300.0;
        dialog.Resources["ContentDialogMaxWidth"] = 320.0;

        if (danger &&
            Application.Current.Resources.TryGetValue("DangerBrush", out var dangerRes) &&
            dangerRes is SolidColorBrush dangerBrush)
        {
            // BasedOn 默认按钮样式，保留与「取消」相同的圆角模板
            dialog.PrimaryButtonStyle = MakeDialogButtonStyle(
                dangerBrush,
                new SolidColorBrush(Microsoft.UI.Colors.White),
                dangerBrush);
        }

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private static Style MakeDialogButtonStyle(Brush background, Brush foreground, Brush? border = null)
    {
        var style = new Style(typeof(Button));
        if (Application.Current.Resources.TryGetValue("DefaultButtonStyle", out var baseObj) &&
            baseObj is Style baseStyle)
        {
            style.BasedOn = baseStyle;
        }

        // 与 ContentDialog 默认按钮一致（主题 ControlCornerRadius，一般为 4）
        var radius = 4.0;
        if (Application.Current.Resources.TryGetValue("ControlCornerRadius", out var radiusObj))
        {
            if (radiusObj is CornerRadius cr)
                radius = cr.TopLeft;
            else if (radiusObj is double d)
                radius = d;
        }

        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, border ?? background));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(radius)));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 6, 16, 6)));
        return style;
    }

    private static async Task<ManagePending?> ShowManageListAsync(
        XamlRoot root,
        AppSettings settings,
        bool afterSave,
        Action<string> setApplyId)
    {
        ContentDialog? dialog = null;
        ManagePending? pending = null;

        var countText = new TextBlock
        {
            FontSize = 14,
            Foreground = Brush("InkMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(2, 0, 8, 0)
        };

        var clearAllBtn = new Button
        {
            Content = "清空全部",
            MinWidth = 88,
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            Style = StyleOrNull("DangerAction"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var topBar = new Grid
        {
            Margin = new Thickness(0, 4, 0, 10),
            MinHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(countText, 0);
        Grid.SetColumn(clearAllBtn, 1);
        topBar.Children.Add(countText);
        topBar.Children.Add(clearAllBtn);

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 300,
            BorderBrush = Brush("SurfaceLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Background = Brush("SurfaceBrush"),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        list.ItemContainerStyle = new Style(typeof(ListViewItem))
        {
            Setters =
            {
                new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(ListViewItem.PaddingProperty, new Thickness(0)),
                new Setter(ListViewItem.MinHeightProperty, 44.0),
                new Setter(Control.HorizontalAlignmentProperty, HorizontalAlignment.Stretch)
            }
        };

        Border? header = null;
        var tableHost = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        tableHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tableHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(list, 1);
        tableHost.Children.Add(list);

        var mainPanel = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        mainPanel.Children.Add(topBar);
        mainPanel.Children.Add(tableHost);

        var closeBtn = new Button
        {
            Content = "关闭",
            Width = 140,
            Height = 40,
            Style = StyleOrNull("PrimaryAction"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0)
        };
        closeBtn.Click += (_, _) => dialog?.Hide();
        mainPanel.Children.Add(closeBtn);

        void Rebuild()
        {
            list.Items.Clear();
            countText.Text = $"共 {settings.Schemes.Count} 个方案";
            clearAllBtn.IsEnabled = settings.Schemes.Count > 0;

            var ordered = settings.Schemes
                .OrderByDescending(s => s.CreatedAt)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            const double opsWidth = 180;
            var timeWidth = MeasureTimeColumnWidth(ordered.Select(s => FormatCreatedAt(s.CreatedAt)));

            if (header is not null)
                tableHost.Children.Remove(header);
            header = BuildHeaderRow(timeWidth, opsWidth);
            Grid.SetRow(header, 0);
            tableHost.Children.Add(header);

            if (ordered.Count == 0)
            {
                list.Items.Add(new ListViewItem
                {
                    IsEnabled = false,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock
                    {
                        Text = "（暂无已保存方案）",
                        FontSize = 14,
                        Foreground = Brush("InkMutedBrush"),
                        Margin = new Thickness(12, 14, 12, 14),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                });
                return;
            }

            foreach (var scheme in ordered)
            {
                var id = scheme.Id;
                var schemeName = scheme.Name;

                var useBtn = new Button
                {
                    Content = "使用",
                    MinWidth = 48,
                    Height = 30,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = StyleOrNull("Action")
                };
                var renameBtn = new Button
                {
                    Content = "改名",
                    MinWidth = 48,
                    Height = 30,
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = StyleOrNull("Action")
                };
                var delBtn = new Button
                {
                    Content = "删除",
                    MinWidth = 48,
                    Height = 30,
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = StyleOrNull("DangerAction")
                };

                useBtn.Click += (_, _) =>
                {
                    setApplyId(id);
                    dialog?.Hide();
                };

                // 先关掉管理框，再开紧凑改名/确认（ContentDialog 打开后无法收窄）
                renameBtn.Click += (_, _) =>
                {
                    pending = new PendingRename(id, schemeName);
                    dialog?.Hide();
                };

                delBtn.Click += (_, _) =>
                {
                    pending = new PendingDelete(id, schemeName);
                    dialog?.Hide();
                };

                var row = BuildDataRow(
                    scheme.Name,
                    FormatCreatedAt(scheme.CreatedAt),
                    useBtn,
                    renameBtn,
                    delBtn,
                    timeWidth,
                    opsWidth);
                list.Items.Add(new ListViewItem
                {
                    Content = row,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    MinHeight = 44
                });
            }
        }

        clearAllBtn.Click += (_, _) =>
        {
            if (settings.Schemes.Count == 0)
                return;
            pending = new PendingClearAll();
            dialog?.Hide();
        };

        Rebuild();

        dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = afterSave ? "方案已保存" : "方案管理",
            Content = mainPanel,
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        dialog.Resources["ContentDialogMinWidth"] = 640.0;

        await dialog.ShowAsync();
        return pending;
    }

    private static void DispatcherQueueTryFocus(Control control)
    {
        try
        {
            control.DispatcherQueue.TryEnqueue(() =>
            {
                control.Focus(FocusState.Programmatic);
                if (control is TextBox box)
                    box.SelectAll();
            });
        }
        catch
        {
            // ignore
        }
    }

    private static double MeasureTextWidth(string text, double fontSize)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            TextWrapping = TextWrapping.NoWrap
        };
        tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }

    private static double MeasureTimeColumnWidth(IEnumerable<string> timeTexts)
    {
        var max = MeasureTextWidth("创建时间", 14);
        foreach (var t in timeTexts)
            max = Math.Max(max, MeasureTextWidth(t, 13));
        return Math.Ceiling(max + 28);
    }

    private static Grid BuildColumnGrid(double timeWidth, double opsWidth)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 80
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(timeWidth)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(opsWidth)
        });
        return grid;
    }

    private static Border BuildHeaderRow(double timeWidth, double opsWidth)
    {
        var grid = BuildColumnGrid(timeWidth, opsWidth);
        grid.Padding = new Thickness(10, 8, 10, 8);

        void AddHeader(string text, int col, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = Brush("InkMutedBrush"),
                HorizontalAlignment = align,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        AddHeader("方案名称", 0);
        AddHeader("创建时间", 1);
        AddHeader("操作", 2, HorizontalAlignment.Right);

        return new Border
        {
            Child = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 243, 244, 246)),
            BorderBrush = Brush("SurfaceLineBrush"),
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(6, 6, 0, 0)
        };
    }

    private static Grid BuildDataRow(
        string name,
        string timeText,
        Button useBtn,
        Button renameBtn,
        Button delBtn,
        double timeWidth,
        double opsWidth)
    {
        var grid = BuildColumnGrid(timeWidth, opsWidth);
        grid.Padding = new Thickness(10, 6, 10, 6);

        var nameBlock = new TextBlock
        {
            Text = name,
            FontSize = 14,
            Foreground = Brush("InkBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameBlock, 0);

        var timeBlock = new TextBlock
        {
            Text = timeText,
            FontSize = 13,
            Foreground = Brush("InkMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 12, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None
        };
        Grid.SetColumn(timeBlock, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0)
        };
        buttons.Children.Add(useBtn);
        buttons.Children.Add(renameBtn);
        buttons.Children.Add(delBtn);
        Grid.SetColumn(buttons, 2);

        grid.Children.Add(nameBlock);
        grid.Children.Add(timeBlock);
        grid.Children.Add(buttons);
        return grid;
    }

    private static string FormatCreatedAt(DateTime createdAt)
    {
        if (createdAt <= DateTime.MinValue.AddYears(1))
            return "—";

        var local = createdAt.Kind == DateTimeKind.Utc ? createdAt.ToLocalTime() : createdAt;
        var relative = FormatRelative(local);
        return string.IsNullOrEmpty(relative)
            ? local.ToString("yyyy-MM-dd HH:mm")
            : $"{local:yyyy-MM-dd HH:mm}（{relative}）";
    }

    private static string FormatRelative(DateTime local)
    {
        var span = DateTime.Now - local;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        if (span.TotalMinutes < 1)
            return "刚刚";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24)
            return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 30)
        {
            var days = Math.Max(1, (int)span.TotalDays);
            return days == 1 ? "1 天前" : $"{days} 天前";
        }

        if (span.TotalDays < 365)
        {
            var months = Math.Max(1, (int)(span.TotalDays / 30));
            return months == 1 ? "1 个月前" : $"{months} 个月前";
        }

        var years = Math.Max(1, (int)(span.TotalDays / 365));
        return years == 1 ? "1 年前" : $"{years} 年前";
    }

    private static Brush Brush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private static Style? StyleOrNull(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Style style)
            return style;
        return null;
    }

    private static async Task ShowMessageAsync(XamlRoot root, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "提示",
            Content = message,
            CloseButtonText = "知道了"
        };
        dialog.Resources["ContentDialogMaxWidth"] = 360.0;
        dialog.Resources["ContentDialogMinWidth"] = 300.0;
        await dialog.ShowAsync();
    }
}
