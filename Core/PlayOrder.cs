namespace GratingPlayer.Core;

/// <summary>开始播放时的初始队列排序。</summary>
public enum PlayOrder
{
    /// <summary>不额外排序，按文件系统枚举顺序。</summary>
    AsRead = 0,
    NameAsc = 1,
    NameDesc = 2,
    CreatedAsc = 3,
    CreatedDesc = 4,
    ModifiedAsc = 5,
    ModifiedDesc = 6
}
