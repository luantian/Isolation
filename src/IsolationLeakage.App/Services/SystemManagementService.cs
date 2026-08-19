using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;
using IsolationLeakage.App.Data;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 系统管理服务
/// </summary>
public sealed class SystemManagementService
{
    /// <summary>
    /// 备份数据库到指定文件
    /// </summary>
    /// <param name="backupPath">备份文件完整路径，例如 D:\Backups\Isolation_20260611.bak</param>
    public async Task BackupDatabaseAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new ArgumentNullException(nameof(backupPath));

        // 用当前活跃库连接（跟随主从故障切换），避免主库宕机、系统在从库运行期间备份始终连主库而全部失败
        var connectionString = DbContextFactory.GetActiveConnectionString();

        // BACKUP DATABASE 需要连接到 master 库
        var masterConnectionStr = GetMasterConnectionString(connectionString);

        // 确保目录存在
        var directory = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqlConnection(masterConnectionStr);
        await connection.OpenAsync();

        // 使用参数化防止 SQL 注入（BACKUP 语句不支持参数，需手动拼接）
        // ✅ 加 N 前缀支持中文路径；去掉 COMPRESSION（Express 版不支持）
        var safePath = "N'" + backupPath.Replace("'", "''") + "'";
        var sql = $"BACKUP DATABASE [{GetDatabaseName(connectionString)}] TO DISK = {safePath} WITH INIT, STATS = 10";

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 300; // 5分钟超时
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 校验备份文件是否合法（可还原的 SQL Server 备份）
    /// </summary>
    /// <param name="backupPath">备份文件完整路径</param>
    /// <returns>校验结果，成功返回 null，失败返回错误信息</returns>
    public async Task<string?> VerifyBackupFileAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return "备份文件路径为空";

        if (!File.Exists(backupPath))
            return $"备份文件不存在: {backupPath}";

        // 用当前活跃库连接（跟随主从故障切换）
        var connectionString = DbContextFactory.GetActiveConnectionString();
        var masterConnectionStr = GetMasterConnectionString(connectionString);

        try
        {
            using var connection = new SqlConnection(masterConnectionStr);
            await connection.OpenAsync();

            var safePath = "N'" + backupPath.Replace("'", "''") + "'";
            var sql = $"RESTORE VERIFYONLY FROM DISK = {safePath}";

            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync();

            return null; // 校验通过
        }
        catch (SqlException ex)
        {
            return $"备份文件校验失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"校验过程出错: {ex.Message}";
        }
    }

    /// <summary>
    /// 获取备份目录下的备份文件列表（按时间倒序）
    /// </summary>
    /// <param name="backupDirectory">备份文件所在目录</param>
    /// <returns>备份文件信息列表</returns>
    public List<BackupFileInfo> GetBackupList(string? backupDirectory = null)
    {
        // 与实际备份写入目录保持一致：AutoBackupService.BackupDirectory 优先用户配置目录，
        // 其次 SQL Server 默认备份目录。此前固定扫应用目录 Backups，用户自定义备份路径后
        // 历史列表永远显示"暂无备份记录"
        var dir = backupDirectory ?? Services.AutoBackupService.Instance.BackupDirectory;

        if (!Directory.Exists(dir))
            return new List<BackupFileInfo>();

        return Directory.GetFiles(dir, "*.bak")
            .Select(f => new FileInfo(f))
            .Select(fi => new BackupFileInfo
            {
                FileName = fi.Name,
                FullPath = fi.FullName,
                SizeBytes = fi.Length,
                CreatedTime = fi.CreationTime,
                LastModifiedTime = fi.LastWriteTime
            })
            .OrderByDescending(b => b.CreatedTime)
            .ToList();
    }

    /// <summary>
    /// 从备份文件还原数据库
    /// </summary>
    /// <param name="backupPath">备份文件完整路径</param>
    public async Task RestoreDatabaseAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new ArgumentNullException(nameof(backupPath));

        if (!File.Exists(backupPath))
            throw new FileNotFoundException("备份文件不存在", backupPath);

        // 与备份/校验一致，用当前活跃库连接（跟随主从故障切换）：
        // 主库故障期间系统在从库运行时还原，若仍连主库要么连接失败、要么还原到非预期实例
        var connectionString = DbContextFactory.GetActiveConnectionString();
        var databaseName = GetDatabaseName(connectionString);
        var masterConnectionStr = GetMasterConnectionString(connectionString);

        using var connection = new SqlConnection(masterConnectionStr);
        await connection.OpenAsync();

        // 1. 设置为单用户模式（断开其他连接）
        await ExecuteNonQueryAsync(connection, $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");

        try
        {
            // 2. 执行还原（N 前缀支持中文路径）
            var safePath = "N'" + backupPath.Replace("'", "''") + "'";
            var restoreSql = $"RESTORE DATABASE [{databaseName}] FROM DISK = {safePath} WITH REPLACE, STATS = 10";

            using var command = new SqlCommand(restoreSql, connection);
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            // 3. 恢复多用户模式
            await ExecuteNonQueryAsync(connection, $"ALTER DATABASE [{databaseName}] SET MULTI_USER");
        }
    }

    /// <summary>
    /// 生成完整数据库备份的 SQL 脚本
    /// </summary>
    /// <returns>SQL 备份脚本内容</returns>
    public string GenerateBackupScript()
    {
        var connectionString = DbContextFactory.GetDefaultConnectionString();
        var databaseName = GetDatabaseName(connectionString);
        var defaultBackupDir = GetDefaultBackupDirectory();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupFileName = $"{databaseName}_FullBackup_{timestamp}.bak";
        var backupPath = Path.Combine(defaultBackupDir, backupFileName);

        // 确保目录存在
        if (!Directory.Exists(defaultBackupDir))
            Directory.CreateDirectory(defaultBackupDir);

        var script = $@"-- =============================================
-- 数据库完整备份脚本
-- 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- 数据库: {databaseName}
-- =============================================

USE master;
GO

-- 执行备份
BACKUP DATABASE [{databaseName}]
TO DISK = N'{backupPath}'
WITH INIT, COMPRESSION, STATS = 10;
GO

PRINT '备份完成: {backupPath}';
";
        return script;
    }

    /// <summary>
    /// 导出所有数据为 SQL INSERT 脚本
    /// </summary>
    /// <param name="outputPath">输出文件路径</param>
    public async Task ExportDataAsync(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentNullException(nameof(outputPath));

        var connectionString = DbContextFactory.GetDefaultConnectionString();
        var databaseName = GetDatabaseName(connectionString);

        var sb = new StringBuilder();
        sb.AppendLine("-- =============================================");
        sb.AppendLine($"-- 数据导出脚本");
        sb.AppendLine($"-- 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"-- 数据库: {databaseName}");
        sb.AppendLine("-- =============================================");
        sb.AppendLine();

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // 获取所有用户表
        var tables = await GetTablesAsync(connection, databaseName);

        foreach (var table in tables)
        {
            sb.AppendLine($"-- 表: {table}");
            sb.AppendLine($"SET IDENTITY_INSERT [{table}] ON;");
            sb.AppendLine();

            // 读取表数据
            var selectSql = $"SELECT * FROM [{table}]";
            using var cmd = new SqlCommand(selectSql, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            var columnNames = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                columnNames.Add(reader.GetName(i));

            while (await reader.ReadAsync())
            {
                var values = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i))
                        values.Add("NULL");
                    else
                        values.Add(EscapeSqlValue(reader.GetValue(i)));
                }

                var columns = string.Join(", ", columnNames.Select(c => $"[{c}]"));
                var vals = string.Join(", ", values);
                sb.AppendLine($"INSERT INTO [{table}] ({columns}) VALUES ({vals});");
            }

            reader.Close();
            sb.AppendLine($"SET IDENTITY_INSERT [{table}] OFF;");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString());
    }

    /// <summary>
    /// 导入 SQL 脚本文件
    /// </summary>
    /// <param name="scriptPath">SQL 脚本文件路径</param>
    public async Task ImportDataAsync(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            throw new ArgumentNullException(nameof(scriptPath));

        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("SQL 脚本文件不存在", scriptPath);

        var script = await File.ReadAllTextAsync(scriptPath);
        var connectionString = DbContextFactory.GetDefaultConnectionString();

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // 分割脚本并逐条执行（按 GO 分隔）
        var batches = script.Split(new[] { "\r\nGO\r\n", "\nGO\n", "GO" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("--"))
                continue;

            try
            {
                using var cmd = new SqlCommand(trimmed, connection);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"执行 SQL 批次失败: {ex.Message}\n批次内容: {trimmed.Substring(0, Math.Min(200, trimmed.Length))}", ex);
            }
        }
    }

    #region Private Helpers

    private static string GetMasterConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.InitialCatalog = "master";
        return builder.ConnectionString;
    }

    private static string GetDatabaseName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return builder.InitialCatalog;
    }

    /// <summary>
    /// 获取 SQL Server 实例的默认备份目录（SQL Server 服务账号保证可写）
    /// </summary>
    public static async Task<string> GetSqlServerDefaultBackupDirAsync()
    {
        try
        {
            var connectionString = DbContextFactory.GetDefaultConnectionString();
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupDir') AS NVARCHAR(MAX))", connection);
            cmd.CommandTimeout = 10;
            var result = await cmd.ExecuteScalarAsync();
            var dir = result?.ToString();
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        catch
        {
            // 查询失败时回退
        }
        // 回退：应用目录下的 Backups 文件夹
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
    }

    private static string GetDefaultBackupDirectory()
    {
        // 默认备份目录：应用运行目录下的 Backups 文件夹
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var backupDir = Path.Combine(appDir, "Backups");
        return backupDir;
    }

    private static async Task<List<string>> GetTablesAsync(SqlConnection connection, string databaseName)
    {
        var tables = new List<string>();
        var sql = @"SELECT TABLE_NAME
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_CATALOG = @db
                    ORDER BY TABLE_NAME";

        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@db", databaseName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        return tables;
    }

    private static string EscapeSqlString(string value)
    {
        // 转义单引号并用单引号包裹
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string EscapeSqlValue(object value)
    {
        if (value == null || Convert.IsDBNull(value))
            return "NULL";

        if (value is string str)
            return EscapeSqlString(str);

        if (value is DateTime dt)
            return $"'{dt:yyyy-MM-dd HH:mm:ss}'";

        if (value is bool b)
            return b ? "1" : "0";

        if (value is byte[] bytes)
            return "0x" + BitConverter.ToString(bytes).Replace("-", "");

        // numeric types
        if (value is IFormattable formattable)
            return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);

        return EscapeSqlString(value.ToString() ?? string.Empty);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql, int timeout = 60)
    {
        using var cmd = new SqlCommand(sql, connection);
        cmd.CommandTimeout = timeout;
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion
}

/// <summary>
/// 备份文件信息
/// </summary>
public sealed class BackupFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime LastModifiedTime { get; set; }

    public string SizeDisplay => FormatFileSize(SizeBytes);

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
