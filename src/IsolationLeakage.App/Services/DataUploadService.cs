using System.Globalization;
using System.IO;
using System.Text.Json;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 数据上传服务：解析数据包、校验并上传试验数据
/// </summary>
public sealed class DataUploadService
{
    private readonly TestRecordService _testRecordService;

    public DataUploadService(TestRecordService testRecordService)
    {
        _testRecordService = testRecordService;
    }

    /// <summary>
    /// 是否唯一键冲突（多客户端并发插入同一记录时的预期竞争）。
    /// 按 SqlException 错误号 2601/2627 判定（语言无关），并兼容中英文消息文本兜底——
    /// 中文版 SQL Server 的消息是"不能在对象...中插入重复键"，仅匹配英文会漏判。
    /// </summary>
    private static bool IsDuplicateKeyError(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is Microsoft.Data.SqlClient.SqlException sql && (sql.Number == 2601 || sql.Number == 2627))
            {
                return true;
            }
        }

        return ex.Message.Contains("Cannot insert duplicate key")
               || ex.Message.Contains("插入重复键");
    }

    /// <summary>
    /// 解析数据包文件（JSON / 文本键值对 / 真实装置 CSV）
    /// </summary>
    /// <param name="filePath">数据包文件路径</param>
    /// <returns>解析后的数据包对象</returns>
    public async Task<ParsedDataPackage> ParseDataPackageAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("数据包文件不存在", filePath);
        }

        // JSON / TXT 路径走 ReadAllTextAsync，同样受大小防线保护（CSV 在 ReadTextWithEncoding 内检查）
        EnsureParseableSize(filePath);

        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension == ".json")
        {
            var json = await File.ReadAllTextAsync(filePath);
            return await ParseJsonAsync(json);
        }

        if (extension == ".csv")
        {
            // 真实装置导出 CSV，可能是 GBK 编码，需按编码读取
            var csv = await ReadTextWithEncodingAsync(filePath);

            // 根据CSV内容类型选择解析方式
            var csvKind = SniffCsvKindFromContent(csv);
            if (csvKind == CsvKind.Summary)
            {
                // 结果汇总CSV：键值对或简单表头格式，包含对象/装置/泄漏率/判定等元数据
                return ParseResultSummaryCsv(csv);
            }
            else
            {
                // 曲线CSV或其他：时序数据格式，包含压力/流量/温度等通道数据
                return ParseDeviceCsv(csv);
            }
        }

        var content = await File.ReadAllTextAsync(filePath);
        return await ParseTextAsync(content);
    }

    /// <summary>
    /// 单文件解析的大小上限（256MB）。超过几乎必然是选错了文件（误选导出目录/数据库文件等），
    /// 全量载入会造成数百 MB 级内存峰值。真实曲线 CSV（数小时 × 1Hz × 多通道）通常仅 MB 量级。
    /// 超限时抛出明确异常，批量导入按单文件失败跳过、批次继续（需求 §11.2 可靠性）。
    /// </summary>
    private const long MaxTextParseFileSizeBytes = 256L * 1024 * 1024;

    /// <summary>解析前检查文件大小，超限快速失败（避免全量读入后 OOM）。</summary>
    private static void EnsureParseableSize(string filePath)
    {
        var length = new FileInfo(filePath).Length;
        if (length > MaxTextParseFileSizeBytes)
        {
            throw new InvalidOperationException(
                $"文件过大（{length / 1024.0 / 1024.0:F0} MB，上限 {MaxTextParseFileSizeBytes / 1024 / 1024} MB），" +
                "已跳过解析。请确认选择的是测量装置导出的数据文件。");
        }
    }

    /// <summary>
    /// 读取文本文件，自动处理 UTF-8 / GBK 编码（真实装置 CSV 多为 GBK）。
    /// </summary>
    private static async Task<string> ReadTextWithEncodingAsync(string filePath)
    {
        EnsureParseableSize(filePath);
        var bytes = await File.ReadAllBytesAsync(filePath);
        return DecodeBytes(bytes);
    }

    /// <summary>同步版本（供文件嗅探使用）。</summary>
    private static string ReadTextWithEncoding(string filePath)
    {
        EnsureParseableSize(filePath);
        var bytes = File.ReadAllBytes(filePath);
        return DecodeBytes(bytes);
    }

    /// <summary>按 UTF-8(BOM/严格) → GBK 顺序解码字节。</summary>
    private static string DecodeBytes(byte[] bytes)
    {
        // 带 BOM 的 UTF-8
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        // 先尝试严格 UTF-8 解码，失败说明是 GBK
        try
        {
            var strictUtf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            // GBK（中文 Windows 默认，代码页 936）。需注册 CodePagesEncodingProvider。
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    /// <summary>
    /// 校验并上传解析后的数据（自动选择配方）
    /// </summary>
    public Task<TestRecord> ValidateAndUploadAsync(
        ParsedDataPackage parsedData,
        string recordCode,
        string projectCode,
        string unitCode,
        string operatorName)
    {
        // 不传 forceRecipeId 时，自动使用试验对象关联的默认配方
        return ValidateAndUploadAsync(parsedData, recordCode, projectCode, unitCode, operatorName, null);
    }

    /// <summary>
    /// 校验并上传解析后的数据（支持手动指定配方）
    /// </summary>
    /// <param name="forceRecipeId">强制指定的配方ID（null=自动选择，0=不使用配方，其他值=使用指定配方）</param>
    public async Task<TestRecord> ValidateAndUploadAsync(
        ParsedDataPackage parsedData,
        string recordCode,
        string projectCode,
        string unitCode,
        string operatorName,
        int? forceRecipeId)
    {
        // 1. 校验必填字段
        ValidateRequiredFields(parsedData, recordCode, projectCode, unitCode, operatorName);

        // 2. 检查重复记录（相同对象 + 相同时间 = 重复）
        await CheckDuplicateAsync(parsedData.ObjectCode!, parsedData.TestTime);

        // 2.5 检查测量装置是否存在（避免因外键约束导致保存失败）
        await CheckDeviceExistsAsync(parsedData.DeviceCode!);

        // 3. 构建试验记录
        // 从试验对象路径节点读取泄漏率限值和关联配方
        decimal leakageLimit = 0;
        int? testRecipeId = null;
        string? recipeSnapshotJson = null;
        int? recipeVersionNumber = null;
        string? objectName = null;
        try
        {
            var node = await AppServices.DbContext.TestObjectPathNodes
                .AsNoTracking()
                .Include(n => n.DefaultRecipe)
                .FirstOrDefaultAsync(n => n.Code == parsedData.ObjectCode);
            if (node?.LeakageLimit.HasValue == true)
                leakageLimit = node.LeakageLimit.Value;
            objectName = node?.Name;

            // 【关键逻辑】配方选择策略
            int? actualRecipeId = null;

            if (forceRecipeId.HasValue && forceRecipeId.Value > 0)
            {
                // 用户手动指定了配方 → 优先使用
                actualRecipeId = forceRecipeId.Value;
            }
            else if (!forceRecipeId.HasValue && node?.DefaultRecipeId.HasValue == true)
            {
                // 用户未指定，且试验对象有关联配方 → 使用默认配方
                actualRecipeId = node.DefaultRecipeId.Value;
            }
            // forceRecipeId == 0 → 明确不使用配方

            // 如果确定了使用配方，则创建快照
            if (actualRecipeId.HasValue && actualRecipeId.Value > 0)
            {
                testRecipeId = actualRecipeId.Value;

                // 创建配方快照（永久保存试验时的配方参数，不受后续配方修改影响）
                recipeSnapshotJson = await AppServices.RecipeService.CreateSnapshotForTestAsync(actualRecipeId.Value);

                // 获取配方当前版本号
                recipeVersionNumber = await AppServices.RecipeService.GetCurrentVersionAsync(actualRecipeId.Value);

                // 优先使用配方的泄漏率设计最大值作为判定标准
                var recipe = await AppServices.DbContext.TestRecipes.FindAsync(actualRecipeId.Value);
                if (recipe != null && recipe.LeakageLimit > 0)
                {
                    leakageLimit = recipe.LeakageLimit;
                }
            }
        }
        catch { /* 查询失败时使用默认值 */ }

        // 【安全】CSV 文件的 LeakageLimit 不覆盖系统配置（路径节点/配方）的限值
        // 防止恶意或错误的 CSV 篡改验收判定标准。
        // 若系统未配置限值，则降级使用 CSV 提供的限值作为兜底。
        string? csvLeakageLimitNote = null;
        if (parsedData.LeakageLimit.HasValue && parsedData.LeakageLimit.Value > 0)
        {
            // 容差比较：文档限值（如 6895/60 的全精度 decimal）与库中配置（decimal(18,6) 存储舍入）
            // 数值上一致时不应误报"不一致"污染记录备注
            if (leakageLimit > 0 && Math.Abs(parsedData.LeakageLimit.Value - leakageLimit) > 0.0001m)
            {
                // 系统有限值且与 CSV 不一致 → 以系统为准，记录备注供人工复核
                csvLeakageLimitNote = $"[CSV限值{parsedData.LeakageLimit.Value:0.####}与系统限值{leakageLimit:0.####}不一致，以系统为准]";
                Log.Warning(
                    "[DataUpload] CSV 泄漏限值 {CsvLimit} 与系统配置 {SystemLimit} 不一致，以系统为准。ObjectCode={ObjectCode}",
                    parsedData.LeakageLimit.Value, leakageLimit, parsedData.ObjectCode);
            }
            else if (leakageLimit <= 0)
            {
                // 系统未配置限值 → 使用 CSV 提供的限值作为兜底
                leakageLimit = parsedData.LeakageLimit.Value;
                csvLeakageLimitNote = $"[系统未配置限值，已使用CSV限值{leakageLimit:0.####}]";
                Log.Information(
                    "[DataUpload] 系统未配置泄漏限值，使用 CSV 提供的限值 {CsvLimit}。ObjectCode={ObjectCode}",
                    leakageLimit, parsedData.ObjectCode);
            }
        }

        // 【安全】泄漏率物理范围校验（规格书：0.1 ~ 1000 Nml/min，允许 0 表示无泄漏）
        decimal leakageRate = parsedData.LeakageRate;
        if (leakageRate < 0)
        {
            Log.Warning("[DataUpload] 泄漏率为负值 {Rate}，物理上不合理，ObjectCode={ObjectCode}", leakageRate, parsedData.ObjectCode);
        }
        else if (leakageRate > 0 && (leakageRate < 0.1m || leakageRate > 1000m))
        {
            Log.Warning(
                "[DataUpload] 泄漏率 {Rate} Nml/min 超出规格书物理范围 [0.1, 1000] Nml/min，ObjectCode={ObjectCode} — 请核实",
                leakageRate, parsedData.ObjectCode);
        }

        // 【判定逻辑】Pass/Fail 优先级：
        // 1. CSV 与系统都有判定 → 一致时用之；分歧时取更严格的一方（判 Fail）——
        //    防止装置数据文件"自我认证"（CSV 报合格但泄漏率超系统限值 → 必须判不合格）；
        //    反向（CSV 报不合格、系统算合格）也尊重更严格的判定。
        // 2. 只有 CSV 有结果 → 用 CSV（装置判定，系统无判据）
        // 3. 只有系统有限值 → 系统计算
        // 4. 都没有 → Unknown
        var csvResult = MapTestResult(parsedData.Result ?? "Unknown");
        TestResult computedBySystem = (leakageLimit > 0 && leakageRate >= 0)
            ? (leakageRate <= leakageLimit ? TestResult.Pass : TestResult.Fail)
            : TestResult.Unknown;

        TestResult finalResult;
        if (csvResult != TestResult.Unknown && computedBySystem != TestResult.Unknown)
        {
            if (csvResult == computedBySystem)
            {
                finalResult = csvResult;
            }
            else
            {
                finalResult = TestResult.Fail;
                csvLeakageLimitNote ??= $"[CSV结果\"{parsedData.Result}\"与系统计算(泄漏率{leakageRate:F3} vs 限值{leakageLimit:F3})不一致，按不合格处理]";
                Log.Warning(
                    "[DataUpload] Pass/Fail 分歧: CSV={CsvResult}, 系统计算={Computed} (Rate={Rate}, Limit={Limit})，按更严格的不合格处理。ObjectCode={ObjectCode}",
                    csvResult, computedBySystem, leakageRate, leakageLimit, parsedData.ObjectCode);
            }
        }
        else if (csvResult != TestResult.Unknown)
        {
            // 只有 CSV 有判定（系统无限值）→ 采用装置结果
            finalResult = csvResult;
        }
        else if (computedBySystem != TestResult.Unknown)
        {
            finalResult = computedBySystem;
        }
        else
        {
            finalResult = TestResult.Unknown;
        }

        // 预充压压力（实验报表"预充压压力"列）：记录表无独立字段，
        // 写入备注供追溯（库存 MPa，同时换算 kPa 便于对照界面显示）
        string? remark = csvLeakageLimitNote;
        if (parsedData.PrechargePressureP2.HasValue)
        {
            var prechargeNote = $"[预充压压力 {parsedData.PrechargePressureP2.Value:0.###} MPa（{parsedData.PrechargePressureP2.Value * 1000:0.#} kPa）]";
            remark = string.IsNullOrEmpty(remark) ? prechargeNote : $"{remark} {prechargeNote}";
        }

        var testRecord = new TestRecord
        {
            RecordCode = recordCode,
            ProjectCode = projectCode,
            UnitCode = unitCode,
            ObjectCode = parsedData.ObjectCode!,
            ObjectName = objectName ?? string.Empty,
            DeviceCode = parsedData.DeviceCode!,
            TestTime = parsedData.TestTime,
            ImportTime = DateTime.Now,
            Operator = operatorName,
            TestPressure = parsedData.TestPressure,
            LeakageLimit = leakageLimit,
            FinalLeakageRate = leakageRate,
            Result = finalResult,
            Remark = remark,
            StepSummary = null,
            ResultFieldSummary = null,
            ProcessChannelSummary = null,
            CreatedAt = DateTime.Now,
            // 关联配方信息
            TestRecipeId = testRecipeId,
            RecipeSnapshotJson = recipeSnapshotJson,
            RecipeVersionNumber = recipeVersionNumber,
        };

        // 4. 构建过程数据
        TestProcessData? processData = null;
        if (parsedData.ProcessDataPoints != null && parsedData.ProcessDataPoints.Any())
        {
            processData = BuildProcessData(parsedData.ProcessDataPoints);
        }

        // 5. 插入数据库
        return await _testRecordService.AddAsync(testRecord, processData);
    }

    /// <summary>
    /// 获取试验对象关联的默认配方信息（用于上传前预览）
    /// </summary>
    public async Task<TestRecipe?> GetDefaultRecipeForObjectAsync(string objectCode)
    {
        try
        {
            var node = await AppServices.DbContext.TestObjectPathNodes
                .AsNoTracking()
                .Include(n => n.DefaultRecipe)
                .FirstOrDefaultAsync(n => n.Code == objectCode);

            return node?.DefaultRecipe;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取所有启用的配方列表（用于上传时选择）
    /// </summary>
    public async Task<List<TestRecipe>> GetEnabledRecipesAsync()
    {
        try
        {
            return await AppServices.DbContext.TestRecipes
                .AsNoTracking()
                .Where(r => r.IsEnabled)
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.RecipeName)
                .ToListAsync();
        }
        catch
        {
            return new List<TestRecipe>();
        }
    }

    #region 批量上传相关方法

    /// <summary>
    /// 递归扫描文件夹，获取所有CSV / JSON / TXT数据文件。
    /// 每个文件独立解析，不进行合并。
    /// </summary>
    public List<string> ScanFolderForPackages(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"文件夹不存在: {folderPath}");

        return Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".json" or ".txt" or ".csv")
            .ToList();
    }

    /// <summary>CSV 文件种类。</summary>
    private enum CsvKind { Curve, Summary, MultiRowRecords, Unknown }

    /// <summary>
    /// 嗅探 CSV 是"曲线文件"还是"结果汇总文件"：读取首行表头判断。
    /// 曲线文件表头含 P1/M1/温度等通道列；汇总文件含 对象/泄漏率/判定 等字段。
    /// 按 GBK/UTF-8 自动解码（真实装置 CSV 多为 GBK，默认 UTF-8 读会让中文乱码导致误判）。
    /// </summary>
    private static CsvKind SniffCsvKind(string filePath)
    {
        try
        {
            var content = ReadTextWithEncoding(filePath);
            return SniffCsvKindFromContent(content);
        }
        catch
        {
            return CsvKind.Unknown;
        }
    }

    /// <summary>基于已读取的CSV内容判断文件类型。</summary>
    private static CsvKind SniffCsvKindFromContent(string content)
    {
        try
        {
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var firstLine = lines.FirstOrDefault() ?? string.Empty;
            var lower = firstLine.ToLowerInvariant();

            // 【优先检测】实验报表格式（甲方实验报表.CSV）
            // 特征：表头含"序号/编号"+"系统"+"试验阀门编号/阀门"+"实验结果/试验结果"，且至少有1行数据。
            // 每阀门一个文件时只有"表头+1行"（共2行），整表汇总时为多行，均按此格式解析。
            bool looksMultiRow = (firstLine.Contains("序号") || firstLine.Contains("编号")) &&
                                firstLine.Contains("系统") &&
                                (firstLine.Contains("试验阀门编号") || firstLine.Contains("阀门")) &&
                                (firstLine.Contains("实验结果") || firstLine.Contains("试验结果") || firstLine.Contains("合格")) &&
                                lines.Length >= 2; // 至少有表头+1行数据
            if (looksMultiRow) return CsvKind.MultiRowRecords;

            bool looksCurve = (firstLine.Contains("压力P1") || lower.Contains("p1")) &&
                              (firstLine.Contains("流量") || lower.Contains("m1")) &&
                              (firstLine.Contains("导出时间") || firstLine.Contains("时间") || lower.Contains("time"));
            if (looksCurve) return CsvKind.Curve;

            bool looksSummary = firstLine.Contains("泄漏率") || firstLine.Contains("泄露率") ||
                                firstLine.Contains("判定") || firstLine.Contains("试验对象") ||
                                firstLine.Contains("装置编号") || firstLine.Contains("装置编码") ||
                                firstLine.Contains("位号") || firstLine.Contains("对象编码");
            if (looksSummary) return CsvKind.Summary;

            return CsvKind.Unknown;
        }
        catch
        {
            return CsvKind.Unknown;
        }
    }

    /// <summary>
    /// 为曲线文件查找配对的"结果汇总 CSV"。
    /// 同一阀门文件夹下可能有多组试验（多个曲线+多个汇总），按文件名前缀配对：
    /// 去掉文件名里的类型关键词（过程数据/结果汇总等）后，取相同前缀的那一个。
    /// 找不到同前缀的，退回到"目录里唯一的汇总文件"（单组场景）。
    /// </summary>
    private static string? FindSummaryFileInFolder(string curveFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(curveFilePath);
            if (string.IsNullOrEmpty(dir)) return null;

            var summaries = Directory.GetFiles(dir, "*.csv", SearchOption.TopDirectoryOnly)
                .Where(f => !string.Equals(f, curveFilePath, StringComparison.OrdinalIgnoreCase)
                            && SniffCsvKind(f) == CsvKind.Summary)
                .ToList();

            if (summaries.Count == 0) return null;
            if (summaries.Count == 1) return summaries[0];

            // 多组：按前缀配对
            var curvePrefix = StripTypeKeyword(Path.GetFileNameWithoutExtension(curveFilePath));
            var match = summaries.FirstOrDefault(s =>
            {
                var sp = StripTypeKeyword(Path.GetFileNameWithoutExtension(s));
                return !string.IsNullOrEmpty(curvePrefix) &&
                       (sp.Equals(curvePrefix, StringComparison.OrdinalIgnoreCase)
                        || sp.StartsWith(curvePrefix, StringComparison.OrdinalIgnoreCase)
                        || curvePrefix.StartsWith(sp, StringComparison.OrdinalIgnoreCase));
            });
            return match ?? summaries[0];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 批量解析文件夹中的所有数据包（优化版：只查一次数据库）
    /// </summary>
    public async Task<List<ParsedPathInfo>> BatchParseFolderAsync(string folderPath)
    {
        var files = ScanFolderForPackages(folderPath);
        var results = new List<ParsedPathInfo>();

        // ✅ 优化：只查一次数据库，避免 N+1 查询灾难
        using var context = DbContextFactory.CreateDbContext();
        var allProjects = await context.Projects.ToListAsync();
        var allUnits = await context.Units.ToListAsync();
        var allNodes = await context.TestObjectPathNodes.ToListAsync();
        var deviceCodes = await LoadDeviceCodesAsync(context);

        foreach (var file in files)
        {
            var info = await ParsePathInfoAsync(file, folderPath, allProjects, allUnits, allNodes, deviceCodes);
            results.Add(info);
        }

        return results;
    }

    /// <summary>
    /// 从文件路径解析项目、机组、试验对象信息（内部版本：使用预加载的数据）
    /// 路径结构: 根文件夹\项目\机组\[系统\贯穿件\阀门]\数据包文件
    /// </summary>
    private async Task<ParsedPathInfo> ParsePathInfoAsync(
        string filePath,
        string rootFolderPath,
        List<Project> allProjects,
        List<Unit> allUnits,
        List<TestObjectPathNode> allNodes,
        HashSet<string> deviceCodes)
    {
        var result = new ParsedPathInfo
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath)
        };

        try
        {
            // 获取相对路径（去掉根文件夹前缀）
            var relativePath = Path.GetRelativePath(rootFolderPath, filePath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 解析路径层级
            // parts[0] = 项目
            // parts[1] = 机组
            // parts[2...] = 试验对象路径树（系统→贯穿件→阀门），最后一个是文件名
            string? projectFolder = parts.Length >= 1 ? parts[0] : null;
            string? unitFolder = parts.Length >= 2 ? parts[1] : null;
            string[]? objectPathParts = parts.Length >= 3
                ? parts.Skip(2).Take(parts.Length - 3).ToArray()
                : null;

            result.ProjectFolderName = projectFolder;
            result.UnitFolderName = unitFolder;
            result.ObjectPathParts = objectPathParts;

            // ===== 从文件夹名拆出"编码+名称"，供匹配或自动建档使用 =====
            if (!string.IsNullOrWhiteSpace(projectFolder))
            {
                var (pc, pn) = SplitCodeName(projectFolder);
                result.ParsedProjectCode = pc;
                result.ParsedProjectName = pn;
            }
            if (!string.IsNullOrWhiteSpace(unitFolder))
            {
                var (uc, un) = SplitCodeName(unitFolder);
                result.ParsedUnitCode = uc;
                result.ParsedUnitName = un;
            }

            // 对象路径各层：按深度固定类型（第1层系统、第2层贯穿件、第3层及以后阀门）
            if (objectPathParts != null)
            {
                for (int i = 0; i < objectPathParts.Length; i++)
                {
                    var seg = objectPathParts[i];
                    if (string.IsNullOrWhiteSpace(seg)) continue;
                    var (code, name) = SplitCodeName(seg);
                    var nodeType = i == 0
                        ? PathNodeType.System
                        : (i == 1 ? PathNodeType.Penetration : PathNodeType.Valve);
                    result.ObjectLevels.Add(new ParsedNodeLevel { Code = code, Name = name, NodeType = nodeType });
                }
            }

            // ===== 匹配已有台账（匹配成功则复用，否则将自动建档）=====
            // 项目：优先按编码精确匹配
            if (!string.IsNullOrWhiteSpace(result.ParsedProjectCode))
            {
                result.MatchedProject = allProjects.FirstOrDefault(p =>
                    p.Code.Equals(result.ParsedProjectCode, StringComparison.OrdinalIgnoreCase));
            }

            // 机组：按编码精确匹配
            if (!string.IsNullOrWhiteSpace(result.ParsedUnitCode))
            {
                result.MatchedUnit = allUnits.FirstOrDefault(u =>
                    u.Code.Equals(result.ParsedUnitCode, StringComparison.OrdinalIgnoreCase));
            }

            // 试验对象（叶子节点）：按编码精确匹配
            var leafLevel = result.ObjectLevels.LastOrDefault();
            if (leafLevel != null)
            {
                result.MatchedObjectNode = allNodes.FirstOrDefault(n =>
                    n.Code.Equals(leafLevel.Code, StringComparison.OrdinalIgnoreCase));
                if (result.MatchedObjectNode != null)
                    result.IsObjectMatchedExactly = true;
            }

            // ===== 解析数据包内容（曲线 + 结果汇总）=====
            try
            {
                var package = await ParseDataPackageAsync(filePath);
                result.ParsedPackage = package;

                // 设置 CSV 文件类型
                if (Path.GetExtension(filePath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    var csvContent = await ReadTextWithEncodingAsync(filePath);
                    var csvKind = SniffCsvKindFromContent(csvContent);
                    result.CsvFileType = csvKind switch
                    {
                        CsvKind.MultiRowRecords => CsvFileType.MultiRowRecords,
                        CsvKind.Summary => CsvFileType.Summary,
                        CsvKind.Curve => CsvFileType.Curve,
                        _ => CsvFileType.Other
                    };

                    // 多行记录CSV：重新解析为多个数据包（ParsedPackage只存第一个，其余在上传时处理）
                    if (result.CsvFileType == CsvFileType.MultiRowRecords)
                    {
                        var allPackages = ParseMultiRowRecordsCsv(csvContent);
                        if (allPackages.Count > 0)
                        {
                            result.ParsedPackage = allPackages[0]; // 第一个用于路径匹配校验
                            result.MultiRowPackages = allPackages; // 全部数据存储起来供上传时使用
                        }
                    }
                }

                // 数据包/汇总里若带 ObjectCode，用它精确匹配（优先级最高）
                if (!string.IsNullOrWhiteSpace(package.ObjectCode))
                {
                    var exactNode = allNodes.FirstOrDefault(n =>
                        n.Code.Equals(package.ObjectCode, StringComparison.OrdinalIgnoreCase));
                    if (exactNode != null)
                    {
                        result.MatchedObjectNode = exactNode;
                        result.IsObjectMatchedExactly = true;
                    }
                }
            }
            catch
            {
                result.ErrorMessage = "数据包格式解析失败";
            }

            // ===== 校验测量装置是否已在台账登记（外键 FK_TestRecords_MeasurementDevices_DeviceCode）=====
            // 会创建试验记录的类型（汇总/多行/其他）才需要装置；曲线CSV的装置来自配对的汇总文件，此处跳过。
            if (string.IsNullOrEmpty(result.ErrorMessage) && result.CsvFileType != CsvFileType.Curve)
            {
                var deviceError = ValidateDeviceRegistered(result, deviceCodes);
                if (deviceError != null)
                    result.ErrorMessage = deviceError;
            }

            // 自动建档模式：只要文件夹层级完整（项目/机组/至少一层对象）且数据包能解析，
            // 即视为"就绪"——缺失的台账/节点会在上传时自动创建。
            bool structureOk = !string.IsNullOrWhiteSpace(result.ParsedProjectCode)
                               && !string.IsNullOrWhiteSpace(result.ParsedUnitCode)
                               && result.ObjectLevels.Count > 0;

            result.WillCreateNodes = structureOk &&
                (result.MatchedProject == null || result.MatchedUnit == null || result.MatchedObjectNode == null);

            result.IsReady = structureOk && result.ParsedPackage != null
                             && string.IsNullOrEmpty(result.ErrorMessage);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"路径解析失败: {ex.Message}";
            result.IsReady = false;
        }

        return result;
    }

    /// <summary>
    /// 从文件路径解析项目、机组、试验对象信息（公共API）
    /// </summary>
    public async Task<ParsedPathInfo> ParsePathInfoAsync(string filePath, string rootFolderPath)
    {
        // 内部调用优化版，先查数据库
        using var context = DbContextFactory.CreateDbContext();
        var allProjects = await context.Projects.ToListAsync();
        var allUnits = await context.Units.ToListAsync();
        var allNodes = await context.TestObjectPathNodes.ToListAsync();
        var deviceCodes = await LoadDeviceCodesAsync(context);

        return await ParsePathInfoAsync(filePath, rootFolderPath, allProjects, allUnits, allNodes, deviceCodes);
    }

    /// <summary>
    /// 批量上传试验数据（自动建档：缺失的项目/机组/路径节点会按文件夹层级创建）
    /// 处理策略：
    /// - 汇总CSV：创建试验记录（含元数据）
    /// - 曲线CSV：找到同名汇总CSV创建的记录，附加曲线数据
    /// </summary>
    public async Task<BatchUploadResult> BatchUploadAsync(
        List<ParsedPathInfo> items,
        string operatorName,
        IProgress<BatchUploadProgress>? progress = null,
        System.IO.StreamWriter? logWriter = null,
        CancellationToken cancellationToken = default)
    {
        var result = new BatchUploadResult();
        var readyItems = items.Where(i => i.IsReady && !i.IsSkipped).ToList();

        result.TotalCount = readyItems.Count;

        if (logWriter != null)
        {
            await logWriter.WriteLineAsync($"总计 {readyItems.Count} 个文件待上传");
            await logWriter.WriteLineAsync();
        }

        System.Diagnostics.Debug.WriteLine($"[BatchUpload] 开始上传，总计 {readyItems.Count} 个文件");

        // 分离各种CSV类型
        var multiRowItems = readyItems.Where(i => i.CsvFileType == CsvFileType.MultiRowRecords).ToList();
        var summaryItems = readyItems.Where(i => i.CsvFileType == CsvFileType.Summary).ToList();
        var curveItems = readyItems.Where(i => i.CsvFileType == CsvFileType.Curve).ToList();
        var otherItems = readyItems.Where(i => i.CsvFileType == CsvFileType.Other).ToList();

        if (logWriter != null)
        {
            await logWriter.WriteLineAsync($"多行记录CSV: {multiRowItems.Count} 个, 汇总CSV: {summaryItems.Count} 个, 曲线CSV: {curveItems.Count} 个, 其他: {otherItems.Count} 个");
            await logWriter.WriteLineAsync();
        }

        // 记录已创建的试验记录，key = 记录编号，value = TestRecord
        var createdRecords = new Dictionary<string, TestRecord>(StringComparer.OrdinalIgnoreCase);

        // 进度计数器（跨阶段累加）
        int totalProcessed = 0;

        // ===== 阶段0：处理多行记录CSV（每行一条试验记录）=====
        foreach (var (item, index) in multiRowItems.Select((x, i) => (x, i)))
        {
            // 取消检查：文件粒度，正在写入的当前条目完成后停止（保留已导入的部分结果）
            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCancelled = true;
                if (logWriter != null) await logWriter.WriteLineAsync("收到取消请求，停止导入剩余文件");
                return result;
            }
            try
            {
                if (logWriter != null)
                {
                    await logWriter.WriteLineAsync($"[{index + 1}/{multiRowItems.Count}] 处理多行记录文件: {item.FileName}");
                }

                if (item.MultiRowPackages == null || item.MultiRowPackages.Count == 0
                    || item.ObjectLevels.Count == 0
                    || string.IsNullOrWhiteSpace(item.ParsedProjectCode)
                    || string.IsNullOrWhiteSpace(item.ParsedUnitCode))
                {
                    item.ErrorMessage = "路径信息不完整或无有效数据，无法导入";
                    result.FailedCount++;
                    result.FailedItems.Add(item);
                    continue;
                }

                // 1. 确保项目/机组/路径节点链存在
                var leafCode = await EnsurePathExistsAsync(item);

                // 2. 逐行导入每条记录
                int successCount = 0;
                int failCount = 0;
                foreach (var package in item.MultiRowPackages)
                {
                    // 取消检查：行粒度（多行CSV每行一条独立记录，取消时不产生半条数据）
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.WasCancelled = true;
                        if (logWriter != null) await logWriter.WriteLineAsync($"收到取消请求，停止处理 {item.FileName} 的剩余行");
                        return result;
                    }
                    try
                    {
                        // 每条记录都有独立的时间，生成独立的recordCode
                        var recordCode = BuildRecordCode(item.ParsedProjectCode, item.ParsedUnitCode, package.ObjectCode!, leafCode, package.TestTime);

                        // 身份回填
                        if (string.IsNullOrWhiteSpace(package.ObjectCode))
                            package.ObjectCode = leafCode;

                        // 上传入库
                        var testRecord = await ValidateAndUploadAsync(
                            package,
                            recordCode,
                            item.ParsedProjectCode!,
                            item.ParsedUnitCode!,
                            operatorName,
                            item.SelectedRecipeId);

                        result.SuccessCount++;
                        result.UploadedRecords.Add(testRecord);
                        createdRecords[recordCode] = testRecord;
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        if (logWriter != null)
                        {
                            await logWriter.WriteLineAsync($"  ❌ 记录失败: {ex.Message}");
                        }
                    }
                }

                if (logWriter != null)
                {
                    await logWriter.WriteLineAsync($"  ✅ 完成: 成功 {successCount} 条, 失败 {failCount} 条");
                }
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.FailedItems.Add(item);
                item.ErrorMessage = ex.Message;
                if (logWriter != null)
                {
                    await logWriter.WriteLineAsync($"  ❌ 异常: {ex.Message}");
                }
            }
        }

        // ===== 第一阶段：处理汇总CSV和其他类型，创建试验记录 =====
        var mainItems = summaryItems.Concat(otherItems).ToList();
        foreach (var (item, index) in mainItems.Select((x, i) => (x, i)))
        {
            // 取消检查：文件粒度
            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCancelled = true;
                if (logWriter != null) await logWriter.WriteLineAsync("收到取消请求，停止导入剩余文件");
                return result;
            }
            try
            {
                if (logWriter != null)
                {
                    await logWriter.WriteLineAsync($"[{index + 1}/{mainItems.Count}] 处理文件: {item.FileName}");
                }

                if (item.ParsedPackage == null || item.ObjectLevels.Count == 0
                    || string.IsNullOrWhiteSpace(item.ParsedProjectCode)
                    || string.IsNullOrWhiteSpace(item.ParsedUnitCode))
                {
                    item.ErrorMessage = "路径信息不完整，无法导入";
                    result.FailedCount++;
                    result.FailedItems.Add(item);
                    continue;
                }

                // 1. 确保项目/机组/路径节点链存在
                var leafCode = await EnsurePathExistsAsync(item);

                // 2. 生成记录编号
                var recordCode = BuildRecordCode(item.ParsedProjectCode, item.ParsedUnitCode, item.ParsedPackage.ObjectCode, leafCode, item.ParsedPackage.TestTime);

                // 3. 身份回填
                if (string.IsNullOrWhiteSpace(item.ParsedPackage.ObjectCode))
                    item.ParsedPackage.ObjectCode = leafCode;

                // 4. 上传入库
                var testRecord = await ValidateAndUploadAsync(
                    item.ParsedPackage,
                    recordCode,
                    item.ParsedProjectCode!,
                    item.ParsedUnitCode!,
                    operatorName,
                    item.SelectedRecipeId);

                result.SuccessCount++;
                result.UploadedRecords.Add(testRecord);
                createdRecords[recordCode] = testRecord;

                if (logWriter != null)
                {
                    await logWriter.WriteLineAsync($"  ✅ 成功: 记录编号 {testRecord.RecordCode}");
                }

                progress?.Report(new BatchUploadProgress
                {
                    Current = ++totalProcessed,
                    Total = result.TotalCount,
                    CurrentFileName = item.FileName
                });
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.FailedItems.Add(item);
                item.ErrorMessage = ex.Message;
                if (logWriter != null)
                {
                    await logWriter.WriteLineAsync($"  ❌ 异常: {ex.Message}");
                }
            }
        }

        // ===== 第二阶段：处理曲线CSV，附加到对应的汇总记录 =====
        int curveProcessed = 0;
        // 已被曲线挂载的汇总记录：回退匹配时剔除候选——同对象相邻试验（间隔 < 报表时间与首采样
        // 时间的偏移）时，后来的曲线会先按"最近"撞上已挂载记录被 alreadyHasCurve 静默丢弃，
        // 剔除后它转向次近的（正确的）汇总记录，曲线数据不再丢失
        var curveAttachedRecordCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var curveItem in curveItems)
        {
            // 取消检查：文件粒度（此前各阶段已创建的记录保持完整，仅未附加的曲线数据留待下次）
            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCancelled = true;
                if (logWriter != null) await logWriter.WriteLineAsync("收到取消请求，停止导入剩余曲线文件");
                return result;
            }
            curveProcessed++;
            try
            {
                if (logWriter != null)
                {
                    await logWriter.WriteLineAsync($"[{mainItems.Count + curveProcessed}/{readyItems.Count}] 处理曲线文件: {curveItem.FileName}");
                }

                // 生成对应的记录编号（与汇总CSV相同的规则）
                if (curveItem.ParsedPackage == null || curveItem.ObjectLevels.Count == 0
                    || string.IsNullOrWhiteSpace(curveItem.ParsedProjectCode)
                    || string.IsNullOrWhiteSpace(curveItem.ParsedUnitCode))
                {
                    if (logWriter != null)
                    {
                        await logWriter.WriteLineAsync($"  ⏭️ 跳过: 路径信息不完整");
                    }
                    continue;
                }

                var leafCode = curveItem.ObjectLevels.Last().Code;
                var recordCode = BuildRecordCode(curveItem.ParsedProjectCode, curveItem.ParsedUnitCode, curveItem.ParsedPackage.ObjectCode, leafCode, curveItem.ParsedPackage.TestTime);

                // 查找对应的汇总记录。汇总与曲线的 TestTime 来源不同（报表"试验时间" vs
                // 曲线首行采样时间），毫秒位几乎不可能相等——精确匹配必然失配，曲线被
                // "单独导入"兜底生成第二条记录（Result=Unknown、装置未指定），真正的汇总
                // 记录反而没有曲线。失配时按同对象前缀+时间就近（±5分钟）回退匹配。
                if (!createdRecords.TryGetValue(recordCode, out var summaryRecord))
                {
                    summaryRecord = FindClosestSummaryRecord(createdRecords, curveAttachedRecordCodes, recordCode);
                    if (summaryRecord != null)
                    {
                        recordCode = summaryRecord.RecordCode; // 曲线挂到已存在的汇总记录
                        if (logWriter != null)
                            await logWriter.WriteLineAsync($"  🔗 按时间就近匹配到汇总记录: {recordCode}");
                    }
                }

                if (summaryRecord != null)
                {
                    // 找到对应记录，附加曲线数据
                    if (curveItem.ParsedPackage.ProcessDataPoints != null && curveItem.ParsedPackage.ProcessDataPoints.Any())
                    {
                        var processData = BuildProcessData(curveItem.ParsedPackage.ProcessDataPoints);
                        processData.RecordCode = recordCode;  // TestProcessData 与 TestRecord 共享 RecordCode

                        // 用独立的短生命周期上下文写入，不碰共享单例 AppServices.DbContext。
                        // 原因：单例上下文一旦某次 SaveChanges 失败，失败实体会以 Added 状态残留在
                        // 变更跟踪器里，污染后续任意 SaveChanges（表现为莫名的外键/主键冲突）。
                        // 每次附加曲线用完即弃的上下文，失败也随 using 释放，互不牵连。
                        using var curveContext = DbContextFactory.CreateDbContext();

                        // 过程数据与试验记录是一对一（RecordCode 既是外键也是主键）。
                        // 若该记录已存在曲线数据（如汇总CSV已带过程点），重复插入会撞主键，这里先判存在。
                        bool alreadyHasCurve = await curveContext.TestProcessData
                            .AsNoTracking()
                            .AnyAsync(p => p.RecordCode == recordCode);

                        if (alreadyHasCurve)
                        {
                            if (logWriter != null)
                                await logWriter.WriteLineAsync($"  ⏭️ 跳过: 记录 {recordCode} 已有曲线数据");
                        }
                        else
                        {
                            curveContext.TestProcessData.Add(processData);
                            await curveContext.SaveChangesAsync();
                            curveAttachedRecordCodes.Add(recordCode); // 后续曲线回退匹配不再候选该记录

                            if (logWriter != null)
                            {
                                await logWriter.WriteLineAsync($"  ✅ 曲线数据已附加到记录: {recordCode}");
                            }
                        }
                    }
                    else
                    {
                        if (logWriter != null)
                        {
                            await logWriter.WriteLineAsync($"  ⏭️ 跳过: 无过程数据");
                        }
                    }
                }
                else
                {
                    // 没有找到对应的汇总记录，曲线CSV单独导入（用默认值填充缺失字段）
                    if (logWriter != null)
                    {
                        await logWriter.WriteLineAsync($"  ⚠️ 未找到对应汇总记录，尝试单独导入");
                    }

                    // 回填对象编码
                    if (string.IsNullOrWhiteSpace(curveItem.ParsedPackage.ObjectCode))
                        curveItem.ParsedPackage.ObjectCode = leafCode;

                    // 用默认值填充缺失字段
                    if (string.IsNullOrWhiteSpace(curveItem.ParsedPackage.DeviceCode))
                        curveItem.ParsedPackage.DeviceCode = "UNKNOWN";
                    if (string.IsNullOrWhiteSpace(curveItem.ParsedPackage.Result))
                        curveItem.ParsedPackage.Result = "Unknown";

                    var testRecord = await ValidateAndUploadAsync(
                        curveItem.ParsedPackage,
                        recordCode,
                        curveItem.ParsedProjectCode!,
                        curveItem.ParsedUnitCode!,
                        operatorName,
                        curveItem.SelectedRecipeId);

                    result.SuccessCount++;
                    result.UploadedRecords.Add(testRecord);

                    if (logWriter != null)
                    {
                        await logWriter.WriteLineAsync($"  ✅ 单独导入成功: {testRecord.RecordCode}");
                    }
                }

                progress?.Report(new BatchUploadProgress
                {
                    Current = ++totalProcessed,
                    Total = result.TotalCount,
                    CurrentFileName = curveItem.FileName
                });
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.FailedItems.Add(curveItem);
                curveItem.ErrorMessage = ex.Message;
                if (logWriter != null)
                {
                    await logWriter.WriteLineAsync($"  ❌ 异常: {ex.Message}");
                }
            }
        }

        if (logWriter != null)
        {
            await logWriter.WriteLineAsync();
            await logWriter.WriteLineAsync($"上传完成，成功: {result.SuccessCount}, 失败: {result.FailedCount}");
        }
        System.Diagnostics.Debug.WriteLine($"[BatchUpload] 上传完成，成功: {result.SuccessCount}, 失败: {result.FailedCount}");

        return result;
    }

    /// <summary>
    /// 构建试验记录编号：{项目}_{机组}_{对象}_{yyyyMMddHHmmssfff}（对象为空时回退路径叶子节点编码）。
    /// 按TestRecord.RecordCode的50字符上限预算截断对象段——核电阀门位号较长，不截断会触发
    /// SQL 截断异常导致整条记录导入失败；时间戳段保证唯一性不受截断影响。
    /// 公开供实时监视等模块复用，保证全系统 RecordCode 格式一致。
    /// </summary>
    public static string BuildRecordCode(string? projectCode, string? unitCode, string? objectCode, string leafCode, DateTime testTime)
    {
        var obj = string.IsNullOrWhiteSpace(objectCode) ? leafCode : objectCode;
        var prefix = $"{projectCode}_{unitCode}_";
        var suffix = $"_{testTime:yyyyMMddHHmmssfff}";
        var budget = 50 - prefix.Length - suffix.Length;
        if (budget < obj.Length)
            obj = obj[..Math.Max(1, budget)];
        return $"{prefix}{obj}{suffix}";
    }

    /// <summary>
    /// 曲线CSV与汇总CSV的时间来源不同（报表"试验时间" vs 曲线首行采样时间），
    /// 按记录编号精确匹配必然失配。回退匹配：{项目}_{机组}_{对象}_ 前缀相同的前提下，
    /// 取时间戳差绝对值最小且在 ±5 分钟内的汇总记录；已被其他曲线挂载的记录
    /// （excludeRecordCodes）不参与候选，防相邻试验错挂。
    /// </summary>
    private static TestRecord? FindClosestSummaryRecord(
        Dictionary<string, TestRecord> createdRecords,
        HashSet<string> excludeRecordCodes,
        string curveRecordCode)
    {
        var idx = curveRecordCode.LastIndexOf('_');
        if (idx <= 0) return null;
        var prefix = curveRecordCode[..(idx + 1)]; // {项目}_{机组}_{对象}_

        if (!TryParseRecordCodeTimestamp(curveRecordCode[(idx + 1)..], out var curveTime))
            return null;

        TestRecord? best = null;
        TimeSpan bestDiff = TimeSpan.MaxValue;
        foreach (var kv in createdRecords)
        {
            if (excludeRecordCodes.Contains(kv.Key)) continue; // 已有曲线的记录不再候选
            // 与 createdRecords 的 OrdinalIgnoreCase 比较器保持同一口径：精确匹配（TryGetValue）
            // 大小写不敏感，回退匹配若用 Ordinal 会比精确匹配更严，退化成挂不上
            if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var tIdx = kv.Key.LastIndexOf('_');
            if (tIdx != prefix.Length - 1) continue; // 时间戳段位置与曲线侧一致才可比
            if (!TryParseRecordCodeTimestamp(kv.Key[(tIdx + 1)..], out var summaryTime)) continue;

            var diff = (summaryTime - curveTime).Duration();
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = kv.Value;
            }
        }
        return bestDiff <= TimeSpan.FromMinutes(5) ? best : null;
    }

    private static bool TryParseRecordCodeTimestamp(string text, out DateTime time)
        => DateTime.TryParseExact(text, "yyyyMMddHHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out time);

    /// <summary>客户报表以"空"/"NULL"/"/"/"-"等作空单元格占位；按空值返回 null，避免被当作合法编号建库。</summary>
    private static string? NormalizeCsvField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t is "空" or "NULL" or "null" or "Null" or "/" or "-" or "--" ? null : t;
    }

    /// <summary>
    /// 确保某条导入项的项目/机组/路径节点链在数据库中存在，缺失则创建。
    /// 返回叶子（试验对象）节点编码。
    /// 支持并发：捕获唯一约束冲突后重新查询。
    /// </summary>
    private async Task<string> EnsurePathExistsAsync(ParsedPathInfo item)
    {
        using var context = DbContextFactory.CreateDbContext();
        // 事务保护：项目/机组/路径节点链要么全部创建成功，要么全部回滚
        // 避免出现"机组已创建但路径节点未创建"的中间状态
        using var transaction = await context.Database.BeginTransactionAsync();

        try
        {
            var projectCode = item.ParsedProjectCode!;
            var unitCode = item.ParsedUnitCode!;

            // --- 项目（支持并发：捕获唯一约束冲突后重新查询）---
            try
            {
                if (!await context.Projects.AnyAsync(p => p.Code == projectCode))
                {
                    context.Projects.Add(new Project
                    {
                        Code = projectCode,
                        Name = item.ParsedProjectName ?? projectCode,
                        Status = EnabledStatus.Enabled,
                        Remark = "批量导入自动创建",
                    });
                    await context.SaveChangesAsync();
                }
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyError(ex))
            {
                // 重复键冲突后必须清空跟踪器：失败的 Added 实体若残留，后续每次 SaveChanges
                // 都会带着它重插再撞键、被各自 catch 吞掉，机组/节点连锁静默失败，最终 TestRecord 撞外键
                context.ChangeTracker.Clear();
                if (!await context.Projects.AnyAsync(p => p.Code == projectCode))
                    throw; // 冲突并非该编码已存在（如 Name 唯一索引撞名），交由上层按失败处理
                Log.Information("[批量导入] 项目 {Code} 已被其他客户端创建", projectCode);
            }

            // --- 机组 ---
            try
            {
                if (!await context.Units.AnyAsync(u => u.Code == unitCode))
                {
                    context.Units.Add(new Unit
                    {
                        Code = unitCode,
                        Name = item.ParsedUnitName ?? unitCode,
                        ProjectCode = projectCode,
                        Status = EnabledStatus.Enabled,
                        Remark = "批量导入自动创建",
                    });
                    await context.SaveChangesAsync();
                }
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyError(ex))
            {
                // 同上：清跟踪器后重查确认，防止残留 Added 实体连锁撞键
                context.ChangeTracker.Clear();
                if (!await context.Units.AnyAsync(u => u.Code == unitCode))
                    throw;
                Log.Information("[批量导入] 机组 {Code} 已被其他客户端创建", unitCode);
            }

            // --- 路径节点链（系统→贯穿件→阀门）---
            string? parentCode = null;
            foreach (var level in item.ObjectLevels)
            {
                try
                {
                    var existing = await context.TestObjectPathNodes
                        .FirstOrDefaultAsync(n => n.Code == level.Code);

                    if (existing == null)
                    {
                        context.TestObjectPathNodes.Add(new TestObjectPathNode
                        {
                            Code = level.Code,
                            Name = level.Name,
                            NodeType = level.NodeType,
                            UnitCode = unitCode,
                            ParentCode = parentCode,
                            Status = EnabledStatus.Enabled,
                            Remark = "批量导入自动创建",
                        });
                        await context.SaveChangesAsync();
                    }
                }
                catch (DbUpdateException ex) when (IsDuplicateKeyError(ex))
                {
                    // 同上：清跟踪器后重查确认，防止残留 Added 实体连锁撞键
                    context.ChangeTracker.Clear();
                    if (!await context.TestObjectPathNodes.AnyAsync(n => n.Code == level.Code))
                        throw;
                    Log.Information("[批量导入] 路径节点 {Code} 已被其他客户端创建", level.Code);
                }

            parentCode = level.Code;
        }

            await transaction.CommitAsync();
            return item.ObjectLevels.Last().Code;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 按文档导入：读取实验报表格式 CSV（多行记录），返回逐行解析的数据包列表。
    /// 自动处理 UTF-8/GBK 编码；文件不是实验报表格式时抛 FormatException。
    /// </summary>
    public async Task<List<ParsedDataPackage>> ParseMultiRowRecordsCsvFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("文档文件不存在", filePath);
        }

        var csv = await ReadTextWithEncodingAsync(filePath);
        if (SniffCsvKindFromContent(csv) != CsvKind.MultiRowRecords)
        {
            throw new FormatException("所选文件不是实验报表格式（表头需包含：序号、系统、试验阀门编号、实验结果）。");
        }

        var packages = ParseMultiRowRecordsCsv(csv);
        if (packages.Count == 0)
        {
            throw new FormatException("文档中没有可导入的数据行（需要有\"试验阀门编号\"的行）。");
        }

        return packages;
    }

    /// <summary>
    /// 解析实验记录表 xlsx（甲方《实验记录表格式》模板：首行标题、次行表头、数据行含合并单元格）。
    /// 与 CSV 版的差异处理：
    /// - 自动探测表头行（跳过"××机组 试验记录"标题行）
    /// - 合并单元格（如"系统"列跨行合并）值仅在左上角，空值向下继承上一行
    /// - 列名按关键字匹配，容忍单位后缀（"试验压力(KPa)"、"阀门泄漏率设计最大值（Ncm³/h）"）
    /// - 单位换算：限值 Ncm³/h ÷60 → Nml/min（1 Ncm³=1 Nml，与泄漏率判定单位对齐）；
    ///   试验压力 KPa ÷1000 → MPa（库存单位）
    /// - 无表头但值形如 PN217 的列识别为贯穿件编号，拼入阀门显示名（如 3CAM003VA(PN217)）
    /// </summary>
    public Task<List<ParsedDataPackage>> ParseMultiRowRecordsXlsxAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("文档文件不存在", filePath);
        }

        try
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook(filePath);
            var ws = workbook.Worksheets.FirstOrDefault()
                ?? throw new FormatException("Excel 文件中没有工作表");

            // 使用区域边界（表头探测/列映射/数据行遍历共用）
            var usedRange = ws.RangeUsed();
            int lastRowNumber = usedRange?.RangeAddress.LastAddress.RowNumber ?? 0;
            int lastColumnNumber = usedRange?.RangeAddress.LastAddress.ColumnNumber ?? 0;

            // 1) 探测表头行：前 10 行内找同时含"试验阀门"与"试验仪器读数"关键列的行
            int headerRowNumber = 0;
            for (int r = 1; r <= Math.Min(10, lastRowNumber); r++)
            {
                bool hasValve = false, hasReading = false;
                for (int c = 1; c <= lastColumnNumber; c++)
                {
                    var h = ws.Cell(r, c).GetString().Trim();
                    if (h.StartsWith("试验阀门", StringComparison.Ordinal)) hasValve = true;
                    if (h == "试验仪器读数") hasReading = true;
                }
                if (hasValve && hasReading) { headerRowNumber = r; break; }
            }
            if (headerRowNumber == 0)
            {
                throw new FormatException("未找到表头行（需同时包含\"试验阀门\"与\"试验仪器读数\"列）。");
            }

            // 2) 列映射：关键字匹配，容忍单位后缀（全角/半角括号均可）
            int lastColumn = lastColumnNumber;
            int colSystem = -1, colValve = -1, colLimit = -1, colPressure = -1,
                colReading = -1, colDevice = -1, colDate = -1;
            for (int c = 1; c <= lastColumn; c++)
            {
                var h = ws.Cell(headerRowNumber, c).GetString().Trim();
                if (h.Length == 0) continue;
                if (h == "系统") colSystem = c;
                else if (colValve < 0 && h.StartsWith("试验阀门", StringComparison.Ordinal)) colValve = c;
                else if (colLimit < 0 && h.StartsWith("阀门泄漏率设计最大值", StringComparison.Ordinal)) colLimit = c;
                else if (colPressure < 0 && h.StartsWith("试验压力", StringComparison.Ordinal)) colPressure = c;
                else if (h == "试验仪器读数") colReading = c;
                else if (h == "试验仪器编号" || h == "测量装置编号") colDevice = c;
                else if (h == "试验日期" || h == "实验日期") colDate = c;
            }
            if (colValve < 0 || colReading < 0)
            {
                throw new FormatException("表头缺少必需列（需含\"试验阀门\"与\"试验仪器读数\"）。");
            }

            // 3) 贯穿件编号列：表头为空，但数据值形如 PN217（1~4 个字母+数字）。
            //    甲方模板 B 列无表头、值为穿透编号，用值形态识别而非固定列位
            int colPenetration = -1;
            for (int c = 1; c <= lastColumn; c++)
            {
                if (c == colSystem || c == colValve || c == colLimit || c == colPressure
                    || c == colReading || c == colDevice || c == colDate) continue;
                if (!string.IsNullOrWhiteSpace(ws.Cell(headerRowNumber, c).GetString())) continue;

                int hits = 0, samples = 0;
                int sampleLast = Math.Min(headerRowNumber + 5, lastRowNumber);
                for (int r = headerRowNumber + 1; r <= sampleLast; r++)
                {
                    var v = ws.Cell(r, c).GetString().Trim();
                    if (v.Length == 0) continue;
                    samples++;
                    if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z]{1,4}-?\d{1,4}$")) hits++;
                }
                if (samples > 0 && hits == samples) { colPenetration = c; break; }
            }

            // 4) 逐行解析（系统列/贯穿件编号列均为合并单元格，向下填充）
            var results = new List<ParsedDataPackage>();
            int lastRow = lastRowNumber;
            string systemFill = string.Empty;
            string penFill = string.Empty;

            for (int r = headerRowNumber + 1; r <= lastRow; r++)
            {
                var valveCode = GetXlsxString(ws.Cell(r, colValve));
                if (valveCode.Length == 0) continue; // 空行 / 合并尾行无阀门值

                var system = colSystem > 0 ? GetXlsxString(ws.Cell(r, colSystem)) : string.Empty;
                if (system.Length > 0) systemFill = system;
                else system = systemFill; // 合并单元格：值在左上角，续行继承

                var pen = colPenetration > 0 ? GetXlsxString(ws.Cell(r, colPenetration)) : string.Empty;
                if (pen.Length > 0) penFill = pen;
                else pen = penFill; // 贯穿件编号列同样跨行合并，续行继承

                var pkg = new ParsedDataPackage
                {
                    ObjectCode = valveCode,
                    SystemName = system.Length > 0 ? system : null,
                    // 显示名带贯穿件编号后缀（编码本身保持纯净，用于建链与记录编号）
                    ValveDisplayName = pen.Length > 0 ? $"{valveCode}({pen})" : null,
                };

                // 泄漏率设计最大值：表格单位 Ncm³/h，系统判定单位 Nml/min（1 Ncm³ = 1 Nml），÷60 对齐
                if (colLimit > 0 && TryGetXlsxDecimal(ws.Cell(r, colLimit), out var limitRaw) && limitRaw > 0)
                    pkg.LeakageLimit = limitRaw / 60m;

                // 试验压力：表格单位 KPa，库存 MPa，÷1000
                if (colPressure > 0 && TryGetXlsxDecimal(ws.Cell(r, colPressure), out var pressureKpa))
                    pkg.TestPressure = pressureKpa / 1000m;

                if (TryGetXlsxDecimal(ws.Cell(r, colReading), out var reading))
                    pkg.LeakageRate = reading;

                var device = colDevice > 0 ? GetXlsxString(ws.Cell(r, colDevice)) : string.Empty;
                pkg.DeviceCode = device.Length > 0 ? device : "UNKNOWN";

                if (colDate > 0 && TryGetXlsxDateTime(ws.Cell(r, colDate), out var dt))
                    pkg.TestTime = dt;

                results.Add(pkg);
            }

            if (results.Count == 0)
            {
                throw new FormatException("文档中没有可导入的数据行（需要有\"试验阀门\"值的行）。");
            }

            // 5) 从标题行（表头行上方）提取机组名（如"海南3机组"），供导入端自动归属机组
            //    提取不到则不填，导入沿用页面所选项目/机组
            var unitName = ExtractUnitNameFromTitle(ws, headerRowNumber);
            if (!string.IsNullOrWhiteSpace(unitName))
            {
                foreach (var pkg in results)
                    pkg.UnitName = unitName;
            }

            return Task.FromResult(results);
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FormatException($"解析 Excel 文档失败：{ex.Message}");
        }
    }

    private static string GetXlsxString(ClosedXML.Excel.IXLCell cell) => cell.GetString().Trim();

    /// <summary>
    /// 从表头行上方的标题行提取机组名（如"海南3机组  安全壳隔离阀密封性试验记录"→"海南3机组"）。
    /// 匹配"名称+数字+（可选'号'）机组"模式；找不到返回 null。
    /// </summary>
    private static string? ExtractUnitNameFromTitle(ClosedXML.Excel.IXLWorksheet ws, int headerRowNumber)
    {
        for (int r = 1; r < headerRowNumber; r++)
        {
            var text = ws.Cell(r, 1).GetString();
            if (string.IsNullOrWhiteSpace(text) || !text.Contains("机组")) continue;

            var m = System.Text.RegularExpressions.Regex.Match(text, @"([一-龥A-Za-z]{1,8}\d+号?机组)");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    private static bool TryGetXlsxDecimal(ClosedXML.Excel.IXLCell cell, out decimal value)
    {
        if (cell.TryGetValue(out double d))
        {
            value = (decimal)d;
            return true;
        }
        if (decimal.TryParse(cell.GetString().Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>读 xlsx 日期：日期单元格直取；文本尝试常规解析；纯数字按 Excel 日期序列号（1900 纪元）转换</summary>
    private static bool TryGetXlsxDateTime(ClosedXML.Excel.IXLCell cell, out DateTime value)
    {
        if (cell.TryGetValue(out DateTime dt) && dt.Year > 1900)
        {
            value = dt;
            return true;
        }

        var s = cell.GetString().Trim();
        if (ParseCsvDateTime(s) is { } parsed)
        {
            value = parsed;
            return true;
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is > 30000 and < 60000)
        {
            value = DateTime.FromOADate(serial);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 按文档导入：确保"系统→阀门"两级路径节点存在（不创建项目/机组——由页面已选的承担）。
    /// 系统节点按"同机组同名"复用；阀门节点按编码全局复用（存在于其他机组时抛异常）。
    /// 返回阀门节点编码。
    /// </summary>
    public async Task<string> EnsureCsvPathExistsAsync(
        string unitCode,
        string? systemName,
        string valveCode,
        decimal? leakageLimit,
        decimal? testPressure,
        string? valveDisplayName = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        using var transaction = await context.Database.BeginTransactionAsync();

        try
        {
            // --- 系统节点：同机组同名复用 ---
            var systemNameTrimmed = string.IsNullOrWhiteSpace(systemName) ? "未分类系统" : systemName.Trim();
            var systemNode = await context.TestObjectPathNodes.FirstOrDefaultAsync(
                n => n.UnitCode == unitCode && n.NodeType == PathNodeType.System && n.Name == systemNameTrimmed);

            if (systemNode == null)
            {
                var systemCode = SanitizeNodeCode($"{unitCode}-{systemNameTrimmed}");
                try
                {
                    // 编码全局唯一：先确认编码不冲突（同机组同名已排除，防跨机组同名系统撞码）
                    if (!await context.TestObjectPathNodes.AnyAsync(n => n.Code == systemCode))
                    {
                        context.TestObjectPathNodes.Add(new TestObjectPathNode
                        {
                            Code = systemCode,
                            Name = systemNameTrimmed,
                            NodeType = PathNodeType.System,
                            UnitCode = unitCode,
                            ParentCode = null,
                            Status = EnabledStatus.Enabled,
                            Remark = "按文档导入自动创建",
                        });
                        await context.SaveChangesAsync();
                    }
                }
                catch (DbUpdateException ex) when (IsDuplicateKeyError(ex))
                {
                    Log.Information("[按文档导入] 系统节点 {Code} 已被其他客户端创建", systemCode);
                }

                systemNode = await context.TestObjectPathNodes.FirstOrDefaultAsync(n => n.Code == systemCode);
            }

            if (systemNode == null)
            {
                throw new InvalidOperationException($"系统节点创建失败：{systemNameTrimmed}");
            }

            // --- 阀门节点：按编码全局查 ---
            var valveNode = await context.TestObjectPathNodes.FirstOrDefaultAsync(n => n.Code == valveCode);
            if (valveNode != null)
            {
                if (valveNode.UnitCode != unitCode)
                {
                    throw new InvalidOperationException(
                        $"试验阀门编号 {valveCode} 已存在于其他机组（{valveNode.UnitCode}），无法导入到当前机组。");
                }

                // 回填：节点缺限值/试验压力而文档提供了权威值 → 补上（统计页与后续判定有依据）；
                // 节点已有值不覆盖——尊重人工配置
                bool backfilled = false;
                if (valveNode.LeakageLimit == null && leakageLimit.HasValue)
                {
                    valveNode.LeakageLimit = leakageLimit;
                    backfilled = true;
                }
                if (valveNode.TestPressure == null && testPressure.HasValue && testPressure.Value > 0)
                {
                    valveNode.TestPressure = testPressure;
                    backfilled = true;
                }
                if (backfilled)
                {
                    await context.SaveChangesAsync();
                    Log.Information("[按文档导入] 阀门 {Code} 缺少限值/试验压力，已按文档回填（限值={Limit}，压力={Pressure}）",
                        valveCode, valveNode.LeakageLimit, valveNode.TestPressure);
                }

                return valveNode.Code;
            }

            try
            {
                context.TestObjectPathNodes.Add(new TestObjectPathNode
                {
                    Code = valveCode,
                    // xlsx 实验记录表导入时显示名可带贯穿件编号后缀（如 3CAM003VA(PN217)）
                    Name = string.IsNullOrWhiteSpace(valveDisplayName) ? valveCode : valveDisplayName.Trim(),
                    NodeType = PathNodeType.Valve,
                    UnitCode = unitCode,
                    ParentCode = systemNode.Code,
                    LeakageLimit = leakageLimit,
                    TestPressure = testPressure,
                    Status = EnabledStatus.Enabled,
                    Remark = "按文档导入自动创建",
                });
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyError(ex))
            {
                Log.Information("[按文档导入] 阀门节点 {Code} 已被其他客户端创建", valveCode);
            }

            await transaction.CommitAsync();
            return valveCode;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>净化节点编码：去控制字符与首尾空白，超长截断到 100 字符（主键上限）。</summary>
    private static string SanitizeNodeCode(string code)
    {
        var chars = code.Trim().Where(c => !char.IsControl(c)).ToArray();
        var cleaned = new string(chars);
        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    /// <summary>
    /// 预加载测量装置台账中的全部装置编号（忽略大小写），供批量预览快速校验。
    /// </summary>
    private static async Task<HashSet<string>> LoadDeviceCodesAsync(AppDbContext context)
    {
        var codes = await context.MeasurementDevices
            .AsNoTracking()
            .Select(d => d.DeviceCode)
            .ToListAsync();
        return new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 校验导入项引用的测量装置是否已在台账登记。
    /// 只要该项引用了至少一个已登记装置即视为通过（多行CSV中个别坏行由入库阶段逐行兜底）。
    /// 返回 null 表示通过，否则返回给用户的提示文案。
    /// </summary>
    private static string? ValidateDeviceRegistered(ParsedPathInfo item, HashSet<string> deviceCodes)
    {
        // ✅ 装置编号不再强制校验，为空或 UNKNOWN 时由 ValidateRequiredFields 自动填"未指定"
        // 此处始终放行，不再阻断导入。
        return null;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// 解析 JSON 格式的数据包
    /// </summary>
    /// <summary>
    /// 解析 JSON 格式的数据包。
    /// 支持两种格式：
    /// 1. 新格式：ProcessData 数组（每个元素含 Time/Pressure/Flow/Temp 等字段）
    /// 2. 旧格式：PressureCurve/FlowCurve/TempCurve 等独立曲线（每个含 Unit/Data 数组）
    /// </summary>
    private Task<ParsedDataPackage> ParseJsonAsync(string jsonContent)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            };

            var package = JsonSerializer.Deserialize<ParsedDataPackage>(jsonContent, options);

            if (package == null)
            {
                throw new InvalidOperationException("无法解析数据包，内容为空");
            }

            // 如果 ProcessDataPoints 为空，尝试解析旧格式的曲线数据
            if (package.ProcessDataPoints == null || package.ProcessDataPoints.Count == 0)
            {
                package.ProcessDataPoints = ParseLegacyCurveFormat(jsonContent, options);
            }

            return Task.FromResult(package);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"JSON 格式解析失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 解析旧格式的曲线数据（PressureCurve/FlowCurve/TempCurve 等独立曲线）。
    /// 旧格式示例：
    /// {
    ///   "PressureCurve": { "Unit": "MPa", "Data": [0.1, 0.2, ...] },
    ///   "FlowCurve": { "Unit": "Nml/min", "Data": [0.01, 0.02, ...] },
    ///   "TempCurve": { "Unit": "°C", "Data": [25.0, 25.1, ...] }
    /// }
    /// </summary>
    private static List<ProcessDataPoint>? ParseLegacyCurveFormat(string jsonContent, JsonSerializerOptions options)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            // 检查是否有旧格式的曲线字段
            var curveNames = new[] { "PressureCurve", "FlowCurve", "Flow2Curve", "TempCurve", "Pressure2Curve" };
            bool hasLegacyFormat = curveNames.Any(name => root.TryGetProperty(name, out _));

            if (!hasLegacyFormat) return null;

            // 获取各曲线的数据数组，找到最大长度
            var curveData = new Dictionary<string, double[]>();
            int maxLen = 0;

            foreach (var curveName in curveNames)
            {
                if (root.TryGetProperty(curveName, out var curveElem) &&
                    curveElem.TryGetProperty("Data", out var dataElem) &&
                    dataElem.ValueKind == JsonValueKind.Array)
                {
                    var arr = dataElem.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0.0)
                        .ToArray();
                    if (arr.Length > 0)
                    {
                        curveData[curveName] = arr;
                        maxLen = Math.Max(maxLen, arr.Length);
                    }
                }
            }

            if (maxLen == 0) return null;

            // 转换为 ProcessDataPoint 列表
            var points = new List<ProcessDataPoint>(maxLen);
            for (int i = 0; i < maxLen; i++)
            {
                var point = new ProcessDataPoint
                {
                    Time = TimeSpan.FromSeconds(i),
                };

                if (curveData.TryGetValue("PressureCurve", out var pData) && i < pData.Length)
                {
                    point.Pressure = (decimal)pData[i];
                    point.Channels["Pressure"] = pData[i];
                }
                if (curveData.TryGetValue("FlowCurve", out var fData) && i < fData.Length)
                {
                    point.Flow = (decimal)fData[i];
                    point.Channels["Flow"] = fData[i];
                }
                if (curveData.TryGetValue("Flow2Curve", out var f2Data) && i < f2Data.Length)
                {
                    point.Flow2 = (decimal)f2Data[i];
                    point.Channels["Flow2"] = f2Data[i];
                }
                if (curveData.TryGetValue("TempCurve", out var tData) && i < tData.Length)
                {
                    point.Temp = (decimal)tData[i];
                    point.Channels["Temp"] = tData[i];
                }
                if (curveData.TryGetValue("Pressure2Curve", out var p2Data) && i < p2Data.Length)
                {
                    point.Pressure2 = (decimal)p2Data[i];
                    point.Channels["Pressure2"] = p2Data[i];
                }

                points.Add(point);
            }

            return points;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析文本格式的数据包（键值对格式）
    /// 支持格式：
    /// ObjectCode: VALVE-001
    /// DeviceCode: DEV-001
    /// TestTime: 2024-01-15 10:30:00
    /// Result: Pass
    /// LeakageRate: 0.005
    /// TestPressure: 1.5
    /// ProcessData: [{"Time":"00:00:00","Pressure":1.5,"Flow":0.01,"Temp":25.0}, ...]
    /// </summary>
    private Task<ParsedDataPackage> ParseTextAsync(string textContent)
    {
        var package = new ParsedDataPackage();
        var lines = textContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var processDataJson = string.Empty;

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
                continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            switch (key.ToLowerInvariant())
            {
                case "objectcode":
                    package.ObjectCode = value;
                    break;
                case "devicecode":
                    package.DeviceCode = value;
                    break;
                case "testtime":
                    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var testTime))
                        package.TestTime = testTime;
                    break;
                case "result":
                    package.Result = value;
                    break;
                case "leakagerate":
                    if (decimal.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var leakageRate))
                        package.LeakageRate = leakageRate;
                    break;
                case "testpressure":
                    if (decimal.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var testPressure))
                        package.TestPressure = testPressure;
                    break;
                case "processdata":
                    processDataJson = value;
                    break;
            }
        }

        // 解析过程数据点
        if (!string.IsNullOrWhiteSpace(processDataJson))
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                };
                package.ProcessDataPoints = JsonSerializer.Deserialize<List<ProcessDataPoint>>(processDataJson, options);
            }
            catch (JsonException)
            {
                // 如果过程数据解析失败，忽略并继续
                package.ProcessDataPoints = null;
            }
        }

        return Task.FromResult(package);
    }

    // ============================================================
    // 真实装置 CSV 解析
    // ============================================================

    /// <summary>
    /// 已知通道的列名别名映射。key 是内部通道标识，value 是可能出现在 CSV 表头的列名列表。
    /// 解析时优先匹配已知别名；未匹配的列按列名自动识别为自定义通道。
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownChannelAliases = new(StringComparer.Ordinal)
    {
        // 同时支持无空格和有空格的列名（如"实时压力P1"和"实时压力 P1"）
        ["Pressure"]  = ["实时压力P1", "实时压力 P1", "压力P1", "压力 P1", "P1", "pressure"],
        ["Flow"]      = ["瞬时流量M1", "瞬时流量 M1", "流量M1", "流量 M1", "M1", "flow"],
        ["Flow2"]     = ["瞬时流量M2", "瞬时流量 M2", "流量M2", "流量 M2", "M2", "flow2"],
        ["Temp"]      = ["温度T_R", "温度 T_R", "温度T", "温度 T", "温度", "T_R", "temp"],
        ["Pressure2"] = ["压力P2_R", "压力 P2_R", "压力P2", "压力 P2", "P2_R", "P2", "pressure2"],
        // 2026-08 客户数据报表新增列：P1 的阀门开度（未注册此别名时也能按列名自动识别为
        // 自定义通道入库，注册后可获得正式显示名与单位）
        ["ValveOpeningP1"] = ["P1的阀开度", "P1 阀开度", "P1阀开度", "阀开度P1", "valve opening p1"],
    };

    /// <summary>通道标识 → 显示名称（中文）</summary>
    private static readonly Dictionary<string, string> ChannelDisplayNames = new()
    {
        ["Pressure"]  = "压力P1",
        ["Flow"]      = "流量M1",
        ["Flow2"]     = "流量M2",
        ["Temp"]      = "温度T",
        ["Pressure2"] = "压力P2",
        ["ValveOpeningP1"] = "P1阀开度",
    };

    /// <summary>通道标识 → 单位。
    /// 装置 CSV 压力原始值为 MPa，入库时数值 ×1000 并标 kPa（BuildProcessData），
    /// 与实时采集链路入库量纲一致（见 PressureUnitConverter 注释）。</summary>
    private static readonly Dictionary<string, string> ChannelUnits = new()
    {
        ["Pressure"]  = "kPa",
        ["Flow"]      = "Nml/min",
        ["Flow2"]     = "Nml/min",
        ["Temp"]      = "℃",
        ["Pressure2"] = "kPa",
        ["ValveOpeningP1"] = "%",
    };

    /// <summary>
    /// 解析真实装置导出的曲线 CSV（动态通道时序数据）。
    /// 自动检测表头列：已知通道（P1/M1/M2/T/P2）按别名匹配；
    /// 未知列（如湿度、大气压力等）按列名自动识别，无需改代码。
    /// 该文件只含过程曲线，不含试验对象/装置/判定等元数据——
    /// 这些由"结果汇总 CSV"提供（见 ParseResultSummaryCsv），或由文件夹层级提供。
    /// </summary>
    public ParsedDataPackage ParseDeviceCsv(string csvContent)
    {
        var package = new ParsedDataPackage();
        var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            // 没有数据行，返回空包（上层据此判断无过程数据）
            return package;
        }

        // 解析表头
        var header = SplitCsvLine(lines[0]);
        int idxTime = FindColumn(header, "导出时间", "时间", "采集时间", "time");

        // 自动检测所有数据列：每列要么匹配到已知通道，要么作为自定义通道
        // key = 列索引, value = 通道标识
        var columnChannelMap = new Dictionary<int, string>();
        var matchedChannelKeys = new HashSet<string>();  // 防止同一通道被多列匹配

        for (int col = 0; col < header.Length; col++)
        {
            if (col == idxTime) continue;  // 跳过时间列

            var colName = header[col].Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(colName)) continue;

            // 尝试匹配已知通道别名
            string? matchedKey = null;
            foreach (var (key, aliases) in KnownChannelAliases)
            {
                if (matchedChannelKeys.Contains(key)) continue;  // 已被别的列匹配
                foreach (var alias in aliases)
                {
                    if (colName.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
                        colName.Contains(alias, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedKey = key;
                        break;
                    }
                }
                if (matchedKey != null) break;
            }

            if (matchedKey != null)
            {
                columnChannelMap[col] = matchedKey;
                matchedChannelKeys.Add(matchedKey);
            }
            else
            {
                // 未知列：用列名本身作为通道标识
                columnChannelMap[col] = colName;
            }
        }

        var points = new List<ProcessDataPoint>();
        for (int i = 1; i < lines.Length; i++)
        {
            var cols = SplitCsvLine(lines[i]);
            if (cols.Length == 0) continue;

            var point = new ProcessDataPoint
            {
                SampleTime = idxTime >= 0 ? ParseCsvDateTime(GetCol(cols, idxTime)) : null,
            };

            // 填充每个检测到的通道
            foreach (var (colIdx, channelKey) in columnChannelMap)
            {
                double value = (double)ParseCsvDecimal(colIdx < cols.Length ? cols[colIdx] : "");
                point.Channels[channelKey] = value;

                // 同步写入旧字段（向后兼容）
                switch (channelKey)
                {
                    case "Pressure":  point.Pressure = (decimal)value; break;
                    case "Flow":      point.Flow = (decimal)value; break;
                    case "Flow2":     point.Flow2 = (decimal)value; break;
                    case "Temp":      point.Temp = (decimal)value; break;
                    case "Pressure2": point.Pressure2 = (decimal)value; break;
                }
            }

            points.Add(point);
        }

        package.ProcessDataPoints = points.Count > 0 ? points : null;

        // 试验时间：取首个采样点时间（结果汇总文件若有更权威的时间会覆盖它）
        if (points.Count > 0 && points[0].SampleTime.HasValue)
        {
            package.TestTime = points[0].SampleTime!.Value;
        }

        return package;
    }

    /// <summary>
    /// 解析"结果汇总 CSV"，把试验对象/装置/泄漏率/判定等元数据合并进数据包。
    /// 装置已算好最终泄漏率与合格判定，软件只读取、不计算。
    /// 兼容两种布局：
    ///   1) 键值对：每行 "字段名,值"
    ///   2) 表头+值：第一行字段名，第二行对应值
    /// 字段名按别名识别，大小写/空格不敏感。
    /// </summary>
    public ParsedDataPackage ParseResultSummaryCsv(string csvContent, ParsedDataPackage? mergeInto = null)
    {
        var package = mergeInto ?? new ParsedDataPackage();
        var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return package;

        // 收集 "字段名 -> 值" 字典
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 判断是键值对布局还是表头+值布局：
        // 若第一行有 >2 列、且存在第二行，按"表头+值"处理；否则按键值对逐行处理。
        var firstCols = SplitCsvLine(lines[0]);
        if (firstCols.Length > 2 && lines.Length >= 2)
        {
            var valueCols = SplitCsvLine(lines[1]);
            for (int i = 0; i < firstCols.Length; i++)
            {
                var key = firstCols[i].Trim();
                var val = i < valueCols.Length ? valueCols[i].Trim() : string.Empty;
                if (!string.IsNullOrEmpty(key)) map[key] = val;
            }
        }
        else
        {
            foreach (var line in lines)
            {
                var cols = SplitCsvLine(line);
                if (cols.Length >= 2)
                {
                    var key = cols[0].Trim();
                    if (!string.IsNullOrEmpty(key)) map[key] = cols[1].Trim();
                }
            }
        }

        // 按别名提取各字段
        var objectCode = LookupField(map, "试验对象编码", "试验对象", "对象编码", "设备位号", "位号", "objectcode");
        if (!string.IsNullOrWhiteSpace(objectCode)) package.ObjectCode = objectCode;

        var deviceCode = LookupField(map, "测量装置编号", "装置编号", "装置编码", "设备编号", "devicecode");
        if (!string.IsNullOrWhiteSpace(deviceCode)) package.DeviceCode = deviceCode;

        var result = LookupField(map, "判定结果", "试验结果", "结果", "合格判定", "result");
        if (!string.IsNullOrWhiteSpace(result)) package.Result = result;

        var leakage = LookupField(map, "最终泄漏率", "泄漏率", "泄露率", "leakagerate");
        if (decimal.TryParse(leakage, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var lr)) package.LeakageRate = lr;

        var pressure = LookupField(map, "试验压力", "压力", "testpressure");
        if (decimal.TryParse(pressure, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var tp)) package.TestPressure = tp;

        var testTime = LookupField(map, "试验时间", "测试时间", "采集时间", "testtime");
        var parsedTime = ParseCsvDateTime(testTime);
        if (parsedTime.HasValue) package.TestTime = parsedTime.Value;

        return package;
    }

    /// <summary>
    /// 解析"多行试验记录 CSV"（甲方实验报表.CSV格式），每行一条独立的试验记录。
    /// 支持字段：序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,
    ///           预充压压力,试验仪器读数,实验日期,实验结果,测量装置编号
    /// 2026-08 客户调整：列名"预充压压力P2"→"预充压压力"（两种表头均兼容）；
    ///           删除了"试验压力P1/P2"两列（旧文件仍兼容，缺失时试验压力按无值处理）。
    /// </summary>
    public List<ParsedDataPackage> ParseMultiRowRecordsCsv(string csvContent)
    {
        var results = new List<ParsedDataPackage>();
        var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return results; // 至少有表头+1行数据

        // 解析表头，建立列索引映射
        var headers = SplitCsvLine(lines[0]);
        var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            var header = headers[i].Trim();
            if (!string.IsNullOrEmpty(header))
            {
                colMap[header] = i;
            }
        }

        // 逐行解析数据（跳过表头）
        for (int i = 1; i < lines.Length; i++)
        {
            var cols = SplitCsvLine(lines[i]);
            if (cols.Length == 0) continue;

            var package = new ParsedDataPackage();

            // 试验阀门编号 → ObjectCode（优先）。客户报表用"空"/"NULL"等作空单元格占位，
            // 必须按空值处理——否则会真实创建编码为"空"的阀门节点挂在名为"空"的系统下
            var objectCode = NormalizeCsvField(GetFieldValue(cols, colMap, "试验阀门编号", "阀门编号", "阀门"));
            if (objectCode != null)
                package.ObjectCode = objectCode;

            // 试验仪器读数 → 最终泄漏率
            var leakage = GetFieldValue(cols, colMap, "试验仪器读数", "泄漏率", "泄露率");
            if (decimal.TryParse(leakage, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var lr))
                package.LeakageRate = lr;

            // 试验压力P1 → 试验压力（不要用"阀门泄漏率设计最大值"，那是限值不是压力）
            var pressureStr = GetFieldValue(cols, colMap, "试验压力P1", "试验压力", "试验压力P2");
            if (decimal.TryParse(pressureStr, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var tp))
                package.TestPressure = tp;

            // 实验日期 → 试验时间
            var testTime = GetFieldValue(cols, colMap, "实验日期", "试验日期", "测试时间", "时间");
            var parsedTime = ParseCsvDateTime(testTime);
            if (parsedTime.HasValue)
                package.TestTime = parsedTime.Value;

            // 实验结果 → Result
            var result = GetFieldValue(cols, colMap, "实验结果", "试验结果", "结果", "合格");
            if (!string.IsNullOrWhiteSpace(result))
                package.Result = result.Trim();

            // 测量装置编号 → DeviceCode（实验报表新增列）；缺失或"空"/NULL 占位时用 UNKNOWN 占位。
            // 占位值若不按空处理，会经 CheckDeviceExistsAsync 真实创建名为"空"的测量装置台账记录；
            // UNKNOWN 在校验层自动转"未指定"，文档导入路径则由装置选择器拦截补齐
            var deviceCode = NormalizeCsvField(GetFieldValue(cols, colMap, "测量装置编号", "装置编号", "装置编码", "设备编号"));
            package.DeviceCode = deviceCode ?? "UNKNOWN";

            // 阀门泄漏率设计最大值 → 泄漏限值（客户实验报表给定的判定限值，优先于系统预设）
            var designMax = GetFieldValue(cols, colMap, "阀门泄漏率设计最大值", "泄漏率设计最大值", "设计最大值");
            if (decimal.TryParse(designMax, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var dm))
                package.LeakageLimit = dm;

            // 系统列：按文档导入时用于自动创建路径节点；"空"占位同样按空值处理
            var system = NormalizeCsvField(GetFieldValue(cols, colMap, "系统"));
            if (system != null)
                package.SystemName = system;

            // 预充压压力（新表头优先，兼容旧表头"预充压压力P2"）→ 记录备注（见 ValidateAndUploadAsync）
            var prechargeP2 = GetFieldValue(cols, colMap, "预充压压力", "预充压压力P2", "预充压P2");
            if (decimal.TryParse(prechargeP2, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var pc) && pc > 0)
                package.PrechargePressureP2 = pc;

            // 只有当ObjectCode有效时才添加
            if (!string.IsNullOrWhiteSpace(package.ObjectCode))
            {
                results.Add(package);
            }
        }

        return results;
    }

    /// <summary>
    /// 从CSV行中按候选列名获取字段值（辅助方法）
    /// </summary>
    private static string GetFieldValue(string[] cols, Dictionary<string, int> colMap, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (colMap.TryGetValue(candidate, out var index) && index < cols.Length)
            {
                return cols[index].Trim();
            }
        }
        return string.Empty;
    }

    #region CSV 解析辅助方法

    /// <summary>
    /// 切分一行 CSV，去除字段两端引号。
    /// 同时支持英文逗号 `,` 和中文逗号 `，`（真实装置CSV可能混用）。
    /// </summary>
    private static string[] SplitCsvLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return [];
        // 先将中文逗号替换为英文逗号，再按英文逗号分割
        return line.Replace('，', ',').Split(',')
            .Select(c => c.Trim().Trim('"').Trim())
            .ToArray();
    }

    /// <summary>按一组候选列名查找列索引，找不到返回 -1。</summary>
    private static int FindColumn(string[] header, params string[] candidates)
    {
        for (int i = 0; i < header.Length; i++)
        {
            var col = header[i].Trim().Trim('"').Trim();
            foreach (var c in candidates)
            {
                if (col.Equals(c, StringComparison.OrdinalIgnoreCase) ||
                    col.Contains(c, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        return -1;
    }

    private static string GetCol(string[] cols, int idx)
        => idx >= 0 && idx < cols.Length ? cols[idx] : string.Empty;

    /// <summary>按一组候选字段名在字典中查找值。</summary>
    private static string LookupField(Dictionary<string, string> map, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (map.TryGetValue(c, out var v)) return v;
        }
        // 容错：包含匹配
        foreach (var kv in map)
        {
            foreach (var c in candidates)
            {
                if (kv.Key.Contains(c, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
        }
        return string.Empty;
    }

    private static decimal ParseCsvDecimal(string value)
        => decimal.TryParse(value?.Trim().Trim('"'), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static DateTime? ParseCsvDateTime(string value)
        => DateTime.TryParse(value?.Trim().Trim('"'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : null;

    /// <summary>
    /// 去掉文件名里的类型关键词，得到用于配对的"前缀"。
    /// 例： "2SIS101VP_过程数据" / "2SIS101VP_结果汇总" → "2SIS101VP"
    ///      "2SIS101VP_20260620_过程数据" / "2SIS101VP_20260620_结果汇总" → "2SIS101VP_20260620"
    /// </summary>
    private static string StripTypeKeyword(string fileNameNoExt)
    {
        if (string.IsNullOrEmpty(fileNameNoExt)) return string.Empty;
        var s = fileNameNoExt;
        foreach (var kw in new[] { "过程数据", "结果汇总", "曲线", "汇总", "结果", "process", "summary", "result", "curve" })
        {
            int idx = s.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) s = s.Substring(0, idx);
        }
        return s.TrimEnd('_', '-', ' ', '.');
    }

    /// <summary>
    /// 拆分文件夹名为"编码 + 名称"。
    /// 规则：开头连续的 ASCII 字母/数字/连字符/下划线视为编码，其后（通常是中文）视为名称。
    /// 例： "RHR余热排出系统" → ("RHR","余热排出系统")；"1RHR040VP隔离阀" → ("1RHR040VP","隔离阀")。
    /// 纯中文（如 "海南项目"）无前导编码时，编码与名称都取整串（保证编码非空且唯一）。
    /// </summary>
    public static (string Code, string Name) SplitCodeName(string folderName)
    {
        var s = (folderName ?? string.Empty).Trim();
        if (s.Length == 0) return (string.Empty, string.Empty);

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            bool isCodeChar = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                              || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!isCodeChar) break;
            i++;
        }

        if (i == 0)
        {
            // 无前导编码（纯中文等）：整串既作编码也作名称
            return (s, s);
        }
        if (i == s.Length)
        {
            // 全是编码字符，无名称部分
            return (s, s);
        }

        var code = s.Substring(0, i);
        var name = s.Substring(i).Trim();
        return (code, string.IsNullOrEmpty(name) ? code : name);
    }

    #endregion

    /// <summary>
    /// 校验必填字段
    /// </summary>
    private static void ValidateRequiredFields(
        ParsedDataPackage parsedData,
        string recordCode,
        string projectCode,
        string unitCode,
        string operatorName)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(recordCode))
            errors.Add("记录编号不能为空");

        if (string.IsNullOrWhiteSpace(projectCode))
            errors.Add("项目编码不能为空");

        if (string.IsNullOrWhiteSpace(unitCode))
            errors.Add("机组编码不能为空");

        if (string.IsNullOrWhiteSpace(operatorName))
            errors.Add("操作员不能为空");

        if (string.IsNullOrWhiteSpace(parsedData.ObjectCode))
            errors.Add("试验对象编码不能为空");

        // ✅ 装置编号不再强制必填，为空或 UNKNOWN 时自动填"未指定"
        if (string.IsNullOrWhiteSpace(parsedData.DeviceCode) ||
            string.Equals(parsedData.DeviceCode, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            parsedData.DeviceCode = "未指定";

        if (parsedData.TestTime == default)
            errors.Add("试验时间无效");

        if (string.IsNullOrWhiteSpace(parsedData.Result))
            errors.Add("试验结果不能为空");

        if (errors.Any())
        {
            throw new ArgumentException($"数据校验失败: {string.Join("; ", errors)}");
        }
    }

    /// <summary>
    /// 检查重复记录
    /// </summary>
    private async Task CheckDuplicateAsync(string objectCode, DateTime testTime)
    {
        // 查询相同对象在相同时间的记录（允许5秒误差）
        var startTime = testTime.AddSeconds(-5);
        var endTime = testTime.AddSeconds(5);

        var existingRecords = await _testRecordService.GetByObjectAsync(objectCode, 1000);
        var duplicate = existingRecords.FirstOrDefault(r =>
            r.TestTime >= startTime && r.TestTime <= endTime);

        if (duplicate != null)
        {
            throw new InvalidOperationException(
                $"检测到重复记录：对象 {objectCode} 在 {testTime:yyyy-MM-dd HH:mm:ss} 附近已存在试验记录（记录编号: {duplicate.RecordCode}）");
        }
    }

    /// <summary>
    /// 检查测量装置是否存在于数据库中，不存在则自动创建。
    /// 支持并发：捕获唯一约束冲突后重新查询。
    /// </summary>
    private async Task CheckDeviceExistsAsync(string deviceCode)
    {
        // ✅ "未指定" 是系统默认装置，直接放行
        if (string.Equals(deviceCode, "未指定", StringComparison.OrdinalIgnoreCase))
            return;

        // 安全兜底：空值或 UNKNOWN 不应到达这里（ValidateRequiredFields 已处理），但以防万一
        if (string.IsNullOrWhiteSpace(deviceCode) ||
            string.Equals(deviceCode, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return;

        using var context = DbContextFactory.CreateDbContext();

        try
        {
            if (!await context.MeasurementDevices.AnyAsync(d => d.DeviceCode == deviceCode))
            {
                context.MeasurementDevices.Add(new MeasurementDevice
                {
                    DeviceCode = deviceCode,
                    DeviceName = deviceCode,
                    EnabledStatus = EnabledStatus.Enabled,
                    Remark = "导入时自动创建",
                    CreatedAt = DateTime.Now,
                });
                await context.SaveChangesAsync();
            }
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyError(ex))
        {
            Log.Information("[批量导入] 装置 {Code} 已被其他客户端创建", deviceCode);
        }
    }

    /// <summary>
    /// 将字符串结果映射为 TestResult 枚举
    /// </summary>
    private static TestResult MapTestResult(string result)
    {
        return result.ToLowerInvariant() switch
        {
            "pass" or "合格" or "1" => TestResult.Pass,
            "fail" or "不合格" or "2" => TestResult.Fail,
            _ => TestResult.Unknown,
        };
    }

    /// <summary>
    /// 构建过程数据对象（动态通道 + 真实时间轴）。
    /// 同时写入 ChannelsJson（新格式）和旧列（向后兼容）。
    /// </summary>
    private static TestProcessData BuildProcessData(List<ProcessDataPoint> dataPoints)
    {
        if (dataPoints == null || !dataPoints.Any())
        {
            throw new ArgumentException("过程数据不能为空", nameof(dataPoints));
        }

        // 构建时间轴：优先用绝对采集时间换算为相对首点的秒数偏移；
        // 无绝对时间时退回到 TimeSpan.Time；都没有则用采样索引（保持兼容）。
        // 【关键】同一秒内可能有多条记录（高频采样），需要等分时间段分配时间偏移。
        // 例：15:29:41有5条记录，则等分1-2秒：1.0, 1.2, 1.4, 1.6, 1.8
        double[] timeAxis;
        var firstSample = dataPoints[0].SampleTime;
        if (dataPoints.All(p => p.SampleTime.HasValue) && firstSample.HasValue)
        {
            var baseTime = firstSample.Value;
            var timeList = new List<double>();

            // 先统计每个时间戳有多少条记录
            var timeGroups = new List<(DateTime Time, int Count)>();
            DateTime? currentTime = null;
            int currentCount = 0;

            foreach (var p in dataPoints)
            {
                if (currentTime == null || p.SampleTime!.Value != currentTime.Value)
                {
                    if (currentTime != null)
                        timeGroups.Add((currentTime.Value, currentCount));
                    currentTime = p.SampleTime!.Value;
                    currentCount = 1;
                }
                else
                {
                    currentCount++;
                }
            }
            if (currentTime != null)
                timeGroups.Add((currentTime.Value, currentCount));

            // 为每条记录分配等分的时间偏移
            int groupIndex = 0;
            int indexInGroup = 0;

            foreach (var p in dataPoints)
            {
                var (groupTime, groupCount) = timeGroups[groupIndex];
                double baseSeconds = (groupTime - baseTime).TotalSeconds;

                // 等分时间段：如果这一秒有N条记录，则间隔为 1/N 秒
                double offset = groupCount > 1 ? (double)indexInGroup / groupCount : 0;
                timeList.Add(baseSeconds + offset);

                indexInGroup++;
                if (indexInGroup >= groupCount)
                {
                    groupIndex++;
                    indexInGroup = 0;
                }
            }

            timeAxis = timeList.ToArray();
        }
        else if (dataPoints.Any(p => p.Time != TimeSpan.Zero))
        {
            timeAxis = dataPoints.Select(p => p.Time.TotalSeconds).ToArray();
        }
        else
        {
            timeAxis = dataPoints.Select((_, i) => (double)i).ToArray();
        }

        // ====== 动态通道：从所有数据点收集通道 key ======
        var allKeys = new List<string>();
        var seenKeys = new HashSet<string>();
        foreach (var p in dataPoints)
        {
            foreach (var key in p.Channels.Keys)
            {
                if (seenKeys.Add(key))
                    allKeys.Add(key);
            }
        }

        // 如果 Channels 字典为空（来自旧的 JSON/文本格式），从旧字段回填
        if (allKeys.Count == 0)
        {
            allKeys.AddRange(["Pressure", "Flow", "Flow2", "Temp", "Pressure2"]);
            foreach (var p in dataPoints)
            {
                p.Channels["Pressure"] = (double)p.Pressure;
                p.Channels["Flow"] = (double)p.Flow;
                p.Channels["Flow2"] = (double)p.Flow2;
                p.Channels["Temp"] = (double)p.Temp;
                p.Channels["Pressure2"] = (double)p.Pressure2;
            }
        }

        // 构建 ChannelData 字典
        var channelsDict = new Dictionary<string, ChannelData>();
        foreach (var key in allKeys)
        {
            var values = dataPoints.Select(p => p.Channels.GetValueOrDefault(key, 0.0)).ToArray();
            // 已知压力通道数值 ×1000（MPa→kPa），与实时链路入库量纲一致；
            // 仅限已知压力通道——自定义列单位未知，不做隐式换算
            if (key is "Pressure" or "Pressure2")
                values = Helpers.PressureUnitConverter.ScaleToUnit(values, Helpers.PressureUnitConverter.DisplayUnit);
            channelsDict[key] = new ChannelData
            {
                Name = ChannelDisplayNames.GetValueOrDefault(key, key),
                Unit = ChannelUnits.GetValueOrDefault(key, ""),
                Data = values,
                Min = values.Length > 0 ? values.Min() : 0,
                Max = values.Length > 0 ? values.Max() : 0,
            };
        }

        // 提取旧字段（向后兼容写入）
        var pressures = channelsDict.TryGetValue("Pressure", out var chP) ? chP.Data : [];
        var flows = channelsDict.TryGetValue("Flow", out var chF) ? chF.Data : [];
        var flow2s = channelsDict.TryGetValue("Flow2", out var chF2) ? chF2.Data : [];
        var temps = channelsDict.TryGetValue("Temp", out var chT) ? chT.Data : [];
        var pressure2s = channelsDict.TryGetValue("Pressure2", out var chP2) ? chP2.Data : [];

        var processData = new TestProcessData
        {
            // 新格式：动态通道 JSON
            ChannelsJson = JsonSerializer.Serialize(channelsDict),
            TimeAxisJson = JsonSerializer.Serialize(timeAxis),

            // 旧格式（向后兼容）
            PressureCurveJson = pressures.Length > 0 ? JsonSerializer.Serialize(pressures) : null,
            FlowCurveJson = flows.Length > 0 ? JsonSerializer.Serialize(flows) : null,
            Flow2CurveJson = flow2s.Length > 0 ? JsonSerializer.Serialize(flow2s) : null,
            TempCurveJson = temps.Length > 0 ? JsonSerializer.Serialize(temps) : null,
            Pressure2CurveJson = pressure2s.Length > 0 ? JsonSerializer.Serialize(pressure2s) : null,
            PressureMin = pressures.Length > 0 ? (decimal)pressures.Min() : 0,
            PressureMax = pressures.Length > 0 ? (decimal)pressures.Max() : 0,
            FlowMin = flows.Length > 0 ? (decimal)flows.Min() : 0,
            FlowMax = flows.Length > 0 ? (decimal)flows.Max() : 0,
            Flow2Min = flow2s.Length > 0 ? (decimal)flow2s.Min() : 0,
            Flow2Max = flow2s.Length > 0 ? (decimal)flow2s.Max() : 0,
            TempMin = temps.Length > 0 ? (decimal)temps.Min() : 0,
            TempMax = temps.Length > 0 ? (decimal)temps.Max() : 0,
            Pressure2Min = pressure2s.Length > 0 ? (decimal)pressure2s.Min() : 0,
            Pressure2Max = pressure2s.Length > 0 ? (decimal)pressure2s.Max() : 0,
            CreatedAt = DateTime.Now,
        };

        return processData;
    }

    #endregion
}

/// <summary>
/// 路径解析结果（批量上传用）
/// </summary>
public sealed class ParsedPathInfo
{
    /// <summary>
    /// 文件完整路径
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// 文件名
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 项目文件夹名（路径第一层）
    /// </summary>
    public string? ProjectFolderName { get; set; }

    /// <summary>
    /// 机组文件夹名（路径第二层）
    /// </summary>
    public string? UnitFolderName { get; set; }

    /// <summary>
    /// 试验对象路径部分（路径第三层及以后）
    /// </summary>
    public string[]? ObjectPathParts { get; set; }

    /// <summary>
    /// 匹配到的项目
    /// </summary>
    public Project? MatchedProject { get; set; }

    /// <summary>
    /// 匹配到的机组
    /// </summary>
    public Unit? MatchedUnit { get; set; }

    /// <summary>
    /// 匹配到的试验对象节点
    /// </summary>
    public TestObjectPathNode? MatchedObjectNode { get; set; }

    /// <summary>
    /// 试验对象是否从数据包内容精确匹配到
    /// </summary>
    public bool IsObjectMatchedExactly { get; set; }

    /// <summary>
    /// 解析出的数据包内容
    /// </summary>
    public ParsedDataPackage? ParsedPackage { get; set; }

    /// <summary>
    /// 用户选择的配方ID（覆盖默认）
    /// </summary>
    public int? SelectedRecipeId { get; set; }

    /// <summary>
    /// 是否已准备好可以上传
    /// </summary>
    public bool IsReady { get; set; }

    /// <summary>
    /// 是否跳过（用户手动标记）
    /// </summary>
    public bool IsSkipped { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// CSV 文件类型（曲线/汇总/多行记录/其他），用于批量上传时区分处理
    /// </summary>
    public CsvFileType CsvFileType { get; set; } = CsvFileType.Other;

    /// <summary>
    /// 多行记录CSV解析出的所有数据包（每行一条记录）
    /// MultiRowRecords 类型时此属性有值，ParsedPackage 只存第一条用于预校验
    /// </summary>
    public List<ParsedDataPackage>? MultiRowPackages { get; set; }

    /// <summary>
    /// 配对的文件路径（曲线CSV配对汇总CSV，或反之）
    /// </summary>
    public string? PairedFilePath { get; set; }

    // ===== 自动建档：从文件夹名拆出的编码/名称（用于不存在时创建台账与路径节点）=====

    /// <summary>项目编码（从项目文件夹名拆分）</summary>
    public string? ParsedProjectCode { get; set; }
    /// <summary>项目名称</summary>
    public string? ParsedProjectName { get; set; }
    /// <summary>机组编码</summary>
    public string? ParsedUnitCode { get; set; }
    /// <summary>机组名称</summary>
    public string? ParsedUnitName { get; set; }

    /// <summary>
    /// 试验对象路径各层（系统→贯穿件→阀门），按文件夹顺序，含编码/名称/节点类型。
    /// </summary>
    public List<ParsedNodeLevel> ObjectLevels { get; set; } = [];

    /// <summary>本条是否将新建台账/节点（用于 UI 提示"将新建"）</summary>
    public bool WillCreateNodes { get; set; }
}

/// <summary>
/// 试验对象路径中的一层（自动建档用）
/// </summary>
public sealed class ParsedNodeLevel
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PathNodeType NodeType { get; set; }
}

/// <summary>
/// 批量上传进度
/// </summary>
public sealed class BatchUploadProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string? CurrentFileName { get; set; }
}

/// <summary>
/// 批量上传结果
/// </summary>
public sealed class BatchUploadResult
{
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>用户中途取消（已导入的部分结果保留在计数与列表中）</summary>
    public bool WasCancelled { get; set; }
    public List<TestRecord> UploadedRecords { get; set; } = [];
    public List<ParsedPathInfo> FailedItems { get; set; } = [];
}

/// <summary>
/// 解析后的数据包对象
/// </summary>
public sealed class ParsedDataPackage
{
    /// <summary>
    /// 试验对象编码
    /// </summary>
    public string? ObjectCode { get; set; }

    /// <summary>
    /// 系统名称（实验报表.CSV 的"系统"列，按文档导入时用于自动创建路径节点）
    /// </summary>
    public string? SystemName { get; set; }

    /// <summary>
    /// 测量装置编码
    /// </summary>
    public string? DeviceCode { get; set; }

    /// <summary>
    /// 试验时间
    /// </summary>
    public DateTime TestTime { get; set; }

    /// <summary>
    /// 试验结果（字符串形式：Pass/Fail/合格/不合格）
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// 泄漏率
    /// </summary>
    public decimal LeakageRate { get; set; }

    /// <summary>
    /// 试验压力
    /// </summary>
    public decimal TestPressure { get; set; }

    /// <summary>
    /// 泄漏率限值（来自导入文件，如实验报表的"阀门泄漏率设计最大值"）。
    /// 有值时优先于系统预设（试验对象节点/配方）的限值。
    /// </summary>
    public decimal? LeakageLimit { get; set; }

    /// <summary>
    /// 预充压压力 P2（MPa，实验报表"预充压压力"列，2026-08 前表头为"预充压压力P2"）。
    /// 记录表无独立字段，入库时写入试验记录备注供追溯。
    /// </summary>
    public decimal? PrechargePressureP2 { get; set; }

    /// <summary>
    /// 阀门节点的显示名（xlsx 实验记录表导入时带贯穿件编号后缀，如 "3CAM003VA(PN217)"；
    /// null=沿用阀门编码作为名称）。
    /// </summary>
    public string? ValveDisplayName { get; set; }

    /// <summary>
    /// 文档归属机组名（xlsx 实验记录表从标题行提取，如"海南3机组"）。
    /// 非空时导入按文档归属机组入库（自动匹配现有机组，无则新建），避免页面所选机组与文档不符造成错挂。
    /// </summary>
    public string? UnitName { get; set; }

    /// <summary>
    /// 过程数据点列表
    /// </summary>
    public List<ProcessDataPoint>? ProcessDataPoints { get; set; }
}

/// <summary>
/// 过程数据点（对应真实装置导出 CSV 的一行）。
/// 支持动态通道：Channels 字典存放任意数量的通道数据；
/// 同时保留 Pressure/Flow/Flow2/Temp/Pressure2 旧字段，兼容旧的 JSON/文本格式解析。
/// </summary>
public sealed class ProcessDataPoint
{
    /// <summary>
    /// 采集时间（绝对时间戳，来自 CSV 的"导出时间"列）。
    /// 入库时换算为相对首点的秒数偏移存入 TimeAxisJson。
    /// </summary>
    public DateTime? SampleTime { get; set; }

    /// <summary>
    /// 时间点（相对偏移，兼容旧的 JSON/文本格式；CSV 格式用 SampleTime）
    /// </summary>
    public TimeSpan Time { get; set; }

    /// <summary>
    /// 实时压力 P1
    /// </summary>
    public decimal Pressure { get; set; }

    /// <summary>
    /// 瞬时流量 M1
    /// </summary>
    public decimal Flow { get; set; }

    /// <summary>
    /// 瞬时流量 M2
    /// </summary>
    public decimal Flow2 { get; set; }

    /// <summary>
    /// 温度 T
    /// </summary>
    public decimal Temp { get; set; }

    /// <summary>
    /// 压力 P2
    /// </summary>
    public decimal Pressure2 { get; set; }

    /// <summary>
    /// 动态通道：key 是通道标识（Pressure / Flow / Humidity / 自定义名），value 是数值。
    /// CSV 解析时自动检测所有列并填入；旧的 JSON/文本格式也同步填入已知通道。
    /// </summary>
    public Dictionary<string, double> Channels { get; } = new();
}

/// <summary>
/// CSV 文件类型（批量上传用）
/// </summary>
public enum CsvFileType
{
    /// <summary>其他类型（JSON/TXT/未知CSV）</summary>
    Other,
    /// <summary>曲线数据CSV（含时序的压力/流量/温度等通道数据）</summary>
    Curve,
    /// <summary>结果汇总CSV（含对象/装置/泄漏率/判定等元数据）</summary>
    Summary,
    /// <summary>多行试验记录CSV（每行一条独立试验记录）</summary>
    MultiRowRecords,
}

