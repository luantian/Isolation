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
            var package = ParseDeviceCsv(csv);

            // 配对同目录的"结果汇总 CSV"，合并对象/装置/泄漏率/判定等元数据
            var summaryFile = FindSummaryFileInFolder(filePath);
            if (summaryFile != null)
            {
                var summaryCsv = await ReadTextWithEncodingAsync(summaryFile);
                ParseResultSummaryCsv(summaryCsv, package);
            }

            return package;
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
    /// 递归扫描文件夹，获取所有"主数据文件"（曲线 CSV / 旧版 json / txt）。
    /// 结果汇总 CSV 不作为独立条目返回——它会被配对合并到对应曲线文件。
    /// </summary>
    public List<string> ScanFolderForPackages(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"文件夹不存在: {folderPath}");

        var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".json" or ".txt" or ".csv")
            .ToList();

        // CSV 需区分：曲线文件作为主条目，结果汇总文件配对合并、不单列
        return files
            .Where(f => Path.GetExtension(f).ToLowerInvariant() != ".csv" || SniffCsvKind(f) != CsvKind.Summary)
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
    /// </summary>
    public async Task<BatchUploadResult> BatchUploadAsync(
        List<ParsedPathInfo> items,
        string operatorName,
        IProgress<BatchUploadProgress>? progress = null)
    {
        var result = new BatchUploadResult();
        var readyItems = items.Where(i => i.IsReady && !i.IsSkipped).ToList();

        result.TotalCount = readyItems.Count;

        System.Diagnostics.Debug.WriteLine($"[BatchUpload] 开始上传，总计 {readyItems.Count} 个文件");

        foreach (var (item, index) in readyItems.Select((x, i) => (x, i)))
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[BatchUpload] 处理文件 {index + 1}/{readyItems.Count}: {item.FileName}");

                if (item.ParsedPackage == null || item.ObjectLevels.Count == 0
                    || string.IsNullOrWhiteSpace(item.ParsedProjectCode)
                    || string.IsNullOrWhiteSpace(item.ParsedUnitCode))
                {
                    item.ErrorMessage = "路径信息不完整，无法导入";
                    result.FailedCount++;
                    result.FailedItems.Add(item);
                    System.Diagnostics.Debug.WriteLine($"[BatchUpload] 失败: {item.FileName} - {item.ErrorMessage}");
                    continue;
                }

                // 1. 确保项目/机组/路径节点链存在（缺失则按文件夹层级自动创建），返回叶子节点编码
                var leafCode = await EnsurePathExistsAsync(item);
                System.Diagnostics.Debug.WriteLine($"[BatchUpload] 路径节点已确保存在，叶子节点: {leafCode}");

                // 2. 生成记录编号（项目_机组_对象_时间）
                var recordCode = $"{item.ParsedProjectCode}_{item.ParsedUnitCode}_{leafCode}_{item.ParsedPackage.TestTime:yyyyMMddHHmmss}";
                System.Diagnostics.Debug.WriteLine($"[BatchUpload] 记录编号: {recordCode}");

                // 3. 身份回填：曲线 CSV 不含对象编码，用叶子节点编码回填；装置/结果以汇总文件为准
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
                System.Diagnostics.Debug.WriteLine($"[BatchUpload] 成功: {item.FileName} -> 记录编号: {testRecord.RecordCode}");

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
                System.Diagnostics.Debug.WriteLine($"[BatchUpload] 异常: {item.FileName} - {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[BatchUpload] 异常详情: {ex}");
            }
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

            return Task.FromResult(package);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"JSON 格式解析失败: {ex.Message}", ex);
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
    /// 解析真实装置导出的曲线 CSV（5 通道时序数据）。
    /// 期望表头（顺序可变，按列名识别）：
    ///   导出时间, 实时压力P1, 瞬时流量M1, 瞬时流量M2, 温度T_R, 压力P2_R
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

        // 解析表头，建立"列名 -> 列索引"映射
        var header = SplitCsvLine(lines[0]);
        int idxTime = FindColumn(header, "导出时间", "时间", "采集时间", "time");
        int idxP1 = FindColumn(header, "实时压力P1", "压力P1", "P1", "pressure");
        int idxM1 = FindColumn(header, "瞬时流量M1", "流量M1", "M1", "flow");
        int idxM2 = FindColumn(header, "瞬时流量M2", "流量M2", "M2", "flow2");
        int idxT = FindColumn(header, "温度T_R", "温度T", "温度", "T_R", "temp");
        int idxP2 = FindColumn(header, "压力P2_R", "压力P2", "P2_R", "P2", "pressure2");

        var points = new List<ProcessDataPoint>();
        for (int i = 1; i < lines.Length; i++)
        {
            var cols = SplitCsvLine(lines[i]);
            if (cols.Length == 0) continue;

            var point = new ProcessDataPoint
            {
                SampleTime = idxTime >= 0 ? ParseCsvDateTime(GetCol(cols, idxTime)) : null,
                Pressure = ParseCsvDecimal(GetCol(cols, idxP1)),
                Flow = ParseCsvDecimal(GetCol(cols, idxM1)),
                Flow2 = ParseCsvDecimal(GetCol(cols, idxM2)),
                Temp = ParseCsvDecimal(GetCol(cols, idxT)),
                Pressure2 = ParseCsvDecimal(GetCol(cols, idxP2)),
            };
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
    /// 切分一行 CSV，去除字段两端引号。简单实现（字段内不含逗号的常见情况）。
    /// </summary>
    private static string[] SplitCsvLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return [];
        return line.Split(',')
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
    /// 构建过程数据对象（5 通道 + 真实时间轴）
    /// </summary>
    private static TestProcessData BuildProcessData(List<ProcessDataPoint> dataPoints)
    {
        if (dataPoints == null || !dataPoints.Any())
        {
            throw new ArgumentException("过程数据不能为空", nameof(dataPoints));
        }

        // 提取各通道数组
        var pressures = dataPoints.Select(p => p.Pressure).ToArray();
        var flows = dataPoints.Select(p => p.Flow).ToArray();
        var flow2s = dataPoints.Select(p => p.Flow2).ToArray();
        var temps = dataPoints.Select(p => p.Temp).ToArray();
        var pressure2s = dataPoints.Select(p => p.Pressure2).ToArray();

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

        var processData = new TestProcessData
        {
            PressureCurveJson = JsonSerializer.Serialize(pressures),
            FlowCurveJson = JsonSerializer.Serialize(flows),
            Flow2CurveJson = JsonSerializer.Serialize(flow2s),
            TempCurveJson = JsonSerializer.Serialize(temps),
            Pressure2CurveJson = JsonSerializer.Serialize(pressure2s),
            TimeAxisJson = JsonSerializer.Serialize(timeAxis),
            PressureMin = pressures.Min(),
            PressureMax = pressures.Max(),
            FlowMin = flows.Min(),
            FlowMax = flows.Max(),
            Flow2Min = flow2s.Min(),
            Flow2Max = flow2s.Max(),
            TempMin = temps.Min(),
            TempMax = temps.Max(),
            Pressure2Min = pressure2s.Min(),
            Pressure2Max = pressure2s.Max(),
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
/// 过程数据点（对应真实装置导出 CSV 的一行：导出时间 + 5 通道）
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
}
