using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using IsolationLeakage.App.Configuration;

namespace IsolationLeakage.App.Data;

/// <summary>
/// 设计时 DbContext 工厂（供 dotnet ef 工具使用）
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = AppConfiguration.GetConnectionString("DefaultConnection");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(100));

        return new AppDbContext(optionsBuilder.Options);
    }
}
