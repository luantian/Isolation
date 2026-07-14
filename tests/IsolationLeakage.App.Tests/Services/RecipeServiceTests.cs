using FluentAssertions;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using IsolationLeakage.App.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IsolationLeakage.App.Tests.Services;

/// <summary>
/// RecipeService 数据库操作测试（使用 InMemory DB）
/// </summary>
public class RecipeServiceTests : IDisposable
{
    private readonly string _dbName;

    public RecipeServiceTests()
    {
        // 每个测试类实例使用独立的 InMemory 数据库
        _dbName = $"RecipeTest_{Guid.NewGuid()}";
        DbContextFactory.SetTestFactory(() => TestDbContextHelper.CreateInMemoryContext(_dbName));
    }

    public void Dispose()
    {
        DbContextFactory.SetTestFactory(null);
    }

    // ── CSV 导入 ──

    [Fact]
    public async Task ImportFromCsv_StandardFormat_CreatesRecipes()
    {
        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                  "AA,1,CAS,10.2,FBDF,10.2,200,0.123\n" +
                  "BB,2,CAM,15.5,FBDF2,15.5,100,0.456";

        var service = new RecipeService();
        var result = await service.ImportFromCsvAsync(csv, "tester");

        result.Created.Should().Be(2);
        result.Updated.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.TotalProcessed.Should().Be(2);

        var recipes = await service.GetAllAsync();
        recipes.Should().HaveCount(2);
        recipes[0].RecipeName.Should().Be("AA");
        recipes[0].System.Should().Be("CAS");
        recipes[0].PenetrationDiameter.Should().Be(10.2m);
        recipes[1].RecipeName.Should().Be("BB");
        recipes[1].LeakageLimit.Should().Be(100m);
    }

    [Fact]
    public async Task ImportFromCsv_EmptyRecipeName_AutoGenerates()
    {
        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                  "AA,1,CAS,10.2,FBDF,10.2,200,0.123\n" +
                  ",2,CAS,10.2,FBDF,10.2,200,0.123\n" +
                  ",3,CAM,15.5,FBDF2,15.5,100,0.456";

        var service = new RecipeService();
        var result = await service.ImportFromCsvAsync(csv, "tester");

        result.Created.Should().Be(3);
        result.TotalProcessed.Should().Be(3);

        var recipes = await service.GetAllAsync();
        recipes.Should().HaveCount(3);
        recipes[0].RecipeName.Should().Be("AA");
        recipes[1].RecipeName.Should().StartWith("未命名配方_");
        recipes[2].RecipeName.Should().StartWith("未命名配方_");
        recipes[1].RecipeName.Should().NotBe(recipes[2].RecipeName); // 保证唯一
    }

    [Fact]
    public async Task ImportFromCsv_DuplicateNames_UpdatesExisting()
    {
        // 先导入一次
        var csv1 = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                   "AA,1,CAS,10.2,FBDF,10.2,200,0.123";
        var service = new RecipeService();
        await service.ImportFromCsvAsync(csv1, "tester");

        // 再导入同名配方（不同数据）
        var csv2 = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                   "AA,2,CAM,15.5,FBDF2,15.5,100,0.456";
        var result = await service.ImportFromCsvAsync(csv2, "tester");

        result.Updated.Should().Be(1);
        result.Created.Should().Be(0);

        var recipe = await service.GetByNameAsync("AA");
        recipe.Should().NotBeNull();
        recipe!.System.Should().Be("CAM");
        recipe.PenetrationDiameter.Should().Be(15.5m);
    }

    [Fact]
    public async Task ImportFromCsv_InvalidNumber_AsZeroNotSkipped()
    {
        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                  "AA,abc,CAS,xyz,FBDF,10.2,200,0.123";

        var service = new RecipeService();
        var result = await service.ImportFromCsvAsync(csv, "tester");

        // 不跳过，按0处理
        result.Created.Should().Be(1);
        result.Skipped.Should().Be(0);
        result.Errors.Should().NotBeEmpty(); // 但有警告

        var recipe = await service.GetByNameAsync("AA");
        recipe!.SequenceNo.Should().Be(0);
        recipe.PenetrationDiameter.Should().Be(0);
    }

    [Fact]
    public async Task ImportFromCsv_ExtendedFormat_ParsesAll()
    {
        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2,启用状态,排序号,备注\n" +
                  "AA,1,CAS,10.2,FBDF,10.2,200,0.123,否,5,测试备注";

        var service = new RecipeService();
        var result = await service.ImportFromCsvAsync(csv, "tester");

        result.Created.Should().Be(1);

        var recipe = await service.GetByNameAsync("AA");
        recipe!.IsEnabled.Should().BeFalse();
        recipe.SortOrder.Should().Be(5);
        recipe.Remark.Should().Be("测试备注");
    }

    [Fact]
    public async Task ImportFromCsv_SkipsEmptyLines()
    {
        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                  "AA,1,CAS,10.2,FBDF,10.2,200,0.123\n" +
                  "\n" +
                  "\n" +
                  "BB,2,CAM,15.5,FBDF2,15.5,100,0.456";

        var service = new RecipeService();
        var result = await service.ImportFromCsvAsync(csv, "tester");

        result.Created.Should().Be(2);
    }

    [Fact]
    public async Task ImportFromCsv_BOMHandling_Works()
    {
        // UTF-8 BOM + 数据
        var csv = "配方名称,序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2\n" +
                  "AA,1,CAS,10.2,FBDF,10.2,200,0.123";

        var service = new RecipeService();
        var result = await service.ImportFromCsvAsync(csv, "tester");

        result.Created.Should().Be(1);
        var recipe = await service.GetByNameAsync("AA");
        recipe.Should().NotBeNull();
    }

    [Fact]
    public async Task ExportToCsv_ThenImport_Roundtrip()
    {
        // 先创建配方
        var service = new RecipeService();
        await service.CreateAsync(new TestRecipe
        {
            RecipeName = "RoundTrip",
            SequenceNo = 42,
            System = "CAS",
            PenetrationDiameter = 10.2m,
            ValveNo = "FBDF",
            ValveNominalDiameter = 10.2m,
            LeakageLimit = 200m,
            PrechargePressureP2 = 0.123m,
        }, "测试", "tester");

        // 导出
        var csv = await service.ExportToCsvAsync();
        csv.Should().Contain("RoundTrip");
        csv.Should().Contain("42");

        // 清空数据库后重新导入
        using (var ctx = DbContextFactory.CreateDbContext())
        {
            ctx.TestRecipes.RemoveRange(ctx.TestRecipes);
            ctx.RecipeVersions.RemoveRange(ctx.RecipeVersions);
            await ctx.SaveChangesAsync();
        }

        var result = await service.ImportFromCsvAsync(csv, "tester");
        result.Created.Should().Be(1);

        var recipe = await service.GetByNameAsync("RoundTrip");
        recipe!.SequenceNo.Should().Be(42);
        recipe.System.Should().Be("CAS");
    }

    // ── CRUD + 版本管理 ──

    [Fact]
    public async Task CreateAsync_CreatesVersion1()
    {
        var service = new RecipeService();
        var recipe = await service.CreateAsync(new TestRecipe
        {
            RecipeName = "NewRecipe",
            SequenceNo = 1,
            System = "CAS",
        }, "初始创建", "tester");

        recipe.Id.Should().BeGreaterThan(0);

        var versions = await service.GetVersionHistoryAsync(recipe.Id);
        versions.Should().HaveCount(1);
        versions[0].VersionNumber.Should().Be(1);
        versions[0].ChangeDescription.Should().Be("初始创建");
        versions[0].IsCurrentVersion.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_CreatesNewVersion()
    {
        var service = new RecipeService();
        var recipe = await service.CreateAsync(new TestRecipe
        {
            RecipeName = "VersionedRecipe",
            System = "CAS",
        });

        var updatedRecipe = await service.GetByIdAsync(recipe.Id);
        updatedRecipe!.System = "CAM";
        var success = await service.UpdateAsync(updatedRecipe, "参数修改", "tester");

        success.Should().BeTrue();

        var versions = await service.GetVersionHistoryAsync(recipe.Id);
        versions.Should().HaveCount(2);
        versions[0].VersionNumber.Should().Be(2); // 最新的在前
        versions[0].IsCurrentVersion.Should().BeTrue();
        versions[1].VersionNumber.Should().Be(1);
        versions[1].IsCurrentVersion.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_HardDelete_WhenNotReferenced()
    {
        var service = new RecipeService();
        var recipe = await service.CreateAsync(new TestRecipe { RecipeName = "ToDelete" });

        var result = await service.DeleteAsync(recipe.Id);
        result.Should().BeTrue();

        var found = await service.GetByIdAsync(recipe.Id);
        found.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_SoftDisable_WhenReferenced()
    {
        var service = new RecipeService();
        var recipe = await service.CreateAsync(new TestRecipe { RecipeName = "Referenced" });

        // 模拟有试验记录引用
        using var ctx = DbContextFactory.CreateDbContext();
        ctx.TestRecords.Add(new TestRecord
        {
            ObjectCode = "OBJ001",
            TestRecipeId = recipe.Id,
            TestTime = DateTime.Now,
            ImportTime = DateTime.Now,
        });
        await ctx.SaveChangesAsync();

        var result = await service.DeleteAsync(recipe.Id);
        result.Should().BeTrue();

        // 软删除：还在但被禁用
        var found = await service.GetByIdAsync(recipe.Id);
        found.Should().NotBeNull();
        found!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task NameExistsAsync_ReturnsCorrectly()
    {
        var service = new RecipeService();
        await service.CreateAsync(new TestRecipe { RecipeName = "Existing" });

        (await service.NameExistsAsync("Existing")).Should().BeTrue();
        (await service.NameExistsAsync("NotExisting")).Should().BeFalse();
    }

    [Fact]
    public async Task NameExistsAsync_ExcludeId_Works()
    {
        var service = new RecipeService();
        var recipe = await service.CreateAsync(new TestRecipe { RecipeName = "Self" });

        // 排除自己时不算重复
        (await service.NameExistsAsync("Self", recipe.Id)).Should().BeFalse();
        // 不排除时算重复
        (await service.NameExistsAsync("Self")).Should().BeTrue();
    }
}
