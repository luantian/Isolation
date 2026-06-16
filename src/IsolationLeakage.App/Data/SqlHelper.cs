using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace IsolationLeakage.App.Data;

/// <summary>
/// SQL Server 原始查询助手（用于 SQL Server 2008 R2 等不支持 OFFSET/FETCH 的版本）
/// </summary>
public static class SqlHelper
{
    private static void WriteLog(string message)
    {
        try
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"sqlhelper-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch { }
    }
    /// <summary>
    /// 使用 ROW_NUMBER() 分页查询试验记录 ID（SQL Server 2008 R2 兼容）
    /// </summary>
    public static async Task<(List<string> RecordCodes, int TotalCount)> GetPaginatedRecordIdsAsync(
        string connectionString,
        int page,
        int pageSize,
        string? resultFilter = null,
        string? keyword = null)
    {
        var ids = new List<string>();
        int totalCount = 0;

        try
        {
            WriteLog($"GetPaginatedRecordIdsAsync: page={page}, pageSize={pageSize}, filter={resultFilter ?? "全部"}, keyword={keyword ?? "(none)"}");

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            WriteLog("Connection opened successfully");

        // 构建 WHERE 条件
        var whereClauses = new List<string>();
        var parameters = new List<SqlParameter>();

        // 结果过滤
        if (!string.IsNullOrEmpty(resultFilter) && resultFilter != "全部")
        {
            var resultValue = resultFilter == "合格" ? 1 : 2; // TestResult.Pass=1, Fail=2
            whereClauses.Add("r.Result = @resultValue");
            parameters.Add(new SqlParameter("@resultValue", resultValue));
        }

        // 关键字搜索
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var likeKeyword = "%" + keyword + "%";
            whereClauses.Add(
                "(r.RecordCode LIKE @kw1 OR r.ObjectCode LIKE @kw2 OR r.ObjectName LIKE @kw3 " +
                "OR r.DeviceCode LIKE @kw4 OR r.DataPackageName LIKE @kw5)");
            parameters.Add(new SqlParameter("@kw1", likeKeyword));
            parameters.Add(new SqlParameter("@kw2", likeKeyword));
            parameters.Add(new SqlParameter("@kw3", likeKeyword));
            parameters.Add(new SqlParameter("@kw4", likeKeyword));
            parameters.Add(new SqlParameter("@kw5", likeKeyword));
        }

        var whereSql = whereClauses.Count > 0
            ? "WHERE " + string.Join(" AND ", whereClauses)
            : "";

        // 查询总数
        var countSql = $"SELECT COUNT(*) FROM TestRecords r {whereSql}";
        WriteLog($"Count SQL: {countSql}");
        using (var countCmd = new SqlCommand(countSql, connection))
        {
            foreach (var p in parameters) countCmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
            var result = await countCmd.ExecuteScalarAsync();
            totalCount = Convert.ToInt32(result);
            WriteLog($"Count result: {totalCount}");
        }

        // 分页查询（ROW_NUMBER 方式，兼容 SQL Server 2008）
        var offset = (page - 1) * pageSize;
        var dataSql = $@"
            SELECT t.RecordCode
            FROM (
                SELECT r.RecordCode,
                       ROW_NUMBER() OVER (ORDER BY r.TestTime DESC) AS RowNum
                FROM TestRecords r
                {whereSql}
            ) t
            WHERE t.RowNum BETWEEN @offset + 1 AND @offset + @pageSize
            ORDER BY t.RowNum";
        WriteLog($"Data SQL: {dataSql.Replace("\n", " ")}");

        using (var dataCmd = new SqlCommand(dataSql, connection))
        {
            foreach (var p in parameters) dataCmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
            dataCmd.Parameters.Add(new SqlParameter("@offset", offset));
            dataCmd.Parameters.Add(new SqlParameter("@pageSize", pageSize));

            using var reader = await dataCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetString(0));
            }
            WriteLog($"Data query returned {ids.Count} IDs");
        }

        WriteLog($"Returning: count={totalCount}, ids={ids.Count}");
        return (ids, totalCount);
    }
    catch (Exception ex)
    {
        WriteLog($"ERROR in GetPaginatedRecordIdsAsync: {ex}");
        throw;
    }
    }

    /// <summary>
    /// 使用 ROW_NUMBER() 分页查询登录日志 ID（SQL Server 2008 R2 兼容）
    /// </summary>
    public static async Task<(List<long> LogIds, int TotalCount)> GetPaginatedLoginLogIdsAsync(
        string connectionString,
        int page,
        int pageSize,
        string? operationTypeFilter = null,
        string? keyword = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null)
    {
        var ids = new List<long>();
        int totalCount = 0;

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var whereClauses = new List<string>();
        var parameters = new List<SqlParameter>();

        if (!string.IsNullOrEmpty(operationTypeFilter) && operationTypeFilter != "全部")
        {
            whereClauses.Add("UserAgent LIKE @opType");
            parameters.Add(new SqlParameter("@opType", $"%Operation: {operationTypeFilter}%"));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            whereClauses.Add("(UserName LIKE @kw1 OR (ClientIp IS NOT NULL AND ClientIp LIKE @kw2))");
            var likeKeyword = "%" + keyword + "%";
            parameters.Add(new SqlParameter("@kw1", likeKeyword));
            parameters.Add(new SqlParameter("@kw2", likeKeyword));
        }

        if (dateFrom.HasValue)
        {
            whereClauses.Add("LoginTime >= @dateFrom");
            parameters.Add(new SqlParameter("@dateFrom", dateFrom.Value));
        }

        if (dateTo.HasValue)
        {
            whereClauses.Add("LoginTime <= @dateTo");
            parameters.Add(new SqlParameter("@dateTo", dateTo.Value));
        }

        var whereSql = whereClauses.Count > 0
            ? "WHERE " + string.Join(" AND ", whereClauses)
            : "";

        // 查询总数
        var countSql = $"SELECT COUNT(*) FROM LoginLogs {whereSql}";
        using (var countCmd = new SqlCommand(countSql, connection))
        {
            foreach (var p in parameters) countCmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
            var result = await countCmd.ExecuteScalarAsync();
            totalCount = Convert.ToInt32(result);
        }

        // 分页查询
        var offset = (page - 1) * pageSize;
        var dataSql = $@"
            SELECT t.LogId
            FROM (
                SELECT LogId,
                       ROW_NUMBER() OVER (ORDER BY LoginTime DESC) AS RowNum
                FROM LoginLogs
                {whereSql}
            ) t
            WHERE t.RowNum BETWEEN @offset + 1 AND @offset + @pageSize
            ORDER BY t.RowNum";

        using (var dataCmd = new SqlCommand(dataSql, connection))
        {
            foreach (var p in parameters) dataCmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
            dataCmd.Parameters.Add(new SqlParameter("@offset", offset));
            dataCmd.Parameters.Add(new SqlParameter("@pageSize", pageSize));

            using var reader = await dataCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        return (ids, totalCount);
    }
}
