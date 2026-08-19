using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 试验记录表
/// </summary>
[Table("TestRecords")]
public sealed class TestRecord : INotifyPropertyChanged
{
    [Key]
    [MaxLength(50)]
    public string RecordCode { get; set; } = string.Empty;

    /// <summary>
    /// 列表序号（非数据库字段，仅用于 UI 显示）
    /// </summary>
    [NotMapped]
    public int RowNumber { get; set; }

    private bool _isSelected;
    /// <summary>
    /// 是否选中（非数据库字段，仅用于 UI 批量操作）
    /// </summary>
    [NotMapped]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    // 外键：所属项目
    [Required]
    [MaxLength(50)]
    public string ProjectCode { get; set; } = string.Empty;

    // 外键：所属机组
    [Required]
    [MaxLength(50)]
    public string UnitCode { get; set; } = string.Empty;

    // 外键：试验对象（阀门/部件）
    [Required]
    [MaxLength(100)]
    public string ObjectCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string ObjectName { get; set; } = string.Empty;

    public PathNodeType ObjectType { get; set; }

    // 外键：测量装置
    [Required]
    [MaxLength(50)]
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>
    /// 外键：使用的试验配方（可选）
    /// </summary>
    public int? TestRecipeId { get; set; }

    /// <summary>
    /// 配方参数快照（JSON，试验时的配方参数，永久保留不随配方修改而变化）
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string? RecipeSnapshotJson { get; set; }

    /// <summary>
    /// 试验时使用的配方版本号
    /// </summary>
    public int? RecipeVersionNumber { get; set; }

    [MaxLength(500)]
    public string? DataPackageName { get; set; }

    public DateTime TestTime { get; set; }

    public DateTime ImportTime { get; set; }

    [MaxLength(100)]
    public string Operator { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18, 6)")]
    public decimal TestPressure { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal LeakageLimit { get; set; }

    [Column(TypeName = "decimal(18, 6)")]
    public decimal FinalLeakageRate { get; set; }

    public TestResult Result { get; set; }

    /// <summary>
    /// 结果中文显示（Unknown=未知，不能显示为"不合格"误导验收）
    /// </summary>
    [NotMapped]
    public string ResultText => Result switch
    {
        TestResult.Pass => "合格",
        TestResult.Fail => "不合格",
        _ => "未知",
    };

    [MaxLength(2000)]
    public string? Remark { get; set; }

    [MaxLength(1000)]
    public string? StepSummary { get; set; }

    [MaxLength(1000)]
    public string? ResultFieldSummary { get; set; }

    [MaxLength(1000)]
    public string? ProcessChannelSummary { get; set; }

    /// <summary>
    /// 修改前的旧值快照（JSON 格式，记录关联试验路径前的 LeakageLimit、Result 等）
    /// 用于追溯和恢复原始数据
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string? PreviousValuesJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // 显示属性（通过导航属性获取）
    public string? ProjectName => Project?.Name;
    public string? UnitName => Unit?.Name;

    /// <summary>
    /// 配方名称（用于列表显示，去掉"配方ABC-"前缀）
    /// </summary>
    public string? RecipeName
    {
        get
        {
            if (TestRecipe?.RecipeName == null) return "未关联试验路径";
            var name = TestRecipe.RecipeName;
            var dashIndex = name.IndexOf('-');
            return dashIndex > 0 ? name.Substring(dashIndex + 1).Trim() : name;
        }
    }

    // 导航属性：所属项目
    public Project? Project { get; set; }

    // 导航属性：所属机组
    public Unit? Unit { get; set; }

    // 导航属性：试验对象
    public TestObjectPathNode? TestObject { get; set; }

    // 导航属性：测量装置
    public MeasurementDevice? Device { get; set; }

    // 导航属性：过程数据（一对一）
    public TestProcessData? ProcessData { get; set; }

    // 导航属性：使用的配方
    public TestRecipe? TestRecipe { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
