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
    /// 解析数据包文件（JSON 或文本格式）
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
        var content = await File.ReadAllTextAsync(filePath);

        if (extension == ".json")
        {
            return await ParseJsonAsync(content);
        }
        else
        {
            return await ParseTextAsync(content);
        }
    }

    /// <summary>
    /// 校验并上传解析后的数据
    /// </summary>
    /// <param name="parsedData">解析后的数据包</param>
    /// <param name="recordCode">记录编号（唯一标识）</param>
    /// <param name="projectCode">项目编码</param>
    /// <param name="unitCode">机组编码</param>
    /// <param name="operatorName">操作员</param>
    /// <returns>创建的试验记录</returns>
    public async Task<TestRecord> ValidateAndUploadAsync(
        ParsedDataPackage parsedData,
        string recordCode,
        string projectCode,
        string unitCode,
        string operatorName)
    {
        // 1. 校验必填字段
        ValidateRequiredFields(parsedData, recordCode, projectCode, unitCode, operatorName);

        // 2. 检查重复记录（相同对象 + 相同时间 = 重复）
        await CheckDuplicateAsync(parsedData.ObjectCode!, parsedData.TestTime);

        // 3. 构建试验记录
        // 从试验对象路径节点读取泄漏率限值
        decimal leakageLimit = 0;
        try
        {
            var node = await AppServices.DbContext.TestObjectPathNodes
                .AsNoTracking()
                .FirstOrDefaultAsync(n => n.Code == parsedData.ObjectCode);
            if (node?.LeakageLimit.HasValue == true)
                leakageLimit = node.LeakageLimit.Value;
        }
        catch { /* 查询失败时使用默认值 0 */ }

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
    /// 构建过程数据对象
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
        var temps = dataPoints.Select(p => p.Temp).ToArray();

        // 序列化曲线数据
        var pressureCurveJson = JsonSerializer.Serialize(pressures);
        var flowCurveJson = JsonSerializer.Serialize(flows);
        var tempCurveJson = JsonSerializer.Serialize(temps);

        // 计算统计值
        var processData = new TestProcessData
        {
            PressureCurveJson = pressureCurveJson,
            FlowCurveJson = flowCurveJson,
            TempCurveJson = tempCurveJson,
            PressureMin = pressures.Min(),
            PressureMax = pressures.Max(),
            FlowMin = flows.Min(),
            FlowMax = flows.Max(),
            TempMin = temps.Min(),
            TempMax = temps.Max(),
            CreatedAt = DateTime.Now,
        };

        return processData;
    }

    #endregion
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
/// 过程数据点
/// </summary>
public sealed class ProcessDataPoint
{
    /// <summary>
    /// 时间点
    /// </summary>
    public TimeSpan Time { get; set; }

    /// <summary>
    /// 压力值
    /// </summary>
    public decimal Pressure { get; set; }

    /// <summary>
    /// 流量值
    /// </summary>
    public decimal Flow { get; set; }

    /// <summary>
    /// 温度值
    /// </summary>
    public decimal Temp { get; set; }
}
