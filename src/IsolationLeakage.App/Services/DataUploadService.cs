using System.IO;
using System.Text.Json;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

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
    /// 读取文本文件，自动处理 UTF-8 / GBK 编码（真实装置 CSV 多为 GBK）。
    /// </summary>
    private static async Task<string> ReadTextWithEncodingAsync(string filePath)
    {
        var bytes = await File.ReadAllBytesAsync(filePath);
        return DecodeBytes(bytes);
    }

    /// <summary>同步版本（供文件嗅探使用）。</summary>
    private static string ReadTextWithEncoding(string filePath)
    {
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
        try
        {
            var node = await AppServices.DbContext.TestObjectPathNodes
                .AsNoTracking()
                .Include(n => n.DefaultRecipe)
                .FirstOrDefaultAsync(n => n.Code == parsedData.ObjectCode);
            if (node?.LeakageLimit.HasValue == true)
                leakageLimit = node.LeakageLimit.Value;

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

                // 优先使用配方的预期泄漏流量作为判定标准
                var recipe = await AppServices.DbContext.TestRecipes.FindAsync(actualRecipeId.Value);
                if (recipe != null && recipe.NormalExpectedLeakFlow > 0)
                {
                    leakageLimit = recipe.NormalExpectedLeakFlow;
                }
            }
        }
        catch { /* 查询失败时使用默认值 */ }

        var testRecord = new TestRecord
        {
            RecordCode = recordCode,
            ProjectCode = projectCode,
            UnitCode = unitCode,
            ObjectCode = parsedData.ObjectCode!,
            DeviceCode = parsedData.DeviceCode!,
            TestTime = parsedData.TestTime,
            ImportTime = DateTime.Now,
            Operator = operatorName,
            TestPressure = parsedData.TestPressure,
            LeakageLimit = leakageLimit,
            FinalLeakageRate = parsedData.LeakageRate,
            Result = MapTestResult(parsedData.Result ?? "Unknown"),
            Remark = null,
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
    private enum CsvKind { Curve, Summary, Unknown }

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
            var firstLine = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var lower = firstLine.ToLowerInvariant();

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

        foreach (var file in files)
        {
            var info = await ParsePathInfoAsync(file, folderPath, allProjects, allUnits, allNodes);
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
        List<TestObjectPathNode> allNodes)
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
                    result.CsvFileType = csvKind == CsvKind.Summary ? CsvFileType.Summary : CsvFileType.Curve;
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

        return await ParsePathInfoAsync(filePath, rootFolderPath, allProjects, allUnits, allNodes);
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
        System.IO.StreamWriter? logWriter = null)
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

        // 分离汇总CSV和曲线CSV
        var summaryItems = readyItems.Where(i => i.CsvFileType == CsvFileType.Summary).ToList();
        var curveItems = readyItems.Where(i => i.CsvFileType == CsvFileType.Curve).ToList();
        var otherItems = readyItems.Where(i => i.CsvFileType == CsvFileType.Other).ToList();

        if (logWriter != null)
        {
            await logWriter.WriteLineAsync($"汇总CSV: {summaryItems.Count} 个, 曲线CSV: {curveItems.Count} 个, 其他: {otherItems.Count} 个");
            await logWriter.WriteLineAsync();
        }

        // 记录已创建的试验记录，key = 记录编号，value = TestRecord
        var createdRecords = new Dictionary<string, TestRecord>(StringComparer.OrdinalIgnoreCase);

        // ===== 第一阶段：处理汇总CSV和其他类型，创建试验记录 =====
        var mainItems = summaryItems.Concat(otherItems).ToList();
        foreach (var (item, index) in mainItems.Select((x, i) => (x, i)))
        {
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
                var recordCode = $"{item.ParsedProjectCode}_{item.ParsedUnitCode}_{leafCode}_{item.ParsedPackage.TestTime:yyyyMMddHHmmss}";

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
                    Current = index + 1,
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
        foreach (var curveItem in curveItems)
        {
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
                var recordCode = $"{curveItem.ParsedProjectCode}_{curveItem.ParsedUnitCode}_{leafCode}_{curveItem.ParsedPackage.TestTime:yyyyMMddHHmmss}";

                // 查找对应的汇总记录
                if (createdRecords.TryGetValue(recordCode, out var summaryRecord))
                {
                    // 找到对应记录，附加曲线数据
                    if (curveItem.ParsedPackage.ProcessDataPoints != null && curveItem.ParsedPackage.ProcessDataPoints.Any())
                    {
                        var processData = BuildProcessData(curveItem.ParsedPackage.ProcessDataPoints);
                        processData.RecordCode = recordCode;  // TestProcessData 与 TestRecord 共享 RecordCode

                        // 添加到数据库
                        AppServices.DbContext.TestProcessData.Add(processData);
                        await AppServices.DbContext.SaveChangesAsync();

                        if (logWriter != null)
                        {
                            await logWriter.WriteLineAsync($"  ✅ 曲线数据已附加到记录: {recordCode}");
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
                    Current = mainItems.Count + curveProcessed,
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
    /// 确保某条导入项的项目/机组/路径节点链在数据库中存在，缺失则创建。
    /// 返回叶子（试验对象）节点编码。
    /// </summary>
    private async Task<string> EnsurePathExistsAsync(ParsedPathInfo item)
    {
        using var context = DbContextFactory.CreateDbContext();

        var projectCode = item.ParsedProjectCode!;
        var unitCode = item.ParsedUnitCode!;

        // --- 项目 ---
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

        // --- 机组 ---
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

        // --- 路径节点链（系统→贯穿件→阀门）---
        string? parentCode = null;
        foreach (var level in item.ObjectLevels)
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

            parentCode = level.Code;
        }

        return item.ObjectLevels.Last().Code;
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
    ///   "FlowCurve": { "Unit": "L/min", "Data": [0.01, 0.02, ...] },
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
                    if (DateTime.TryParse(value, out var testTime))
                        package.TestTime = testTime;
                    break;
                case "result":
                    package.Result = value;
                    break;
                case "leakagerate":
                    if (decimal.TryParse(value, out var leakageRate))
                        package.LeakageRate = leakageRate;
                    break;
                case "testpressure":
                    if (decimal.TryParse(value, out var testPressure))
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
    };

    /// <summary>通道标识 → 显示名称（中文）</summary>
    private static readonly Dictionary<string, string> ChannelDisplayNames = new()
    {
        ["Pressure"]  = "压力P1",
        ["Flow"]      = "流量M1",
        ["Flow2"]     = "流量M2",
        ["Temp"]      = "温度T",
        ["Pressure2"] = "压力P2",
    };

    /// <summary>通道标识 → 单位</summary>
    private static readonly Dictionary<string, string> ChannelUnits = new()
    {
        ["Pressure"]  = "MPa",
        ["Flow"]      = "L/min",
        ["Flow2"]     = "L/min",
        ["Temp"]      = "℃",
        ["Pressure2"] = "MPa",
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
        if (decimal.TryParse(leakage, out var lr)) package.LeakageRate = lr;

        var pressure = LookupField(map, "试验压力", "压力", "testpressure");
        if (decimal.TryParse(pressure, out var tp)) package.TestPressure = tp;

        var testTime = LookupField(map, "试验时间", "测试时间", "采集时间", "testtime");
        var parsedTime = ParseCsvDateTime(testTime);
        if (parsedTime.HasValue) package.TestTime = parsedTime.Value;

        return package;
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
        => decimal.TryParse(value?.Trim().Trim('"'), out var d) ? d : 0m;

    private static DateTime? ParseCsvDateTime(string value)
        => DateTime.TryParse(value?.Trim().Trim('"'), out var dt) ? dt : null;

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

        if (string.IsNullOrWhiteSpace(parsedData.DeviceCode))
            errors.Add("测量装置编码不能为空");

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
    /// 检查测量装置是否存在于数据库中
    /// </summary>
    private async Task CheckDeviceExistsAsync(string deviceCode)
    {
        var deviceExists = await AppServices.DbContext.MeasurementDevices
            .AsNoTracking()
            .AnyAsync(d => d.DeviceCode == deviceCode);

        if (!deviceExists)
        {
            throw new InvalidOperationException(
                $"测量装置不存在：装置编码 \"{deviceCode}\" 在系统中未注册。请先在\"测量装置台账\"中添加该装置，或使用已存在的装置编码。");
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
        double[] timeAxis;
        var firstSample = dataPoints[0].SampleTime;
        if (dataPoints.All(p => p.SampleTime.HasValue) && firstSample.HasValue)
        {
            var baseTime = firstSample.Value;
            timeAxis = dataPoints.Select(p => (p.SampleTime!.Value - baseTime).TotalSeconds).ToArray();
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
    /// CSV 文件类型（曲线/汇总/其他），用于批量上传时区分处理
    /// </summary>
    public CsvFileType CsvFileType { get; set; } = CsvFileType.Other;

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
}
