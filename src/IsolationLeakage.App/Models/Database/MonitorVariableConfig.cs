using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 实时监视变量配置表
/// 支持用户自定义添加、编辑、删除监视变量
/// </summary>
[Table("MonitorVariableConfig")]
public sealed class MonitorVariableConfig
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// 变量名称（如：试验压力、温度等）
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string VariableName { get; set; } = string.Empty;

    /// <summary>
    /// Modbus 寄存器地址
    /// </summary>
    public int RegisterAddress { get; set; }

    /// <summary>
    /// 西门子 S7 地址格式，如 DB15.DBD0
    /// </summary>
    [MaxLength(50)]
    public string SiemensAddress { get; set; } = string.Empty;

    /// <summary>
    /// 数据类型（double, int, float, bool 等）
    /// </summary>
    [MaxLength(20)]
    public string DataType { get; set; } = "double";

    /// <summary>
    /// 单位（如：MPa、℃、Nml/min 等）
    /// </summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// 关联的曲线通道（Pressure/Flow/Temp 等，为空则不显示曲线）
    /// </summary>
    [MaxLength(20)]
    public string? CurveChannel { get; set; }

    /// <summary>
    /// 显示最小值（用于 Y 轴自动缩放参考）
    /// </summary>
    public double MinDisplay { get; set; }

    /// <summary>
    /// 显示最大值（用于 Y 轴自动缩放参考）
    /// </summary>
    public double MaxDisplay { get; set; }

    /// <summary>
    /// 排序顺序（数字越小越靠前）
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 备注
    /// </summary>
    [MaxLength(200)]
    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }
}
