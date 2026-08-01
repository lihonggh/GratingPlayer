using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;

namespace GratingPlayer.Core;

/// <summary>
/// 监控目录新增图片；仅当最后修改时间已静止超过阈值（默认 0.5 秒）且可读时，才视为可用新文件。
/// </summary>
public sealed class ImageDirectoryMonitor : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private string? _folder;
    private bool _includeSubdirectories;
    private TimeSpan _stableAge = TimeSpan.FromSeconds(0.5);
    private bool _disposed;

    /// <summary>稳定可用的新图片路径（已去重，相对当前 known 集合）。</summary>
    public event Action<IReadOnlyList<string>>? StableFilesAdded;

    public ImageDirectoryMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start(
        string folder,
        bool includeSubdirectories,
        TimeSpan stableAge,
        IEnumerable<string>? alreadyKnown = null)
    {
        Stop();

        _folder = folder;
        _includeSubdirectories = includeSubdirectories;
        _stableAge = stableAge < TimeSpan.FromMilliseconds(500)
            ? TimeSpan.FromMilliseconds(500)
            : stableAge;

        _known.Clear();
        _pending.Clear();
        if (alreadyKnown is not null)
        {
            foreach (var p in alreadyKnown)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    _known[p] = 0;
            }
        }

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.CreationTime
                               | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
        }
        catch
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        _pollTimer = new Timer(_ => PollPending(), null, 200, 200);
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFsEvent;
            _watcher.Changed -= OnFsEvent;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        _pollTimer?.Dispose();
        _pollTimer = null;
        _pending.Clear();
    }

    public void SeedKnown(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p))
                _known[p] = 0;
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => TrackCandidate(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        TrackCandidate(e.FullPath);
        // 旧名若在 pending 中可移除
        _pending.TryRemove(e.OldFullPath, out _);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // 监视器出错时尝试重扫目录中的未知文件
        try
        {
            if (string.IsNullOrWhiteSpace(_folder) || !Directory.Exists(_folder))
                return;

            foreach (var path in ImageLoader.ScanFolder(_folder, _includeSubdirectories, PlayOrder.AsRead))
            {
                if (!_known.ContainsKey(path))
                    TrackCandidate(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void TrackCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        // 目录变化：扫描其下（仅一层或递归由 IncludeSubdirectories 决定）中未知图片
        try
        {
            if (Directory.Exists(path))
            {
                if (!_includeSubdirectories &&
                    !string.Equals(Path.GetFullPath(path).TrimEnd('\\'),
                        Path.GetFullPath(_folder ?? string.Empty).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var option = _includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(path, "*.*", option))
                {
                    if (ImageLoader.IsSupportedImage(file) && !_known.ContainsKey(file))
                        _pending[file] = DateTime.UtcNow;
                }

                return;
            }
        }
        catch
        {
            // ignore
        }

        if (!ImageLoader.IsSupportedImage(path))
            return;

        if (_known.ContainsKey(path))
            return;

        _pending[path] = DateTime.UtcNow;
    }

    private void PollPending()
    {
        if (_pending.IsEmpty)
            return;

        var ready = new List<string>();
        var now = DateTime.UtcNow;

        foreach (var kv in _pending.ToArray())
        {
            var path = kv.Key;
            try
            {
                if (!File.Exists(path))
                {
                    _pending.TryRemove(path, out _);
                    continue;
                }

                if (_known.ContainsKey(path))
                {
                    _pending.TryRemove(path, out _);
                    continue;
                }

                var lastWrite = File.GetLastWriteTimeUtc(path);
                var idle = now - lastWrite;
                if (idle < _stableAge)
                    continue;

                // 再读一次，确保轮询间隙没有继续写入
                var lastWrite2 = File.GetLastWriteTimeUtc(path);
                if (lastWrite2 != lastWrite || DateTime.UtcNow - lastWrite2 < _stableAge)
                    continue;

                if (!IsReadable(path))
                    continue;

                if (_known.TryAdd(path, 0))
                {
                    ready.Add(path);
                    _pending.TryRemove(path, out _);
                }
                else
                {
                    _pending.TryRemove(path, out _);
                }
            }
            catch
            {
                // 文件可能仍被占用，下一轮再试
            }
        }

        if (ready.Count == 0)
            return;

        ready.Sort(StringComparer.OrdinalIgnoreCase);
        RaiseReady(ready);
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.Length >= 0;
        }
        catch
        {
            return false;
        }
    }

    private void RaiseReady(List<string> ready)
    {
        var handler = StableFilesAdded;
        if (handler is null)
            return;

        if (_dispatcher.HasThreadAccess)
            handler(ready);
        else
            _dispatcher.TryEnqueue(() => handler(ready));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        StableFilesAdded = null;
    }
}
