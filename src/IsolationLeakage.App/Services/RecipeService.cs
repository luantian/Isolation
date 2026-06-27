using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 试验配方管理服务
/// </summary>
public sealed class RecipeService
{
    private readonly AppDbContext _context;

    public RecipeService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取所有启用的配方列表
    /// </summary>
    public async Task<List<TestRecipe>> GetAllEnabledAsync()
    {
        return await _context.TestRecipes
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.RecipeName)
            .ToListAsync();
    }

    /// <summary>
    /// 获取所有配方（含禁用）
    /// </summary>
    public async Task<List<TestRecipe>> GetAllAsync()
    {
        return await _context.TestRecipes
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.RecipeName)
            .ToListAsync();
    }

    /// <summary>
    /// 根据 ID 获取配方
    /// </summary>
    public async Task<TestRecipe?> GetByIdAsync(int id)
    {
        return await _context.TestRecipes.FindAsync(id);
    }

    /// <summary>
    /// 根据编码获取配方
    /// </summary>
    public async Task<TestRecipe?> GetByCodeAsync(string recipeCode)
    {
        return await _context.TestRecipes
            .FirstOrDefaultAsync(r => r.RecipeCode == recipeCode);
    }

    /// <summary>
    /// 创建配方
    /// </summary>
    public async Task<TestRecipe> CreateAsync(TestRecipe recipe, string? operatorName = null)
    {
        recipe.CreatedAt = DateTime.Now;
        recipe.CreatedBy = operatorName;
        recipe.UpdatedAt = null;
        recipe.UpdatedBy = null;

        _context.TestRecipes.Add(recipe);
        await _context.SaveChangesAsync();
        return recipe;
    }

    /// <summary>
    /// 更新配方
    /// </summary>
    public async Task<bool> UpdateAsync(TestRecipe recipe, string? operatorName = null)
    {
        var existing = await _context.TestRecipes.FindAsync(recipe.Id);
        if (existing == null) return false;

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

        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 删除配方
    /// </summary>
    public async Task<bool> DeleteAsync(int id)
    {
        var recipe = await _context.TestRecipes.FindAsync(id);
        if (recipe == null) return false;

        // 检查是否有试验记录使用此配方
        var hasRecords = await _context.TestRecords
            .AnyAsync(r => r.TestRecipeId == id);

        if (hasRecords)
        {
            // 有引用时，软删除（禁用）
            recipe.IsEnabled = false;
            recipe.UpdatedAt = DateTime.Now;
            await _context.SaveChangesAsync();
            return true;
        }

        _context.TestRecipes.Remove(recipe);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 检查配方编码是否已存在
    /// </summary>
    public async Task<bool> CodeExistsAsync(string recipeCode, int? excludeId = null)
    {
        return await _context.TestRecipes
            .AnyAsync(r => r.RecipeCode == recipeCode && r.Id != excludeId);
    }

    /// <summary>
    /// 批量导入配方（从 CSV）
    /// </summary>
    public async Task<int> ImportFromCsvAsync(List<TestRecipe> recipes, string? operatorName = null)
    {
        var count = 0;
        foreach (var recipe in recipes)
        {
            recipe.CreatedAt = DateTime.Now;
            recipe.CreatedBy = operatorName;
            _context.TestRecipes.Add(recipe);
            count++;
        }
        await _context.SaveChangesAsync();
        return count;
    }

    /// <summary>
    /// 初始化默认配方（系统首次运行时）
    /// </summary>
    public async Task InitializeDefaultRecipesAsync(string? operatorName = null)
    {
        if (await _context.TestRecipes.AnyAsync()) return;

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

        _context.TestRecipes.AddRange(defaultRecipes);
        await _context.SaveChangesAsync();
    }
}
