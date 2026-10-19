using System.Text;
using ClaudeUsageMonitor.Core.Infrastructure;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Logs;

/// <summary>監視対象のログディレクトリ。WSL の UNC パスは変更通知が届かないため IsRemote でポーリングのみにする。</summary>
public sealed record LogSource(string Directory, bool IsRemote);

/// <summary>
/// セッションログを差分読み込みして集計 DB に取り込む。
/// ローカルは FileSystemWatcher、WSL はポーリングで更新を検知する。
/// </summary>
public sealed class LogCollector : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    private readonly UsageRepository _repository;
    private readonly Func<CancellationToken, Task<IReadOnlyList<LogSource>>> _sourcesProvider;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounceTimer;
    private Dictionary<string, FileProgress>? _progress;
    private bool _disposed;

    public LogCollector(UsageRepository repository, Func<CancellationToken, Task<IReadOnlyList<LogSource>>> sourcesProvider)
    {
        _repository = repository;
        _sourcesProvider = sourcesProvider;
        _debounceTimer = new Timer(_ => _ = ScanSafeAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>新しい応答を取り込んだときに、その件数とともに通知する。</summary>
    public event Action<int>? RecordsAdded;

    /// <summary>すべての監視対象を走査する。走査中に呼ばれた場合は前の走査の完了を待ってから実行する。</summary>
    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sources = await _sourcesProvider(ct).ConfigureAwait(false);
            UpdateWatchers(sources);

            _progress ??= _repository.LoadFileProgress();
            var added = 0;
            foreach (var source in sources)
            {
                if (!Directory.Exists(source.Directory))
                    continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(source.Directory, "*.jsonl", SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"{source.Directory} の列挙に失敗しました: {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    added += ScanFile(file);
                }
            }

            if (added > 0)
                RecordsAdded?.Invoke(added);
            return added;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task ScanSafeAsync()
    {
        try
        {
            await ScanAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            AppLog.Error("ログの走査に失敗しました", ex);
        }
    }

    private int ScanFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return 0;

            var progressMap = _progress!;
            var progress = progressMap.GetValueOrDefault(path) ?? new FileProgress(0, DateTime.MinValue);
            var offset = progress.Offset;
            if (info.Length == offset)
                return 0;
            if (info.Length < offset)
                offset = 0; // 作り直されたファイルは先頭から読み直す

            var added = 0;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[ChunkSize];
            var pending = new List<byte>();

            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                pending.AddRange(buffer.AsSpan(0, read));
                var lastNewline = pending.LastIndexOf((byte)'\n');
                if (lastNewline < 0)
                    continue;

                // 改行で終わっている（書き込みが完了した）行だけを処理し、残りは次回に回す
                var complete = pending.GetRange(0, lastNewline + 1).ToArray();
                pending.RemoveRange(0, lastNewline + 1);

                var records = ParseLines(complete);
                offset += complete.Length;
                var newProgress = new FileProgress(offset, info.LastWriteTimeUtc);
                _repository.SaveChunk(path, newProgress, records);
                progressMap[path] = newProgress;
                added += records.Count;
            }

            return added;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ロック中などは次回に回す
            AppLog.Warn($"{path} の読み込みに失敗しました: {ex.Message}");
            return 0;
        }
    }

    private static List<UsageRecord> ParseLines(byte[] data)
    {
        var records = new List<UsageRecord>();
        var text = Encoding.UTF8.GetString(data);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0 && LogLineParser.TryParse(line, out var record))
                records.Add(record);
        }
        return records;
    }

    private void UpdateWatchers(IReadOnlyList<LogSource> sources)
    {
        var wanted = sources.Where(s => !s.IsRemote && Directory.Exists(s.Directory))
            .Select(s => s.Directory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in _watchers.Keys.Where(d => !wanted.Contains(d)).ToList())
        {
            _watchers[dir].Dispose();
            _watchers.Remove(dir);
        }

        foreach (var dir in wanted.Where(d => !_watchers.ContainsKey(d)))
        {
            try
            {
                var watcher = new FileSystemWatcher(dir, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Renamed += OnFileChanged;
                watcher.Error += (_, e) => AppLog.Warn($"ファイル監視でエラーが発生しました: {e.GetException().Message}");
                watcher.EnableRaisingEvents = true;
                _watchers[dir] = watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                AppLog.Warn($"{dir} の監視を開始できませんでした: {ex.Message}");
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!_disposed)
            _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _disposed = true;
        _debounceTimer.Dispose();
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();
    }
}
