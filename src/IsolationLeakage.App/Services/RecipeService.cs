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
    /// 导出配方为CSV格式（按甲方配方组0.csv格式）
    /// </summary>
    public async Task<string> ExportToCsvAsync(List<int>? recipeIds = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        var recipes = recipeIds != null && recipeIds.Count > 0
            ? await context.TestRecipes.Where(r => recipeIds.Contains(r.Id)).OrderBy(r => r.SortOrder).ToListAsync()
            : await context.TestRecipes.OrderBy(r => r.SortOrder).ToListAsync();

        var sb = new StringBuilder();
        // CSV 表头（按甲方配方组0.csv格式）
        sb.AppendLine("配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2");

        foreach (var r in recipes)
        {
            sb.AppendLine($"{r.RecipeName},{r.SequenceNo},{r.System},{r.PenetrationDiameter},{r.ValveNo},{r.ValveNominalDiameter},{r.LeakageLimit},{r.PrechargePressureP2}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从CSV导入配方（按甲方配方组0.csv格式）
    /// </summary>
    public async Task<int> ImportFromCsvAsync(string csvContent, string? operatorName = null)
    {
        var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return 0;

        using var context = DbContextFactory.CreateDbContext();
        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            var count = 0;
            for (int i = 1; i < lines.Length; i++) // 跳过表头
            {
                var fields = lines[i].Split(',');
                if (fields.Length < 8) continue;

                var recipeName = fields[0].Trim();

                // 检查是否已存在
                var existing = await context.TestRecipes
                    .FirstOrDefaultAsync(r => r.RecipeName == recipeName);

                if (existing != null)
                {
                    // 更新现有配方
                    existing.SequenceNo = int.TryParse(fields[1], out var seq) ? seq : 0;
                    existing.System = fields[2].Trim();
                    existing.PenetrationDiameter = decimal.TryParse(fields[3], out var pd) ? pd : 0;
                    existing.ValveNo = fields[4].Trim();
                    existing.ValveNominalDiameter = decimal.TryParse(fields[5], out var vd) ? vd : 0;
                    existing.LeakageLimit = decimal.TryParse(fields[6], out var ll) ? ll : 0;
                    existing.PrechargePressureP2 = decimal.TryParse(fields[7], out var p2) ? p2 : 0;
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
                }
                else
                {
                    // 创建新配方
                    var newRecipe = new TestRecipe
                    {
                        RecipeName = recipeName,
                        SequenceNo = int.TryParse(fields[1], out var seq) ? seq : 0,
                        System = fields[2].Trim(),
                        PenetrationDiameter = decimal.TryParse(fields[3], out var pd) ? pd : 0,
                        ValveNo = fields[4].Trim(),
                        ValveNominalDiameter = decimal.TryParse(fields[5], out var vd) ? vd : 0,
                        LeakageLimit = decimal.TryParse(fields[6], out var ll) ? ll : 0,
                        PrechargePressureP2 = decimal.TryParse(fields[7], out var p2) ? p2 : 0,
                        IsEnabled = true,
                        SortOrder = await context.TestRecipes.CountAsync() + 1,
                        CreatedAt = DateTime.Now,
                        CreatedBy = operatorName
                    };
                    context.TestRecipes.Add(newRecipe);
                    await context.SaveChangesAsync(); // 先保存获取ID

                    // 创建版本1
                    var version = RecipeVersion.CreateFromRecipe(newRecipe, "CSV导入创建", operatorName);
                    version.VersionNumber = 1;
                    context.RecipeVersions.Add(version);
                }
                count++;
            }

            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return count;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
