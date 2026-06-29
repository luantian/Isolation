using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 试验对象路径节点表（树形结构：系统→贯穿件→阀门/其他部件）
/// </summary>
[Table("TestObjectPathNodes")]
public sealed class TestObjectPathNode : INotifyPropertyChanged
{
    private string _code = string.Empty;
    private string _name = string.Empty;

    [Key]
    [MaxLength(100)]
    public string Code
    {
        get => _code;
        set
        {
            if (_code != value)
            {
                _code = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    [Required]
    [MaxLength(200)]
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    [Required]
    public PathNodeType NodeType { get; set; }

    // 外键：所属机组
    [Required]
    [MaxLength(50)]
    public string UnitCode { get; set; } = string.Empty;

    // 自引用外键：父节点（系统节点为 null）
    [MaxLength(100)]
    public string? ParentCode { get; set; }

    // 阀门类型（仅阀门节点有值）
    [MaxLength(100)]
    public string? ValveType { get; set; }

    // 部件类型（仅其他部件节点有值）
    [MaxLength(100)]
    public string? ComponentType { get; set; }

    // 泄漏率限值（系统节点无值）
    [Column(TypeName = "decimal(18, 6)")]
    public decimal? LeakageLimit { get; set; }

    // 试验压力（系统/贯穿件节点无值）
    [Column(TypeName = "decimal(18, 6)")]
    public decimal? TestPressure { get; set; }

    // 关联的默认配方（用于该试验对象的泄漏试验）
    public int? DefaultRecipeId { get; set; }

    [MaxLength(1000)]
    public string? Remark { get; set; }

    // 导航属性：关联的默认配方
    public TestRecipe? DefaultRecipe { get; set; }

    public EnabledStatus Status { get; set; } = EnabledStatus.Enabled;

    public string StatusText => Status.ToText();
    public string NodeTypeText => NodeType.ToText();

    /// <summary>是否为叶子节点（阀门或其他部件不再有子节点）</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsLeafNode => NodeType is PathNodeType.Valve or PathNodeType.OtherComponent;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? UpdatedAt { get; set; }

    // 导航属性：所属机组
    public Unit? Unit { get; set; }

    // 导航属性：父节点
    public TestObjectPathNode? Parent { get; set; }

    // 导航属性：子节点
    public ObservableCollection<TestObjectPathNode> Children { get; set; } = [];

    // 导航属性：该对象的试验记录
    public ICollection<TestRecord> TestRecords { get; set; } = [];

    // 计算属性：显示名称
    [NotMapped]
    public string DisplayName => $"{Code}  {Name}";

    // 计算属性：是否有历史数据（用于删除保护）
    [NotMapped]
    public bool HasHistoricalData => TestRecords.Any();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
