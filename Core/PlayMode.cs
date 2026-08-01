namespace GratingPlayer.Core;

/// <summary>进入播放的触发方式。</summary>
public enum PlayMode
{
    /// <summary>手动点击按钮进入播放（默认）。</summary>
    Auto = 0,

    /// <summary>系统桌面无操作达到指定秒数后，作为待机屏进入播放。</summary>
    IdleStandby = 1,

    /// <summary>监控新图：先显示默认图/黑屏；第 1 张直接展示，之后每来一张翻转一次（不循环）。</summary>
    NewImageOnly = 2
}
