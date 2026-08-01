namespace GratingPlayer.Core;

/// <summary>可命名保存的一套播放参数。</summary>
public sealed class SettingsScheme
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>方案创建时间（本地时间）。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string WatchFolder { get; set; } = string.Empty;

    public bool IncludeSubdirectories { get; set; } = true;

    public bool WatchNewFiles { get; set; }

    public NewFileAppendMode NewFileAppendMode { get; set; } = NewFileAppendMode.EndOfQueue;

    public PlayOrder PlayOrder { get; set; } = PlayOrder.AsRead;

    public PlayMode PlayMode { get; set; } = PlayMode.Auto;

    public int PlaybackDisplayIndex { get; set; }

    public double IdleSeconds { get; set; } = 60;

    public double FileStableSeconds { get; set; } = 0.5;

    public StripOrientation StripOrientation { get; set; } = StripOrientation.Vertical;

    public int StripCount { get; set; } = 24;

    public int FlipDurationMs { get; set; } = 1000;

    public int StaggerMs { get; set; } = 55;

    public double DwellSeconds { get; set; } = 2.0;

    public static SettingsScheme FromSettings(AppSettings s, string name)
    {
        return new SettingsScheme
        {
            Name = name.Trim(),
            CreatedAt = DateTime.Now,
            WatchFolder = s.WatchFolder,
            IncludeSubdirectories = s.IncludeSubdirectories,
            WatchNewFiles = s.WatchNewFiles,
            NewFileAppendMode = s.NewFileAppendMode,
            PlayOrder = s.PlayOrder,
            PlayMode = s.PlayMode,
            PlaybackDisplayIndex = s.PlaybackDisplayIndex,
            IdleSeconds = s.IdleSeconds,
            FileStableSeconds = s.FileStableSeconds,
            StripOrientation = s.StripOrientation,
            StripCount = s.StripCount,
            FlipDurationMs = s.FlipDurationMs,
            StaggerMs = s.StaggerMs,
            DwellSeconds = s.DwellSeconds
        };
    }

    public void ApplyTo(AppSettings s)
    {
        s.WatchFolder = WatchFolder;
        s.IncludeSubdirectories = IncludeSubdirectories;
        s.WatchNewFiles = WatchNewFiles;
        s.NewFileAppendMode = NewFileAppendMode;
        s.PlayOrder = PlayOrder;
        s.PlayMode = PlayMode;
        s.PlaybackDisplayIndex = PlaybackDisplayIndex;
        s.IdleSeconds = IdleSeconds;
        s.FileStableSeconds = FileStableSeconds;
        s.StripOrientation = StripOrientation;
        s.StripCount = StripCount;
        s.FlipDurationMs = FlipDurationMs;
        s.StaggerMs = StaggerMs;
        s.DwellSeconds = DwellSeconds;
        s.Normalize();
    }
}
