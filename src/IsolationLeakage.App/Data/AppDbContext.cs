using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Models.Security;
using IsolationLeakage.App.Services;

namespace IsolationLeakage.App.Data;

/// <summary>
/// 应用程序数据库上下文
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    // DbSets
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<TestObjectPathNode> TestObjectPathNodes => Set<TestObjectPathNode>();
    public DbSet<MeasurementDevice> MeasurementDevices => Set<MeasurementDevice>();
    public DbSet<TestRecord> TestRecords => Set<TestRecord>();
    public DbSet<TestProcessData> TestProcessData => Set<TestProcessData>();
    public DbSet<RealtimeCurveData> RealtimeCurveData => Set<RealtimeCurveData>();
    public DbSet<TaskDownloadRecord> TaskDownloadRecords => Set<TaskDownloadRecord>();
    public DbSet<TestRecipe> TestRecipes => Set<TestRecipe>();

    // Security DbSets（仿若依）
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Menu> Menus => Set<Menu>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RoleMenu> RoleMenus => Set<RoleMenu>();

    // 登录审计日志
    public DbSet<LoginLog> LoginLogs => Set<LoginLog>();

    // 操作审计日志
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Project 配置
        modelBuilder.Entity<Project>()
            .HasIndex(p => p.Name)
            .IsUnique();

        // Unit 配置
        modelBuilder.Entity<Unit>()
            .HasIndex(u => new { u.ProjectCode, u.Name })
            .IsUnique();

        modelBuilder.Entity<Unit>()
            .HasOne(u => u.Project)
            .WithMany(p => p.Units)
            .HasForeignKey(u => u.ProjectCode)
            .OnDelete(DeleteBehavior.Restrict); // 项目下有机组时不允许删除项目

        // TestObjectPathNode 配置（树形结构）
        modelBuilder.Entity<TestObjectPathNode>()
            .HasOne(n => n.Parent)
            .WithMany(n => n.Children)
            .HasForeignKey(n => n.ParentCode)
            .OnDelete(DeleteBehavior.Restrict); // 有子节点时不允许删除

        modelBuilder.Entity<TestObjectPathNode>()
            .HasIndex(n => n.Code)
            .IsUnique(); // 节点编码全局唯一，防止并发重复

        // TestRecord 配置
        modelBuilder.Entity<TestRecord>()
            .HasOne(r => r.Project)
            .WithMany()
            .HasForeignKey(r => r.ProjectCode)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TestRecord>()
            .HasOne(r => r.Unit)
            .WithMany()
            .HasForeignKey(r => r.UnitCode)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TestRecord>()
            .HasOne(r => r.TestObject)
            .WithMany(o => o.TestRecords)
            .HasForeignKey(r => r.ObjectCode)
            .OnDelete(DeleteBehavior.Restrict); // 有试验记录的对象不允许删除

        modelBuilder.Entity<TestRecord>()
            .HasOne(r => r.Device)
            .WithMany(d => d.TestRecords)
            .HasForeignKey(r => r.DeviceCode)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TestRecord>()
            .HasIndex(r => new { r.ProjectCode, r.UnitCode, r.ObjectCode, r.TestTime });

        modelBuilder.Entity<TestRecord>()
            .HasIndex(r => r.TestTime);

        // TestProcessData 配置（一对一关系）
        modelBuilder.Entity<TestProcessData>()
            .HasOne(d => d.TestRecord)
            .WithOne(r => r.ProcessData)
            .HasForeignKey<TestProcessData>(d => d.RecordCode)
            .OnDelete(DeleteBehavior.Cascade);

        // RealtimeCurveData 配置
        modelBuilder.Entity<RealtimeCurveData>()
            .HasIndex(r => r.SessionCode)
            .IsUnique();

        // MeasurementDevice 配置
        modelBuilder.Entity<MeasurementDevice>()
            .HasIndex(d => d.DeviceCode)
            .IsUnique();

        // TestRecipe 配置（试验配方）
        modelBuilder.Entity<TestRecipe>()
            .HasIndex(r => r.RecipeCode)
            .IsUnique();

        modelBuilder.Entity<TestRecipe>()
            .HasIndex(r => r.RecipeName);

        // TestRecord 关联 TestRecipe 配置
        modelBuilder.Entity<TestRecord>()
            .HasOne(r => r.TestRecipe)
            .WithMany(recipe => recipe.TestRecords)
            .HasForeignKey(r => r.TestRecipeId)
            .OnDelete(DeleteBehavior.SetNull);

        // ================ Security 配置（仿若依） ================

        // User 配置
        modelBuilder.Entity<User>()
            .HasIndex(u => u.UserName)
            .IsUnique();

        // Role 配置
        modelBuilder.Entity<Role>()
            .HasIndex(r => r.RoleKey)
            .IsUnique();

        // Menu 自引用
        modelBuilder.Entity<Menu>()
            .HasOne(m => m.Parent)
            .WithMany(m => m.Children)
            .HasForeignKey(m => m.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        // UserRole 复合主键 + 外键
        modelBuilder.Entity<UserRole>()
            .HasKey(ur => new { ur.UserId, ur.RoleId });

        modelBuilder.Entity<UserRole>()
            .HasOne(ur => ur.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserRole>()
            .HasOne(ur => ur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // RoleMenu 复合主键 + 外键
        modelBuilder.Entity<RoleMenu>()
            .HasKey(rm => new { rm.RoleId, rm.MenuId });

        modelBuilder.Entity<RoleMenu>()
            .HasOne(rm => rm.Role)
            .WithMany(r => r.RoleMenus)
            .HasForeignKey(rm => rm.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RoleMenu>()
            .HasOne(rm => rm.Menu)
            .WithMany(m => m.RoleMenus)
            .HasForeignKey(rm => rm.MenuId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
