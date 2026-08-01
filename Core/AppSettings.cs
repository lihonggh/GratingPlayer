using System.Text.Json;
using System.Text.Json.Serialization;

namespace GratingPlayer.Core;

public sealed class AppSettings
{
    public string WatchFolder { get; set; } = string.Empty;

    /// <summary>是否包含子目录图片（默认是）。</summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>是否监控目录中动态新增的图片文件（默认否）。</summary>
    public bool WatchNewFiles { get; set; }

    /// <summary>新文件插入播放队列的方式。</summary>
    public NewFileAppendMode NewFileAppendMode { get; set; } = NewFileAppendMode.EndOfQueue;

    /// <summary>开始播放时的排序方式。</summary>
    public PlayOrder PlayOrder { get; set; } = PlayOrder.AsRead;

    /// <summary>进入播放的方式。</summary>
    public PlayMode PlayMode { get; set; } = PlayMode.Auto;

    /// <summary>
    /// 播放所在屏幕：0 = 默认当前屏幕（设置窗所在屏）；
    /// 1..N = 按从左到右排序后的第 N 块屏幕。
    /// </summary>
    public int PlaybackDisplayIndex { get; set; }

    /// <summary>待机屏：系统桌面无操作多少秒后进入播放。</summary>
    public double IdleSeconds { get; set; } = 60;

    /// <summary>新文件判定稳定所需的最短静止时间（秒）。</summary>
    public double FileStableSeconds { get; set; } = 0.5;

    /// <summary>条纹方向：竖条 / 横条。</summary>
    public StripOrientation StripOrientation { get; set; } = StripOrientation.Vertical;

    /// <summary>分成的条带数量。</summary>
    public int StripCount { get; set; } = 24;

    /// <summary>单条翻转时长（毫秒）。</summary>
    public int FlipDurationMs { get; set; } = 1000;

    /// <summary>相邻竖条延迟（毫秒）。</summary>
    public int StaggerMs { get; set; } = 55;

    /// <summary>切换完成后停留（秒）。</summary>
    public double DwellSeconds { get; set; } = 2.0;

    /// <summary>当前激活的方案 Id；null/空表示「自定义设置」。</summary>
    public string? ActiveSchemeId { get; set; }

    /// <summary>已保存的命名方案列表。</summary>
    public List<SettingsScheme> Schemes { get; set; } = [];

    [JsonIgnore]
    public bool IsCustomMode => string.IsNullOrWhiteSpace(ActiveSchemeId);

    [JsonIgnore]
    public bool HasFolder => !string.IsNullOrWhiteSpace(WatchFolder) && Directory.Exists(WatchFolder);

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GratingPlayer",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new AppSettings();

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded is null)
                return new AppSettings();

            loaded.Normalize();
            return loaded;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StorePath, json);
        }
        catch
        {
            // 保存失败不抛出，避免拖垮 UI 事件
        }
    }

    public void Normalize()
    {
        if (!double.IsFinite(DwellSeconds) || DwellSeconds < 0.5)
            DwellSeconds = 2.0;
        if (!double.IsFinite(FileStableSeconds) || FileStableSeconds < 0.5)
            FileStableSeconds = 0.5;
        if (!double.IsFinite(IdleSeconds))
            IdleSeconds = 60;

        StripCount = Math.Clamp(StripCount <= 0 ? 24 : StripCount, 1, 200);
        FlipDurationMs = Math.Clamp(FlipDurationMs <= 0 ? 1000 : FlipDurationMs, 120, 5000);
        StaggerMs = Math.Clamp(StaggerMs, 0, 400);
        IdleSeconds = Math.Clamp(IdleSeconds, 5, 3600);
        if (PlaybackDisplayIndex < 0)
            PlaybackDisplayIndex = 0;
        if (!Enum.IsDefined(NewFileAppendMode))
            NewFileAppendMode = NewFileAppendMode.EndOfQueue;
        if (!Enum.IsDefined(PlayOrder))
            PlayOrder = PlayOrder.AsRead;
        if (!Enum.IsDefined(PlayMode))
            PlayMode = PlayMode.Auto;
        if (!Enum.IsDefined(StripOrientation))
            StripOrientation = StripOrientation.Vertical;
        WatchFolder = WatchFolder?.Trim() ?? string.Empty;
        Schemes ??= [];
        Schemes.RemoveAll(s => s is null || string.IsNullOrWhiteSpace(s.Name));
        foreach (var scheme in Schemes)
        {
            scheme.Id = string.IsNullOrWhiteSpace(scheme.Id) ? Guid.NewGuid().ToString("N") : scheme.Id;
            scheme.Name = scheme.Name.Trim();
            scheme.StripCount = Math.Clamp(scheme.StripCount, 1, 200);
            scheme.FlipDurationMs = Math.Clamp(scheme.FlipDurationMs <= 0 ? 1000 : scheme.FlipDurationMs, 120, 5000);
            scheme.IdleSeconds = Math.Clamp(scheme.IdleSeconds, 5, 3600);
            if (scheme.DwellSeconds < 0.5)
                scheme.DwellSeconds = 0.5;
        }

        if (!string.IsNullOrWhiteSpace(ActiveSchemeId) &&
            Schemes.All(s => !string.Equals(s.Id, ActiveSchemeId, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveSchemeId = null;
        }
    }

    public SettingsScheme? FindScheme(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return Schemes.FirstOrDefault(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
