using System.Text;
using System.Text.Json;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 试验配方管理服务（基于甲方配方组0.csv格式）
/// </summary>
public sealed class RecipeService
{
    /// <summary>
    /// 注意：此构造函数保留供 AppServices 初始化使用
    /// 所有数据操作方法内部使用独立的 DbContext，避免并发读取冲突
    /// </summary>
    public RecipeService(AppDbContext? context = null)
    {
        // 不保存 context，每次操作独立创建
    }

    /// <summary>
    /// 获取所有启用的配方列表
    /// </summary>
    public async Task<List<TestRecipe>> GetAllEnabledAsync()
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecipes
            .AsNoTracking()
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .ToListAsync();
    }

    /// <summary>
    /// 获取所有配方（含禁用）
    /// </summary>
    public async Task<List<TestRecipe>> GetAllAsync()
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecipes
            .AsNoTracking()
            .OrderBy(r => r.SortOrder)
            .ToListAsync();
    }

    /// <summary>
    /// 根据 ID 获取配方
    /// </summary>
    public async Task<TestRecipe?> GetByIdAsync(int id)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecipes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>
    /// 根据配方名称获取配方
    /// </summary>
    public async Task<TestRecipe?> GetByNameAsync(string recipeName)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecipes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RecipeName == recipeName);
    }

    /// <summary>
    /// 获取配方的所有版本历史
    /// </summary>
    public async Task<List<RecipeVersion>> GetVersionHistoryAsync(int recipeId)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.RecipeVersions
            .AsNoTracking()
            .Where(v => v.RecipeId == recipeId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync();
    }

    /// <summary>
    /// 获取配方当前版本号
    /// </summary>
    public async Task<int> GetCurrentVersionAsync(int recipeId)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.RecipeVersions
            .Where(v => v.RecipeId == recipeId)
            .MaxAsync(v => (int?)v.VersionNumber) ?? 0;
    }

    /// <summary>
    /// 创建配方（自动创建版本1）
    /// </summary>
    public async Task<TestRecipe> CreateAsync(TestRecipe recipe, string? changeDescription = null, string? operatorName = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            recipe.CreatedAt = DateTime.Now;
            recipe.CreatedBy = operatorName;
            recipe.UpdatedAt = null;
            recipe.UpdatedBy = null;

            context.TestRecipes.Add(recipe);
            await context.SaveChangesAsync();

            // 自动创建版本1
            var version = RecipeVersion.CreateFromRecipe(recipe, changeDescription ?? "初始创建", operatorName);
            version.VersionNumber = 1;
            context.RecipeVersions.Add(version);
            await context.SaveChangesAsync();

            await transaction.CommitAsync();
            return recipe;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 更新配方（自动创建新版本）
    /// </summary>
    public async Task<bool> UpdateAsync(TestRecipe recipe, string? changeDescription = null, string? operatorName = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            var existing = await context.TestRecipes.FindAsync(recipe.Id);
            if (existing == null) return false;

            // 将旧版本标记为非当前版本
            var oldVersions = await context.RecipeVersions
                .Where(v => v.RecipeId == recipe.Id && v.IsCurrentVersion)
                .ToListAsync();
            oldVersions.ForEach(v => v.IsCurrentVersion = false);

            // 更新配方（按新字段）
            existing.RecipeName = recipe.RecipeName;
            existing.SequenceNo = recipe.SequenceNo;
            existing.System = recipe.System;
            existing.PenetrationDiameter = recipe.PenetrationDiameter;
            existing.ValveNo = recipe.ValveNo;
            existing.ValveNominalDiameter = recipe.ValveNominalDiameter;
            existing.LeakageLimit = recipe.LeakageLimit;
            existing.PrechargePressureP2 = recipe.PrechargePressureP2;
            existing.IsEnabled = recipe.IsEnabled;
            existing.SortOrder = recipe.SortOrder;
            existing.Remark = recipe.Remark;
            existing.UpdatedAt = DateTime.Now;
            existing.UpdatedBy = operatorName;

            // 创建新版本
            var currentVersion = await context.RecipeVersions
                .Where(v => v.RecipeId == recipe.Id)
                .MaxAsync(v => (int?)v.VersionNumber) ?? 0;
            var newVersion = RecipeVersion.CreateFromRecipe(existing, changeDescription ?? "参数修改", operatorName);
            newVersion.VersionNumber = currentVersion + 1;
            context.RecipeVersions.Add(newVersion);

            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return true;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 删除配方
    /// </summary>
    public async Task<bool> DeleteAsync(int id)
    {
        using var context = DbContextFactory.CreateDbContext();
        var recipe = await context.TestRecipes.FindAsync(id);
        if (recipe == null) return false;

        // 检查是否有试验记录使用此配方
        var hasRecords = await context.TestRecords
            .AnyAsync(r => r.TestRecipeId == id);

        if (hasRecords)
        {
            // 有引用时，软删除（禁用）
            recipe.IsEnabled = false;
            recipe.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync();
            return true;
        }

        context.TestRecipes.Remove(recipe);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 检查配方名称是否已存在
    /// </summary>
    public async Task<bool> NameExistsAsync(string recipeName, int? excludeId = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecipes
            .AsNoTracking()
            .AnyAsync(r => r.RecipeName == recipeName && r.Id != excludeId);
    }

    /// <summary>
    /// 创建试验记录时使用的配方快照
    /// 返回快照JSON字符串
    /// </summary>
    public async Task<string?> CreateSnapshotForTestAsync(int recipeId)
    {
        var recipe = await GetByIdAsync(recipeId);
        if (recipe == null) return null;

        var snapshot = new RecipeSnapshot
        {
            RecipeId = recipe.Id,
            RecipeName = recipe.RecipeName,
            SequenceNo = recipe.SequenceNo,
            System = recipe.System,
            PenetrationDiameter = recipe.PenetrationDiameter,
            ValveNo = recipe.ValveNo,
            ValveNominalDiameter = recipe.ValveNominalDiameter,
            LeakageLimit = recipe.LeakageLimit,
            PrechargePressureP2 = recipe.PrechargePressureP2,
            Remark = recipe.Remark,
            SnapshotTime = DateTime.Now
        };

        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// 从JSON快照还原配方参数对象（静态方法）
    /// </summary>
    public static RecipeSnapshot? ParseSnapshot(string? snapshotJson)
    {
        if (string.IsNullOrEmpty(snapshotJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<RecipeSnapshot>(snapshotJson);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 导出配方为CSV格式（兼容甲方配方组0.csv格式，扩展可选列）
    /// 必选列：配方名称,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2
    /// 可选列：启用状态,排序号,备注
    /// </summary>
    public async Task<string> ExportToCsvAsync(List<int>? recipeIds = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        var recipes = recipeIds != null && recipeIds.Count > 0
            ? await context.TestRecipes.Where(r => recipeIds.Contains(r.Id)).OrderBy(r => r.SortOrder).ToListAsync()
            : await context.TestRecipes.OrderBy(r => r.SortOrder).ToListAsync();

        var sb = new StringBuilder();
        // 表头（含可选列）
        sb.AppendLine("配方名称,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2,启用状态,排序号,备注");

        foreach (var r in recipes)
        {
            sb.Append(CsvEscape(r.RecipeName));
            sb.Append(',');
            sb.Append(CsvEscape(r.System));
            sb.Append(',');
            sb.Append(r.PenetrationDiameter);
            sb.Append(',');
            sb.Append(CsvEscape(r.ValveNo));
            sb.Append(',');
            sb.Append(r.ValveNominalDiameter);
            sb.Append(',');
            sb.Append(r.LeakageLimit);
            sb.Append(',');
            sb.Append(r.PrechargePressureP2);
            sb.Append(',');
            sb.Append(r.IsEnabled ? "是" : "否");
            sb.Append(',');
            sb.Append(r.SortOrder);
            sb.Append(',');
            sb.Append(CsvEscape(r.Remark ?? string.Empty));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// CSV字段转义：含逗号/引号/换行时用双引号包裹，内部引号双写
    /// </summary>
    internal static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    /// <summary>
    /// CSV行解析：正确处理引号字段（含逗号、换行、双引号转义）
    /// </summary>
    internal static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    // 双引号转义
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inQuotes = false;
                        i++;
                    }
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    i++;
                }
                else if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>
    /// 根据表头名称找到列索引（不区分大小写，去除首尾空格）
    /// </summary>
    internal static Dictionary<string, int> BuildColumnMap(List<string> headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i].Trim().Trim('"');
            map.TryAdd(h, i);
        }
        return map;
    }

    /// <summary>
    /// 安全获取字段值（越界返回空字符串）
    /// </summary>
    internal static string FieldAt(List<string> fields, int index)
        => index < fields.Count ? fields[index].Trim() : string.Empty;

    /// <summary>
    /// 从CSV导入配方（基于表头自动识别列，支持甲方原始格式和扩展格式）
    /// </summary>
    public async Task<CsvImportResult> ImportFromCsvAsync(string csvContent, string? operatorName = null)
    {
        var result = new CsvImportResult();

        // 去除BOM
        if (csvContent.Length > 0 && csvContent[0] == '﻿')
            csvContent = csvContent.Substring(1);

        // 按行拆分（保留空行用于错误报告）
        var allLines = csvContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // 找到第一个非空行作为表头
        int headerIndex = -1;
        for (int i = 0; i < allLines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(allLines[i]))
            {
                headerIndex = i;
                break;
            }
        }
        if (headerIndex < 0)
        {
            result.Errors.Add("文件为空，无有效数据");
            return result;
        }

        var headers = ParseCsvLine(allLines[headerIndex]);
        var colMap = BuildColumnMap(headers);

        // 获取列索引（-1表示该列不存在，所有列都是可选的）
        int idxName    = colMap.TryGetValue("配方名称",              out var v0) ? v0 : -1;
        int idxSeq     = colMap.TryGetValue("序号",                out var v1) ? v1 : -1;
        int idxSys     = colMap.TryGetValue("系统",                out var v2) ? v2 : -1;
        int idxPD      = colMap.TryGetValue("贯穿件直径",          out var v3) ? v3 : -1;
        int idxValveNo = colMap.TryGetValue("试验阀门编号",        out var v4) ? v4 : -1;
        int idxVND     = colMap.TryGetValue("阀门公称直径",        out var v5) ? v5 : -1;
        int idxLL      = colMap.TryGetValue("阀门泄漏率设计最大值", out var v6) ? v6 : -1;
        int idxP2      = colMap.TryGetValue("预充压压力P2",        out var v7) ? v7 : -1;
        int idxEnabled = colMap.TryGetValue("启用状态",            out var v8) ? v8 : -1;
        int idxSort    = colMap.TryGetValue("排序号",              out var v9) ? v9 : -1;
        int idxRemark  = colMap.TryGetValue("备注",                out var v10) ? v10 : -1;

        using var context = DbContextFactory.CreateDbContext();
        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            // 预加载全部配方（用于名称查重）
            var allRecipes = await context.TestRecipes.ToListAsync();
            var nameDict = allRecipes.ToDictionary(r => r.RecipeName, r => r);
            int nextSortOrder = allRecipes.Count > 0 ? allRecipes.Max(r => r.SortOrder) + 1 : 1;
            int unnamedCounter = 0; // 用于为空名称生成唯一名称

            for (int lineIdx = headerIndex + 1; lineIdx < allLines.Length; lineIdx++)
            {
                var line = allLines[lineIdx];
                if (string.IsNullOrWhiteSpace(line)) continue;

                int lineNo = lineIdx + 1; // 1-based 行号（给用户看）
                var fields = ParseCsvLine(line);

                // 试验路径名称为空时自动生成唯一名称
                var recipeName = FieldAt(fields, idxName);
                if (string.IsNullOrWhiteSpace(recipeName))
                {
                    unnamedCounter++;
                    recipeName = $"未命名试验路径_{unnamedCounter}";
                    // 确保不与已有名称冲突
                    while (nameDict.ContainsKey(recipeName))
                    {
                        unnamedCounter++;
                        recipeName = $"未命名试验路径_{unnamedCounter}";
                    }
                    result.Errors.Add($"第{lineNo}行：试验路径名称为空，已自动生成「{recipeName}」");
                }

                // 解析数值字段（解析失败按0处理，不跳过）
                int seq = 0;
                if (idxSeq >= 0)
                {
                    var seqStr = FieldAt(fields, idxSeq);
                    if (!string.IsNullOrEmpty(seqStr) && !int.TryParse(seqStr, out seq))
                    {
                        result.Errors.Add($"第{lineNo}行「{recipeName}」：序号「{seqStr}」不是有效整数，按0处理");
                        seq = 0;
                    }
                }

                string system = idxSys >= 0 ? FieldAt(fields, idxSys) : string.Empty;

                decimal pd = 0;
                if (idxPD >= 0)
                {
                    var s = FieldAt(fields, idxPD);
                    if (!string.IsNullOrEmpty(s) && !decimal.TryParse(s, out pd))
                    {
                        result.Errors.Add($"第{lineNo}行「{recipeName}」：贯穿件直径「{s}」无法解析，按0处理");
                        pd = 0;
                    }
                }

                string valveNo = idxValveNo >= 0 ? FieldAt(fields, idxValveNo) : string.Empty;

                decimal vnd = 0;
                if (idxVND >= 0)
                {
                    var s = FieldAt(fields, idxVND);
                    if (!string.IsNullOrEmpty(s) && !decimal.TryParse(s, out vnd))
                    {
                        result.Errors.Add($"第{lineNo}行「{recipeName}」：阀门公称直径「{s}」无法解析，按0处理");
                        vnd = 0;
                    }
                }

                decimal ll = 0;
                if (idxLL >= 0)
                {
                    var s = FieldAt(fields, idxLL);
                    if (!string.IsNullOrEmpty(s) && !decimal.TryParse(s, out ll))
                    {
                        result.Errors.Add($"第{lineNo}行「{recipeName}」：泄漏率限值「{s}」无法解析，按0处理");
                        ll = 0;
                    }
                }

                decimal p2 = 0;
                if (idxP2 >= 0)
                {
                    var s = FieldAt(fields, idxP2);
                    if (!string.IsNullOrEmpty(s) && !decimal.TryParse(s, out p2))
                    {
                        result.Errors.Add($"第{lineNo}行「{recipeName}」：预充压压力P2「{s}」无法解析，按0处理");
                        p2 = 0;
                    }
                }

                bool isEnabled = true;
                if (idxEnabled >= 0)
                {
                    var s = FieldAt(fields, idxEnabled);
                    isEnabled = s != "否" && s.ToLower() != "false" && s != "0" && !string.IsNullOrEmpty(s);
                }

                int? sortOrder = null;
                if (idxSort >= 0)
                {
                    var s = FieldAt(fields, idxSort);
                    if (!string.IsNullOrEmpty(s) && int.TryParse(s, out var so))
                        sortOrder = so;
                }

                string? remark = idxRemark >= 0 ? FieldAt(fields, idxRemark) : null;
                if (string.IsNullOrWhiteSpace(remark)) remark = null;

                // 创建或更新
                if (nameDict.TryGetValue(recipeName, out var existing))
                {
                    // 更新现有配方
                    existing.SequenceNo = seq;
                    existing.System = system;
                    existing.PenetrationDiameter = pd;
                    existing.ValveNo = valveNo;
                    existing.ValveNominalDiameter = vnd;
                    existing.LeakageLimit = ll;
                    existing.PrechargePressureP2 = p2;
                    existing.IsEnabled = isEnabled;
                    if (sortOrder.HasValue) existing.SortOrder = sortOrder.Value;
                    existing.Remark = remark;
                    existing.UpdatedAt = DateTime.Now;
                    existing.UpdatedBy = operatorName;

                    // 将旧版本标记为非当前版本
                    var oldVersions = await context.RecipeVersions
                        .Where(v => v.RecipeId == existing.Id && v.IsCurrentVersion)
                        .ToListAsync();
                    oldVersions.ForEach(v => v.IsCurrentVersion = false);

                    // 创建新版本
                    var currentVersion = await context.RecipeVersions
                        .Where(v => v.RecipeId == existing.Id)
                        .MaxAsync(v => (int?)v.VersionNumber) ?? 0;
                    var newVersion = RecipeVersion.CreateFromRecipe(existing, "CSV导入更新", operatorName);
                    newVersion.VersionNumber = currentVersion + 1;
                    context.RecipeVersions.Add(newVersion);

                    result.Updated++;
                }
                else
                {
                    // 创建新配方
                    var newRecipe = new TestRecipe
                    {
                        RecipeName = recipeName,
                        SequenceNo = seq,
                        System = system,
                        PenetrationDiameter = pd,
                        ValveNo = valveNo,
                        ValveNominalDiameter = vnd,
                        LeakageLimit = ll,
                        PrechargePressureP2 = p2,
                        IsEnabled = isEnabled,
                        SortOrder = sortOrder ?? nextSortOrder++,
                        Remark = remark,
                        CreatedAt = DateTime.Now,
                        CreatedBy = operatorName
                    };
                    context.TestRecipes.Add(newRecipe);
                    await context.SaveChangesAsync(); // 先保存获取ID

                    // 创建版本1
                    var version = RecipeVersion.CreateFromRecipe(newRecipe, "CSV导入创建", operatorName);
                    version.VersionNumber = 1;
                    context.RecipeVersions.Add(version);

                    nameDict[recipeName] = newRecipe;
                    result.Created++;
                }
                result.TotalProcessed++;
            }

            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return result;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            result.Errors.Add($"导入失败：{ex.Message}");
            return result;
        }
    }
}

/// <summary>
/// CSV导入结果统计
/// </summary>
public sealed class CsvImportResult
{
    /// <summary>新建配方数量</summary>
    public int Created { get; set; }

    /// <summary>更新配方数量</summary>
    public int Updated { get; set; }

    /// <summary>跳过行数（如配方名称为空）</summary>
    public int Skipped { get; set; }

    /// <summary>成功处理的总行数（新建+更新）</summary>
    public int TotalProcessed { get; set; }

    /// <summary>警告/错误信息列表</summary>
    public List<string> Errors { get; } = new();

    /// <summary>是否完全成功（无错误）</summary>
    public bool IsSuccess => Errors.Count == 0;

    /// <summary>生成供用户查看的汇总文本</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Created > 0) parts.Add($"新建 {Created} 个");
            if (Updated > 0) parts.Add($"更新 {Updated} 个");
            if (Skipped > 0) parts.Add($"跳过 {Skipped} 行");
            string main = parts.Count > 0 ? string.Join("，", parts) : "无有效数据";
            if (Errors.Count > 0)
                main += $"，{Errors.Count} 条警告";
            return main;
        }
    }
}
