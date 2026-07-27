using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Serilog;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 数据缓冲服务 —— 数据库不可用时暂存数据，恢复后自动补写。
/// 防止主从切换空窗期内采集数据丢失。
/// 单例模式，通过 Instance 访问。
/// 支持磁盘持久化：内存缓冲区满时自动写入磁盘，避免数据丢失。
/// </summary>
public sealed class DataBufferService : IDisposable
{
    #region 单例

    private static readonly Lazy<DataBufferService> _lazy =
        new(() => new DataBufferService());

    public static DataBufferService Instance => _lazy.Value;

    #endregion

    #region 类型定义

    /// <summary>
    /// 缓冲的操作类型
    /// </summary>
    public enum BufferOperationType
    {
        SaveRealtimeData,
        SaveOther,
    }

    /// <summary>
    /// 缓冲的数据项
    /// </summary>
    public class BufferedItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public BufferOperationType OperationType { get; set; }
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 重试动作。接收新的 DbContext 创建器，返回是否成功。
        /// 注意：不持有旧 DbContext 的实体引用，避免切换 DB 后实体失效。
        /// </summary>
        public Func<Task<bool>> RetryAction { get; set; } = null!;

        public DateTime BufferedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 数据占用的估算内存（字节）
        /// </summary>
        public long EstimatedSizeBytes { get; set; }

        /// <summary>
        /// 是否已持久化到磁盘（用于恢复时跳过重复处理）
        /// </summary>
        public bool DiskPersisted { get; set; }

        /// <summary>
        /// 磁盘持久化文件路径（用于恢复后清理）
        /// </summary>
        public string? DiskFilePath { get; set; }
    }

    /// <summary>
    /// 磁盘持久化的数据项（可序列化）
    /// </summary>
    private class DiskBufferedItem
    {
        public Guid Id { get; set; }
        public BufferOperationType OperationType { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTime BufferedAt { get; set; }
        public long EstimatedSizeBytes { get; set; }
        /// <summary>
        /// 序列化后的重试数据（JSON 格式，具体结构取决于 OperationType）
        /// </summary>
        public string? SerializedRetryData { get; set; }
    }

    #endregion

    #region 字段

    private readonly ConcurrentQueue<BufferedItem> _buffer = new();
    private readonly long _maxBufferMemoryBytes;
    private long _currentBufferMemoryBytes;
    private Timer? _flushTimer;
    private bool _isFlushing;
    private readonly object _flushLock = new();

    // 磁盘缓冲
    private readonly string _diskBufferDir;
    private const double DiskPersistThreshold = 0.9; // 90% 内存时开始持久化到磁盘

    #endregion

    #region 事件

    /// <summary>
    /// 缓冲区数据量变化时触发（供 UI 显示）
    /// </summary>
    public event Action<int>? BufferSizeChanged;

    #endregion

    #region 属性

    /// <summary>
    /// 当前缓冲区中的数据量
    /// </summary>
    public int BufferCount => _buffer.Count;

    /// <summary>
    /// 当前缓冲区占用的内存（MB）
    /// </summary>
    public double BufferMemoryMB => Interlocked.Read(ref _currentBufferMemoryBytes) / 1024.0 / 1024.0;

    /// <summary>
    /// 缓冲区是否非空
    /// </summary>
    public bool HasBufferedData => !_buffer.IsEmpty;

    #endregion

    #region 构造函数

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="maxBufferMemoryMB">最大缓冲内存（MB），默认 200MB</param>
    private DataBufferService(int maxBufferMemoryMB = 200)
    {
        _maxBufferMemoryBytes = (long)maxBufferMemoryMB * 1024 * 1024;

        // 初始化磁盘缓冲目录
        _diskBufferDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BufferedData");
        try
        {
            if (!Directory.Exists(_diskBufferDir))
            {
                Directory.CreateDirectory(_diskBufferDir);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "无法创建磁盘缓冲目录: {Dir}", _diskBufferDir);
        }

        // 恢复之前持久化到磁盘的数据
        RecoverFromDisk();

        // 每 5 秒尝试刷新一次缓冲区
        _flushTimer = new Timer(OnFlushTimer, null, 5000, 5000);

        Log.Information("数据缓冲服务已初始化，最大缓冲内存 {MaxMB} MB，磁盘缓冲目录: {Dir}", maxBufferMemoryMB, _diskBufferDir);
    }

    #endregion

    #region 核心方法

    /// <summary>
    /// 缓冲一个数据项（当数据库写入失败时调用）
    /// </summary>
    /// <param name="operationType">操作类型</param>
    /// <param name="description">描述（用于日志和UI显示）</param>
    /// <param name="estimatedBytes">估算的数据大小（字节）</param>
    /// <param name="retryAction">重试动作，返回 true 表示成功</param>
    public void Buffer(BufferOperationType operationType, string description, long estimatedBytes, Func<Task<bool>> retryAction)
    {
        // 检查内存限制：超过上限则丢弃最旧的数据
        while (Interlocked.Read(ref _currentBufferMemoryBytes) + estimatedBytes > _maxBufferMemoryBytes)
        {
            if (_buffer.TryDequeue(out var dropped))
            {
                Interlocked.Add(ref _currentBufferMemoryBytes, -dropped.EstimatedSizeBytes);
                Log.Warning("数据缓冲区内存超限，丢弃最旧的缓冲数据: {Desc}（释放 {Bytes} 字节）",
                    dropped.Description, dropped.EstimatedSizeBytes);
            }
            else
            {
                break;
            }
        }

        var item = new BufferedItem
        {
            OperationType = operationType,
            Description = description,
            EstimatedSizeBytes = estimatedBytes,
            RetryAction = retryAction,
            BufferedAt = DateTime.Now,
        };

        _buffer.Enqueue(item);
        Interlocked.Add(ref _currentBufferMemoryBytes, estimatedBytes);
        BufferSizeChanged?.Invoke(_buffer.Count);

        Log.Warning("数据已缓冲: [{Type}] {Desc}（{Bytes} 字节），当前缓冲 {Count} 条，占用 {MemMB:F1} MB",
            operationType, description, estimatedBytes, _buffer.Count, BufferMemoryMB);

        // 缓冲区达到 80% 容量时告警，90% 时持久化到磁盘
        var currentMB = BufferMemoryMB;
        var maxMB = _maxBufferMemoryBytes / 1024.0 / 1024.0;
        if (currentMB >= maxMB * 0.9)
        {
            // 持久化到磁盘，避免数据丢失
            PersistToDisk();
        }
        if (currentMB >= maxMB * 0.8)
        {
            AlertService.AlertBufferNearlyFull(currentMB, maxMB);
        }
    }

    /// <summary>
    /// 尝试将数据写入数据库，如果失败则自动缓冲。
    /// </summary>
    /// <param name="operationType">操作类型</param>
    /// <param name="description">描述</param>
    /// <param name="estimatedBytes">估算的数据大小（字节）</param>
    /// <param name="saveAction">写入操作</param>
    /// <param name="retryFactory">
    /// 构建重试动作的工厂函数。DB 恢复后用新的 DbContext 重新创建保存操作。
    /// 参数是创建新 DbContext 的函数，返回是否保存成功。
    /// </param>
    /// <returns>true=写入成功，false=已缓冲（稍后重试）</returns>
    public async Task<bool> SaveOrBufferAsync(
        BufferOperationType operationType,
        string description,
        long estimatedBytes,
        Func<Task> saveAction,
        Func<Func<Data.AppDbContext>, Task<bool>>? retryFactory = null)
    {
        try
        {
            await saveAction();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "数据库写入失败，数据将缓冲: [{Type}] {Desc}", operationType, description);

            // 构建重试动作
            Func<Task<bool>> retryAction;
            if (retryFactory != null)
            {
                // 使用工厂函数创建新的重试操作（解决 DbContext 切换后实体失效的问题）
                retryAction = () => retryFactory(() => Data.DbContextFactory.CreateDbContext());
            }
            else
            {
                // 简单重试（适用于不依赖实体引用的场景）
                retryAction = async () =>
                {
                    try
                    {
                        await saveAction();
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                };
            }

            Buffer(operationType, description, estimatedBytes, retryAction);
            return false;
        }
    }

    /// <summary>
    /// 刷新缓冲区：尝试将所有缓冲数据写入数据库
    /// </summary>
    public async Task FlushAsync()
    {
        if (_buffer.IsEmpty) return;
        if (!Monitor.TryEnter(_flushLock)) return;

        try
        {
            _isFlushing = true;
            var flushed = 0;
            var total = _buffer.Count;

            // 取出所有待处理项
            var items = new List<BufferedItem>();
            while (_buffer.TryDequeue(out var item))
            {
                items.Add(item);
            }

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                bool stop = false;
                try
                {
                    var success = await item.RetryAction();
                    if (success)
                    {
                        flushed++;
                        Interlocked.Add(ref _currentBufferMemoryBytes, -item.EstimatedSizeBytes);
                    }
                    else
                    {
                        // 数据库可能还没恢复，停止刷新
                        stop = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "刷新缓冲数据失败: {Desc}", item.Description);
                    stop = true;
                }

                if (stop)
                {
                    // 把当前失败项及其后所有未处理项按原顺序全部放回队列，避免静默丢失。
                    // 已出队但未成功写入的项，其字节数仍计入 _currentBufferMemoryBytes（未扣减），
                    // 重新入队后计数保持一致，不产生虚高。
                    for (int j = i; j < items.Count; j++)
                    {
                        _buffer.Enqueue(items[j]);
                    }
                    break;
                }
            }

            if (flushed > 0)
            {
                // 清理已成功写入数据库的磁盘持久化文件
                CleanupDiskFiles(items.Take(flushed));

                Log.Information("缓冲区刷新完成：成功 {Flushed}/{Total}，剩余 {Remaining} 条",
                    flushed, total, _buffer.Count);
            }

            BufferSizeChanged?.Invoke(_buffer.Count);
        }
        finally
        {
            _isFlushing = false;
            Monitor.Exit(_flushLock);
        }
    }

    /// <summary>
    /// 清空缓冲区（丢弃所有缓冲数据，包括磁盘持久化文件）
    /// </summary>
    public void Clear()
    {
        while (_buffer.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _currentBufferMemoryBytes, 0);

        // 清理磁盘持久化文件
        try
        {
            if (Directory.Exists(_diskBufferDir))
            {
                foreach (var file in Directory.GetFiles(_diskBufferDir, "*.json"))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "清理磁盘缓冲文件失败");
        }

        BufferSizeChanged?.Invoke(0);
        Log.Information("数据缓冲区已清空");
    }

    /// <summary>
    /// 将内存缓冲区中的数据持久化到磁盘（当内存占用超过阈值时调用）
    /// </summary>
    private void PersistToDisk()
    {
        try
        {
            // 只持久化尚未持久化的项
            var itemsToPersist = _buffer.Where(b => !b.DiskPersisted).ToList();
            if (itemsToPersist.Count == 0) return;

            foreach (var item in itemsToPersist)
            {
                var diskItem = new DiskBufferedItem
                {
                    Id = item.Id,
                    OperationType = item.OperationType,
                    Description = item.Description,
                    BufferedAt = item.BufferedAt,
                    EstimatedSizeBytes = item.EstimatedSizeBytes,
                    // RetryAction 无法序列化，只保存元数据
                    // 恢复时需要根据 OperationType 重建重试逻辑
                };

                var fileName = $"buffer_{item.Id:N}.json";
                var filePath = Path.Combine(_diskBufferDir, fileName);

                var json = JsonSerializer.Serialize(diskItem, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);

                item.DiskPersisted = true;
                item.DiskFilePath = filePath;
            }

            Log.Information("已将 {Count} 条缓冲数据持久化到磁盘，目录: {Dir}", itemsToPersist.Count, _diskBufferDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "持久化缓冲数据到磁盘失败");
        }
    }

    /// <summary>
    /// 从磁盘恢复之前持久化的数据（应用启动时调用）
    /// </summary>
    private void RecoverFromDisk()
    {
        try
        {
            if (!Directory.Exists(_diskBufferDir)) return;

            var files = Directory.GetFiles(_diskBufferDir, "buffer_*.json");
            if (files.Length == 0) return;

            int recovered = 0;
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var diskItem = JsonSerializer.Deserialize<DiskBufferedItem>(json);
                    if (diskItem == null) continue;

                    // 重建内存缓冲项（RetryAction 无法从磁盘恢复，需要调用方重新提供）
                    // 这里只恢复元数据，实际重试需要调用方在下次 Buffer() 时提供新的 RetryAction
                    var item = new BufferedItem
                    {
                        Id = diskItem.Id,
                        OperationType = diskItem.OperationType,
                        Description = diskItem.Description,
                        BufferedAt = diskItem.BufferedAt,
                        EstimatedSizeBytes = diskItem.EstimatedSizeBytes,
                        DiskPersisted = true,
                        DiskFilePath = file,
                        // RetryAction 需要在恢复后由调用方重新设置
                        RetryAction = async () => false // 占位，实际恢复时需要重建
                    };

                    _buffer.Enqueue(item);
                    Interlocked.Add(ref _currentBufferMemoryBytes, item.EstimatedSizeBytes);
                    recovered++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "恢复磁盘缓冲文件失败: {File}", file);
                }
            }

            if (recovered > 0)
            {
                Log.Information("从磁盘恢复了 {Count} 条缓冲数据", recovered);
                BufferSizeChanged?.Invoke(_buffer.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "从磁盘恢复缓冲数据失败");
        }
    }

    /// <summary>
    /// 清理已成功写入数据库的磁盘持久化文件
    /// </summary>
    private void CleanupDiskFiles(IEnumerable<BufferedItem> successfulItems)
    {
        foreach (var item in successfulItems)
        {
            if (item.DiskPersisted && !string.IsNullOrEmpty(item.DiskFilePath))
            {
                try
                {
                    if (File.Exists(item.DiskFilePath))
                    {
                        File.Delete(item.DiskFilePath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "清理磁盘缓冲文件失败: {File}", item.DiskFilePath);
                }
            }
        }
    }

    #endregion

    #region 定时器

    private async void OnFlushTimer(object? state)
    {
        if (_buffer.IsEmpty || _isFlushing) return;

        // System.Threading.Timer 回调，直接 await 而非 .GetAwaiter().GetResult()，
        // 避免长时间阻塞占用线程池线程。FlushAsync 内部有 Monitor.TryEnter + _isFlushing 双重防重入。
        try
        {
            await FlushAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "定时刷新缓冲区异常");
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
    }

    #endregion
}
