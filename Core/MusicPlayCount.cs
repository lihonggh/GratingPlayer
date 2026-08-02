namespace GratingPlayer.Core;

/// <summary>背景音乐播放方式。</summary>
public enum MusicPlayCount
{
    /// <summary>循环播放（默认模式）。</summary>
    Loop = 0,

    /// <summary>按指定次数播放（见 <see cref="AppSettings.MusicRepeatTimes"/>）。</summary>
    Times = 1
}
