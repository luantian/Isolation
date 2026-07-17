using System.Collections.Concurrent;
using Serilog;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 数据缓冲服务 —— 数据库不可用时暂存数据，恢复后自动补写。
/// 防止主从切换空窗期内采集数据丢失。
/// 单例模式，通过 Instance 访问。
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
    }

    #endregion

    #region 字段

    private readonly ConcurrentQueue<BufferedItem> _buffer = new();
    private readonly long _maxBufferMemoryBytes;
    private long _currentBufferMemoryBytes;
    private Timer? _flushTimer;
    private bool _isFlushing;
    private readonly object _flushLock = new();

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

        // 每 5 秒尝试刷新一次缓冲区
        _flushTimer = new Timer(OnFlushTimer, null, 5000, 5000);

        Log.Information("数据缓冲服务已初始化，最大缓冲内存 {MaxMB} MB", maxBufferMemoryMB);
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

            foreach (var item in items)
            {
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
                        // 放回缓冲区稍后重试
                        _buffer.Enqueue(item);
                        break; // 数据库可能还没恢复，停止刷新
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "刷新缓冲数据失败: {Desc}", item.Description);
                    _buffer.Enqueue(item);
                    break;
                }
            }

            if (flushed > 0)
            {
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
    /// 清空缓冲区（丢弃所有缓冲数据）
    /// </summary>
    public void Clear()
    {
        while (_buffer.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _currentBufferMemoryBytes, 0);
        BufferSizeChanged?.Invoke(0);
        Log.Information("数据缓冲区已清空");
    }

    #endregion

    #region 定时器

    private void OnFlushTimer(object? state)
    {
        if (_buffer.IsEmpty || _isFlushing) return;

        try
        {
            FlushAsync().GetAwaiter().GetResult();
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
