# 照片放映器（GratingPlayer）

本地图片全屏轮播工具。以竖/横条纹波浪翻转动画切换照片，适合展览、展台、待机展示等场景。

## 功能

- **目录播放**：选择本地目录，可选包含多级子文件夹；支持 jpg/jpeg/png/bmp/gif/tif/tiff/webp
- **新图监控**：实时监控新增图片，可追加到队列末尾或下一张之前
- **播放顺序**：默认文件顺序，或按文件名 / 创建时间 / 修改时间正序、倒序
- **播放方式**
  - 连续播放：全屏循环；空格暂停/继续；Esc 或右键「退出播放」
  - 待机后播放：键鼠空闲达到设定秒数后自动开播；按钮显示实时倒计时「n秒后播放（点击取消）」
  - 新图播放：只播监控期间新增的图
- **播放音乐**（可选）：指定音频文件路径或浏览选择
  - 音乐次数：循环，或按 1–1000 次播放
  - 连续/待机：进入播放后按设定播放；新图模式在「按次数」时每来一张新图重头播指定次数
  - 退出播放时音乐立刻停止
- **动画参数**：竖/横条纹、条纹数量、动画时长、停留时长
- **多屏**：可选当前屏或指定主屏 / 扩展屏全屏播放
- **方案管理**：保存命名方案并套用（含音乐设置）；支持改名、删除、清空；方案模式下参数锁定

## 环境

- Windows 10 19041+ / Windows 11
- .NET 9 / WinUI 3（Windows App SDK）
- 可编 x86 / x64 / ARM64；支持便携发布（整目录拷贝运行，无需安装 .NET）

配置保存在 `%LocalAppData%\GratingPlayer\settings.json`。

## 构建

```bash
dotnet build GratingPlayer.csproj -c Release -p:Platform=x64
```

自包含便携发布示例：

```bash
dotnet publish GratingPlayer.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -o ./发布/光栅播放器-便携-x64
```

## 版本

- **v1.1.0**：播放音乐与次数、待机倒计时按钮、退出播放菜单等（见 [Releases](https://github.com/lihonggh/GratingPlayer/releases)）
