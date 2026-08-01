namespace GratingPlayer.Core;

/// <summary>动态监控到的新文件如何插入播放队列。</summary>
public enum NewFileAppendMode
{
    /// <summary>追加到整体队列末尾。</summary>
    EndOfQueue = 0,
    /// <summary>插入到下一张将要播放的位置。</summary>
    NextPlayPosition = 1
}
