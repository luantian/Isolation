using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
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
        /// 刷新失败次数（含返回 false 与抛异常）。达到上限后该项被丢弃，
        /// 防止永久性失败项（如会话行不存在）卡死队头、阻塞其后所有缓冲项。
        /// </summary>
        public int RetryCount { get; set; }

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
    /// 磁盘持久化的数据项（可序列化）。仅元数据清单——重试委托无法序列化，
    /// 落盘目的是让下次启动生成恢复报告，提示操作员哪些数据未入库需人工处理。
    /// </summary>
    private class DiskBufferedItem
    {
        public Guid Id { get; set; }
        public BufferOperationType OperationType { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTime BufferedAt { get; set; }
        public long EstimatedSizeBytes { get; set; }
    }

    #endregion

    #region 字段

    private readonly ConcurrentQueue<BufferedItem> _buffer = new();

    /// <summary>
    /// 单项缓冲数据的最大刷新重试次数，超过即丢弃并告警，避免毒丸卡死队列。
    /// 仅统计"数据库可达但写入仍失败"的业务性失败（如会话行不存在）；
    /// 数据库不可达期间（宕机/切换窗口）不累计——那时丢弃等于数据白丢。
    /// </summary>
    private const int MaxRetryCountPerItem = 10;
    private readonly long _maxBufferMemoryBytes;
    private long _currentBufferMemoryBytes;
    private Timer? _flushTimer;
    private bool _isFlushing;
    private readonly object _flushLock = new();

    // 磁盘缓冲
    private readonly string _diskBufferDir;
    private const double DiskPersistThreshold = 0.9; // 90% 内存时开始持久化到磁盘

    /// <summary>
    /// 数据库可达性探测的测试注入点（测试进程无真实数据库时替换，用完置 null）。
    /// </summary>
    internal static Func<Task<bool>>? DatabaseReachableProbeOverride;

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
    /// 快速探测当前活跃数据库是否可达（2 秒超时）。
    /// 仅探测连通性：实例活着但目标库不存在（4060）也视为可达——那属业务层问题，
    /// 交给 RetryCount 计数处理；网络不可达/超时才视为宕机。
    /// </summary>
    private static async Task<bool> IsDatabaseReachableAsync()
    {
        if (DatabaseReachableProbeOverride != null)
            return await DatabaseReachableProbeOverride();

        var connStr = Data.DbContextFactory.GetActiveConnectionString();
        if (string.IsNullOrWhiteSpace(connStr)) return false;

        try
        {
            var builder = new SqlConnectionStringBuilder(connStr) { ConnectTimeout = 2 };
            using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch (SqlException ex) when (ex.Number == 4060)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 刷新缓冲区：尝试将所有缓冲数据写入数据库。
    /// 宕机熔断：数据库不可达时本轮直接跳过且【不累计失败次数】——
    /// 否则主库故障切换窗口（健康检查判定 + 连接超时，可超过 50 秒）内
    /// 每 5 秒一轮的空转会把缓冲项推向毒丸丢弃上限，数据库恢复前数据先被扔掉。
    /// </summary>
    public async Task FlushAsync()
    {
        if (_buffer.IsEmpty) return;
        if (!Monitor.TryEnter(_flushLock)) return;

        try
        {
            _isFlushing = true;

            // 宕机熔断：不可达则整轮跳过，数据原样留在队列
            if (!await IsDatabaseReachableAsync())
            {
                Log.Debug("[DataBuffer] 数据库不可达，本轮刷新跳过（缓冲 {Count} 条待恢复）", _buffer.Count);
                return;
            }

            var flushed = 0;
            var total = _buffer.Count;

            // 取出所有待处理项
            var items = new List<BufferedItem>();
            while (_buffer.TryDequeue(out var item))
            {
                items.Add(item);
            }

            var succeededItems = new List<BufferedItem>();
            int discarded = 0;

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
                        succeededItems.Add(item);
                    }
                    else
                    {
                        item.RetryCount++;
                        if (item.RetryCount >= MaxRetryCountPerItem)
                        {
                            // 毒丸防护：单项反复失败（如会话行不存在等永久性错误）时丢弃，
                            // 否则它会永远卡在队头，导致其后所有缓冲项滞留、内存耗尽后丢弃最旧数据。
                            // 丢弃前把元数据落盘：下次启动 RecoverFromDisk 会生成恢复报告，
                            // 操作员至少知道丢了什么、可人工补录
                            Interlocked.Add(ref _currentBufferMemoryBytes, -item.EstimatedSizeBytes);
                            discarded++;
                            PersistDiscardedItemMetadata(item);
                            Log.Error("[DataBuffer] 缓冲项「{Desc}」连续刷新失败 {Count} 次，已丢弃以防阻塞队列（该批数据未写入数据库，元数据已落盘待生成恢复报告）",
                                item.Description, item.RetryCount);
                            continue;
                        }
                        // 数据库可能还没恢复，停止刷新
                        stop = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "刷新缓冲数据失败: {Desc}", item.Description);
                    item.RetryCount++;
                    if (item.RetryCount >= MaxRetryCountPerItem)
                    {
                        Interlocked.Add(ref _currentBufferMemoryBytes, -item.EstimatedSizeBytes);
                        discarded++;
                        PersistDiscardedItemMetadata(item);
                        Log.Error("[DataBuffer] 缓冲项「{Desc}」连续刷新异常 {Count} 次，已丢弃以防阻塞队列（该批数据未写入数据库，元数据已落盘待生成恢复报告）",
                            item.Description, item.RetryCount);
                        continue;
                    }
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

            // 丢弃项不清理其 buffer_*.json：丢弃路径已通过 PersistDiscardedItemMetadata
            // 写入（或覆盖）报告文件，必须保留给下次启动 RecoverFromDisk 生成恢复报告；
            // 曾 90% 落盘的丢弃项文件同路径同内容，保留同样正确。

            if (succeededItems.Count > 0)
            {
                // 清理已成功写入数据库的磁盘持久化文件
                CleanupDiskFiles(succeededItems);

                Log.Information("缓冲区刷新完成：成功 {Flushed}/{Total}，丢弃 {Discarded}，剩余 {Remaining} 条",
                    flushed, total, discarded, _buffer.Count);
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
    /// 毒丸丢弃时把该项元数据写入磁盘（与 90% 落盘同格式）。
    /// 下次启动 RecoverFromDisk 会将其计入恢复报告并告警，避免数据被丢得无声无息。
    /// </summary>
    private void PersistDiscardedItemMetadata(BufferedItem item)
    {
        try
        {
            var diskItem = new DiskBufferedItem
            {
                Id = item.Id,
                OperationType = item.OperationType,
                Description = item.Description,
                BufferedAt = item.BufferedAt,
                EstimatedSizeBytes = item.EstimatedSizeBytes,
            };

            var filePath = Path.Combine(_diskBufferDir, $"buffer_{item.Id:N}.json");
            var tmpPath = filePath + ".tmp";
            var json = JsonSerializer.Serialize(diskItem, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "丢弃缓冲项元数据落盘失败: {Desc}", item.Description);
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
    /// 【C-3 修复】使用 _flushLock 与 FlushAsync 互斥，避免并发导致孤儿磁盘文件
    /// </summary>
    private void PersistToDisk()
    {
        // 与 FlushAsync 互斥：如果 flush 正在进行，跳过本次持久化
        if (!Monitor.TryEnter(_flushLock))
        {
            Log.Debug("磁盘持久化跳过：缓冲区正在刷新中");
            return;
        }

        try
        {
            // 在锁内拍快照，确保遍历期间不会有 FlushAsync 并发出队
            var itemsToPersist = _buffer.Where(b => !b.DiskPersisted).ToList();
            if (itemsToPersist.Count == 0) return;

            // 锁内做磁盘 I/O（串行化与 FlushAsync）
            // 注意：这会阻塞 FlushAsync，但磁盘写入通常很快（KB 级 JSON 文件）
            foreach (var item in itemsToPersist)
            {
                // 再次检查：可能在获取锁后已经被 flush 出队
                if (item.DiskPersisted) continue;

                var diskItem = new DiskBufferedItem
                {
                    Id = item.Id,
                    OperationType = item.OperationType,
                    Description = item.Description,
                    BufferedAt = item.BufferedAt,
                    EstimatedSizeBytes = item.EstimatedSizeBytes,
                };

                // 原子写入：先写临时文件，再重命名（防止半成品文件）
                var fileName = $"buffer_{item.Id:N}.json";
                var filePath = Path.Combine(_diskBufferDir, fileName);
                var tmpPath = filePath + ".tmp";

                var json = JsonSerializer.Serialize(diskItem, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, filePath, overwrite: true);

                item.DiskPersisted = true;
                item.DiskFilePath = filePath;
            }

            Log.Information("已将 {Count} 条缓冲数据持久化到磁盘，目录: {Dir}", itemsToPersist.Count, _diskBufferDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "持久化缓冲数据到磁盘失败");
        }
        finally
        {
            Monitor.Exit(_flushLock);
        }
    }

    /// <summary>
    /// 从磁盘恢复之前持久化的数据（应用启动时调用）
    /// 注意：由于 RetryAction（委托）无法序列化，恢复的项只保留元数据用于审计，
    /// 不会自动重试写入。应用重启后需要用户手动重新触发相关操作。
    /// </summary>
    private void RecoverFromDisk()
    {
        try
        {
            if (!Directory.Exists(_diskBufferDir)) return;

            var files = Directory.GetFiles(_diskBufferDir, "buffer_*.json");
            if (files.Length == 0) return;

            int recovered = 0;
            var recoveryLog = new System.Text.StringBuilder();

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var diskItem = JsonSerializer.Deserialize<DiskBufferedItem>(json);
                    if (diskItem == null) continue;

                    recoveryLog.AppendLine($"  - [{diskItem.BufferedAt:yyyy-MM-dd HH:mm:ss}] {diskItem.Description}");
                    recovered++;

                    // 不重新入队（因为 RetryAction 无法恢复）
                    // 只记录日志，让用户知道有哪些数据在 DB 宕机期间丢失
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "读取磁盘缓冲文件失败: {File}", file);
                }
            }

            if (recovered > 0)
            {
                // 写入恢复报告文件（供用户查看）
                var reportPath = Path.Combine(_diskBufferDir, $"recovery_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                var report = $"磁盘缓冲恢复报告\n" +
                             $"恢复时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                             $"发现 {recovered} 条未写入数据库的缓冲数据（数据库长时间不可用被丢弃、或应用异常关闭期间遗留）\n\n" +
                             $"这些数据无法自动恢复（重试逻辑无法序列化），需要手动重新触发相关操作：\n" +
                             recoveryLog.ToString();

                try
                {
                    File.WriteAllText(reportPath, report);
                    Log.Warning("从磁盘发现 {Count} 条崩溃前未写入的数据，已生成恢复报告: {Report}",
                        recovered, reportPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "写入恢复报告失败");
                }

                // 告警用户
                AlertService.ShowCriticalAlert(
                    "⚠️ 发现崩溃前未保存的数据",
                    $"应用启动时发现 {recovered} 条在上次运行期间因数据库不可用而缓存的数据。\n\n" +
                    $"这些数据无法自动恢复，请查看报告：\n{reportPath}\n\n" +
                    $"需要手动重新触发相关操作（如重新导入数据）。",
                    forceShow: true);
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
        // 先等在跑的 FlushAsync 结束：退出时恰有 flush 在执行的话，它已把全部缓冲项
        // 出队到本地列表——直接落盘会扑空（_buffer 为空），该 flush 若随后失败放回队列，
        // 进程退出这批数据既未入库也无恢复报告。最多等 3 秒：flush 可能卡在 DB 连接
        // 超时上，不能让退出流程无限阻塞；等不到锁时落盘为弱一致快照（尽力而为）。
        bool acquired = Monitor.TryEnter(_flushLock, TimeSpan.FromSeconds(3));
        try
        {
            // 退出兜底：把仍在内存缓冲、未写入库的项落盘（元数据清单，内部自带 try-catch）。
            // 下次启动 RecoverFromDisk 会据此生成恢复报告提示人工处理，
            // 避免主库断开期间正常关机时缓冲数据无报告、无告警地静默消失。
            PersistToDisk();
        }
        finally
        {
            if (acquired) Monitor.Exit(_flushLock);
        }

        _flushTimer?.Dispose();
        _flushTimer = null;
    }

    #endregion
}
