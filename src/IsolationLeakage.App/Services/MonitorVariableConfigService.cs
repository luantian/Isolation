using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 实时监视变量配置管理服务
/// 支持变量的增删改查和排序
/// </summary>
public class MonitorVariableConfigService
{
    private readonly AppDbContext _context;

    public MonitorVariableConfigService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取所有启用的变量配置（按排序顺序）
    /// </summary>
    public async Task<List<MonitorVariableConfig>> GetEnabledVariablesAsync()
    {
        return await _context.MonitorVariableConfigs
            .Where(v => v.IsEnabled)
            .OrderBy(v => v.SortOrder)
            .ToListAsync();
    }

    /// <summary>
    /// 获取所有变量配置（包括禁用的）
    /// </summary>
    public async Task<List<MonitorVariableConfig>> GetAllVariablesAsync()
    {
        return await _context.MonitorVariableConfigs
            .OrderBy(v => v.SortOrder)
            .ToListAsync();
    }

    /// <summary>
    /// 根据 ID 获取变量配置
    /// </summary>
    public async Task<MonitorVariableConfig?> GetByIdAsync(int id)
    {
        return await _context.MonitorVariableConfigs
            .FirstOrDefaultAsync(v => v.Id == id);
    }

    /// <summary>
    /// 创建新的变量配置
    /// </summary>
    public async Task<MonitorVariableConfig> CreateAsync(MonitorVariableConfig config)
    {
        // 检查变量名是否已存在
        var exists = await _context.MonitorVariableConfigs
            .AnyAsync(v => v.VariableName == config.VariableName);
        if (exists)
        {
            throw new InvalidOperationException($"变量名「{config.VariableName}」已存在");
        }

        // 如果没有设置排序顺序，自动追加到最后
        if (config.SortOrder == 0)
        {
            var maxSort = await _context.MonitorVariableConfigs
                .MaxAsync(v => (int?)v.SortOrder) ?? 0;
            config.SortOrder = maxSort + 1;
        }

        config.CreatedAt = DateTime.Now;
        _context.MonitorVariableConfigs.Add(config);
        await _context.SaveChangesAsync();

        return config;
    }

    /// <summary>
    /// 更新变量配置
    /// </summary>
    public async Task UpdateAsync(MonitorVariableConfig config)
    {
        var existing = await _context.MonitorVariableConfigs
            .FirstOrDefaultAsync(v => v.Id == config.Id);

        if (existing == null)
        {
            throw new InvalidOperationException($"变量配置 ID={config.Id} 不存在");
        }

        // 检查变量名是否与其他记录冲突
        var nameConflict = await _context.MonitorVariableConfigs
            .AnyAsync(v => v.VariableName == config.VariableName && v.Id != config.Id);
        if (nameConflict)
        {
            throw new InvalidOperationException($"变量名「{config.VariableName}」已被其他变量使用");
        }

        // 更新字段
        existing.VariableName = config.VariableName;
        existing.RegisterAddress = config.RegisterAddress;
        existing.SiemensAddress = config.SiemensAddress;
        existing.DataType = config.DataType;
        existing.Unit = config.Unit;
        existing.CurveChannel = config.CurveChannel;
        existing.MinDisplay = config.MinDisplay;
        existing.MaxDisplay = config.MaxDisplay;
        existing.SortOrder = config.SortOrder;
        existing.IsEnabled = config.IsEnabled;
        existing.Remark = config.Remark;
        existing.UpdatedAt = DateTime.Now;

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 删除变量配置
    /// </summary>
    public async Task DeleteAsync(int id)
    {
        var config = await _context.MonitorVariableConfigs
            .FirstOrDefaultAsync(v => v.Id == id);

        if (config == null)
        {
            throw new InvalidOperationException($"变量配置 ID={id} 不存在");
        }

        _context.MonitorVariableConfigs.Remove(config);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 初始化默认变量（如果数据库为空）
    /// 用于首次运行时插入默认的五个变量（严格按照 plc-registers.json 中的配置）
    /// 寄存器地址与 MockPlcConnection 的模拟数据地址一致
    /// </summary>
    public async Task SeedDefaultVariablesAsync()
    {
        var count = await _context.MonitorVariableConfigs.CountAsync();
        if (count > 0) return;

        var defaults = new[]
        {
            new MonitorVariableConfig
            {
                VariableName = "压力P1",
                RegisterAddress = 512,
                SiemensAddress = "DB15.0",
                DataType = "real",
                Unit = "MPa",
                CurveChannel = "Pressure",
                MinDisplay = 0,
                MaxDisplay = 10,
                SortOrder = 1,
                IsEnabled = true,
                Remark = "默认压力P1变量"
            },
            new MonitorVariableConfig
            {
                VariableName = "温度T",
                RegisterAddress = 500,
                SiemensAddress = "DB15.4",
                DataType = "real",
                Unit = "℃",
                CurveChannel = "Temp",
                MinDisplay = -20,
                MaxDisplay = 100,
                SortOrder = 2,
                IsEnabled = true,
                Remark = "默认温度T变量"
            },
            new MonitorVariableConfig
            {
                VariableName = "压力P2",
                RegisterAddress = 504,
                SiemensAddress = "DB15.8",
                DataType = "real",
                Unit = "MPa",
                CurveChannel = "Pressure2",
                MinDisplay = 0,
                MaxDisplay = 10,
                SortOrder = 3,
                IsEnabled = true,
                Remark = "默认压力P2变量"
            },
            new MonitorVariableConfig
            {
                VariableName = "流量M1",
                RegisterAddress = 804,
                SiemensAddress = "DB15.12",
                DataType = "uint",
                Unit = "L/min",
                CurveChannel = "Flow",
                MinDisplay = 0,
                MaxDisplay = 100,
                SortOrder = 4,
                IsEnabled = true,
                Remark = "默认流量M1变量"
            },
            new MonitorVariableConfig
            {
                VariableName = "流量M2",
                RegisterAddress = 806,
                SiemensAddress = "DB15.14",
                DataType = "uint",
                Unit = "L/min",
                CurveChannel = "Flow2",
                MinDisplay = 0,
                MaxDisplay = 100,
                SortOrder = 5,
                IsEnabled = true,
                Remark = "默认流量M2变量"
            }
        };

        _context.MonitorVariableConfigs.AddRange(defaults);
        await _context.SaveChangesAsync();
    }
}
