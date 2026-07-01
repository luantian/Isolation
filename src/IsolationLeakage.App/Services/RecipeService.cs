using System.Text;
using System.Text.Json;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 试验配方管理服务
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
            .OrderByDescending(r => r.CreatedAt)
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
            .OrderByDescending(r => r.CreatedAt)
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
    /// 根据编码获取配方
    /// </summary>
    public async Task<TestRecipe?> GetByCodeAsync(string recipeCode)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecipes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RecipeCode == recipeCode);
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

            // 更新配方
            existing.RecipeCode = recipe.RecipeCode;
            existing.RecipeName = recipe.RecipeName;
            existing.Description = recipe.Description;
            existing.AirtightTargetPressureP1 = recipe.AirtightTargetPressureP1;
            existing.AirtightAllowDropValue = recipe.AirtightAllowDropValue;
            existing.FineBlowTargetPressureP1 = recipe.FineBlowTargetPressureP1;
            existing.PurgeReleasePressure = recipe.PurgeReleasePressure;
            existing.NormalExpectedLeakFlow = recipe.NormalExpectedLeakFlow;
            existing.SmallPrechargeTargetP1 = recipe.SmallPrechargeTargetP1;
            existing.SmallPrechargeTargetP2 = recipe.SmallPrechargeTargetP2;
            existing.MediumPrechargeTargetP1 = recipe.MediumPrechargeTargetP1;
            existing.MediumPrechargeTargetP2 = recipe.MediumPrechargeTargetP2;
            existing.LargePrechargeTargetP1 = recipe.LargePrechargeTargetP1;
            existing.LargePrechargeTargetP2 = recipe.LargePrechargeTargetP2;
            existing.IsEnabled = recipe.IsEnabled;
            existing.SortOrder = recipe.SortOrder;
            existing.UpdatedAt = DateTime.Now;
            existing.UpdatedBy = operatorName;

            // 创建新版本（在当前事务的 DbContext 内查询，保证一致性）
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
    /// 检查配方编码是否已存在
    /// </summary>
    public async Task<bool> CodeExistsAsync(string recipeCode, int? excludeId = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        return await context.TestRecipes
            .AsNoTracking()
            .AnyAsync(r => r.RecipeCode == recipeCode && r.Id != excludeId);
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
            RecipeCode = recipe.RecipeCode,
            RecipeName = recipe.RecipeName,
            Description = recipe.Description,
            AirtightTargetPressureP1 = recipe.AirtightTargetPressureP1,
            AirtightAllowDropValue = recipe.AirtightAllowDropValue,
            FineBlowTargetPressureP1 = recipe.FineBlowTargetPressureP1,
            PurgeReleasePressure = recipe.PurgeReleasePressure,
            NormalExpectedLeakFlow = recipe.NormalExpectedLeakFlow,
            SmallPrechargeTargetP1 = recipe.SmallPrechargeTargetP1,
            SmallPrechargeTargetP2 = recipe.SmallPrechargeTargetP2,
            MediumPrechargeTargetP1 = recipe.MediumPrechargeTargetP1,
            MediumPrechargeTargetP2 = recipe.MediumPrechargeTargetP2,
            LargePrechargeTargetP1 = recipe.LargePrechargeTargetP1,
            LargePrechargeTargetP2 = recipe.LargePrechargeTargetP2,
            IsEnabled = recipe.IsEnabled,
            SortOrder = recipe.SortOrder,
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
    /// 导出配方为CSV格式（按甲方格式）
    /// </summary>
    public async Task<string> ExportToCsvAsync(List<int>? recipeIds = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        var recipes = recipeIds != null && recipeIds.Count > 0
            ? await context.TestRecipes.Where(r => recipeIds.Contains(r.Id)).ToListAsync()
            : await context.TestRecipes.OrderBy(r => r.SortOrder).ToListAsync();

        var sb = new StringBuilder();
        // CSV 表头（按甲方配方组0.csv格式）
        sb.AppendLine("配方编码,气密目标压力P1,气密下降值,精吹目标压力P1,吹扫泄压压力,常规预期泄露流量,常规小预充压目标压力P1,常规小预充压目标压力P2,常规中预充压目标压力P1,常规中预充压目标压力P2,常规大预充压目标压力P1,常规大预充压目标压力P2");

        foreach (var r in recipes)
        {
            sb.AppendLine($"{r.RecipeCode},{r.AirtightTargetPressureP1},{r.AirtightAllowDropValue},{r.FineBlowTargetPressureP1},{r.PurgeReleasePressure},{r.NormalExpectedLeakFlow},{r.SmallPrechargeTargetP1},{r.SmallPrechargeTargetP2},{r.MediumPrechargeTargetP1},{r.MediumPrechargeTargetP2},{r.LargePrechargeTargetP1},{r.LargePrechargeTargetP2}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从CSV导入配方
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
                if (fields.Length < 12) continue;

                var recipeCode = fields[0].Trim();

                // 检查是否已存在
                var existing = await context.TestRecipes
                    .FirstOrDefaultAsync(r => r.RecipeCode == recipeCode);

                if (existing != null)
                {
                    // 更新现有配方
                    existing.AirtightTargetPressureP1 = decimal.TryParse(fields[1], out var p1) ? p1 : 0;
                    existing.AirtightAllowDropValue = decimal.TryParse(fields[2], out var drop) ? drop : 0;
                    existing.FineBlowTargetPressureP1 = decimal.TryParse(fields[3], out var p3) ? p3 : 0;
                    existing.PurgeReleasePressure = decimal.TryParse(fields[4], out var p4) ? p4 : 0;
                    existing.NormalExpectedLeakFlow = decimal.TryParse(fields[5], out var flow) ? flow : 0;
                    existing.SmallPrechargeTargetP1 = decimal.TryParse(fields[6], out var p5) ? p5 : 0;
                    existing.SmallPrechargeTargetP2 = decimal.TryParse(fields[7], out var p6) ? p6 : 0;
                    existing.MediumPrechargeTargetP1 = decimal.TryParse(fields[8], out var p7) ? p7 : 0;
                    existing.MediumPrechargeTargetP2 = decimal.TryParse(fields[9], out var p8) ? p8 : 0;
                    existing.LargePrechargeTargetP1 = decimal.TryParse(fields[10], out var p9) ? p9 : 0;
                    existing.LargePrechargeTargetP2 = decimal.TryParse(fields[11], out var p10) ? p10 : 0;
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
                        RecipeCode = recipeCode,
                        RecipeName = $"配方{recipeCode}",
                        AirtightTargetPressureP1 = decimal.TryParse(fields[1], out var p1) ? p1 : 0,
                        AirtightAllowDropValue = decimal.TryParse(fields[2], out var drop) ? drop : 0,
                        FineBlowTargetPressureP1 = decimal.TryParse(fields[3], out var p3) ? p3 : 0,
                        PurgeReleasePressure = decimal.TryParse(fields[4], out var p4) ? p4 : 0,
                        NormalExpectedLeakFlow = decimal.TryParse(fields[5], out var flow) ? flow : 0,
                        SmallPrechargeTargetP1 = decimal.TryParse(fields[6], out var p5) ? p5 : 0,
                        SmallPrechargeTargetP2 = decimal.TryParse(fields[7], out var p6) ? p6 : 0,
                        MediumPrechargeTargetP1 = decimal.TryParse(fields[8], out var p7) ? p7 : 0,
                        MediumPrechargeTargetP2 = decimal.TryParse(fields[9], out var p8) ? p8 : 0,
                        LargePrechargeTargetP1 = decimal.TryParse(fields[10], out var p9) ? p9 : 0,
                        LargePrechargeTargetP2 = decimal.TryParse(fields[11], out var p10) ? p10 : 0,
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

    /// <summary>
    /// 初始化默认配方（系统首次运行时）
    /// </summary>
    public async Task InitializeDefaultRecipesAsync(string? operatorName = null)
    {
        using var context = DbContextFactory.CreateDbContext();
        if (await context.TestRecipes.AnyAsync()) return;

        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            var defaultRecipes = new List<TestRecipe>
            {
                new()
                {
                    RecipeCode = "A",
                    RecipeName = "配方A - 低压标准",
                    Description = "适用于低压密封试验",
                    AirtightTargetPressureP1 = 1,
                    AirtightAllowDropValue = 0,
                    FineBlowTargetPressureP1 = 6,
                    PurgeReleasePressure = 0,
                    NormalExpectedLeakFlow = 0,
                    SmallPrechargeTargetP1 = 0,
                    SmallPrechargeTargetP2 = 0,
                    MediumPrechargeTargetP1 = 0,
                    MediumPrechargeTargetP2 = 0,
                    LargePrechargeTargetP1 = 0,
                    LargePrechargeTargetP2 = 0,
                    IsEnabled = true,
                    SortOrder = 1,
                    CreatedBy = operatorName
                },
                new()
                {
                    RecipeCode = "B",
                    RecipeName = "配方B - 中压标准",
                    Description = "适用于中压密封试验",
                    AirtightTargetPressureP1 = 5,
                    AirtightAllowDropValue = 2,
                    FineBlowTargetPressureP1 = 6,
                    PurgeReleasePressure = 0,
                    NormalExpectedLeakFlow = 0,
                    SmallPrechargeTargetP1 = 0,
                    SmallPrechargeTargetP2 = 0,
                    MediumPrechargeTargetP1 = 0,
                    MediumPrechargeTargetP2 = 0,
                    LargePrechargeTargetP1 = 0,
                    LargePrechargeTargetP2 = 0,
                    IsEnabled = true,
                    SortOrder = 2,
                    CreatedBy = operatorName
                },
                new()
                {
                    RecipeCode = "C",
                    RecipeName = "配方C - 高压精吹",
                    Description = "适用于高压精吹试验",
                    AirtightTargetPressureP1 = 5,
                    AirtightAllowDropValue = 0,
                    FineBlowTargetPressureP1 = 3,
                    PurgeReleasePressure = 0,
                    NormalExpectedLeakFlow = 0,
                    SmallPrechargeTargetP1 = 0,
                    SmallPrechargeTargetP2 = 0,
                    MediumPrechargeTargetP1 = 0,
                    MediumPrechargeTargetP2 = 0,
                    LargePrechargeTargetP1 = 0,
                    LargePrechargeTargetP2 = 0,
                    IsEnabled = true,
                    SortOrder = 3,
                    CreatedBy = operatorName
                }
            };

            // 批量添加所有配方（一次数据库操作）
            context.TestRecipes.AddRange(defaultRecipes);
            await context.SaveChangesAsync();

            // 为每个配方创建版本1
            foreach (var recipe in defaultRecipes)
            {
                var version = RecipeVersion.CreateFromRecipe(recipe, "初始创建", operatorName);
                version.VersionNumber = 1;
                context.RecipeVersions.Add(version);
            }
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
