using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace IsolationLeakage.App.Models.Database;

/// <summary>
/// 项目表
/// </summary>
[Table("Projects")]
public sealed class Project : INotifyPropertyChanged
{
    private string _code = string.Empty;
    private string _name = string.Empty;
    private EnabledStatus _status = EnabledStatus.Enabled;
    private string? _remark;
    private DateTime _createdAt = DateTime.Now;
    private DateTime? _updatedAt;

    [Key]
    [MaxLength(50)]
    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value);
    }

    [Required]
    [MaxLength(200)]
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public EnabledStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => Status.ToText();

    [MaxLength(1000)]
    public string? Remark
    {
        get => _remark;
        set => SetProperty(ref _remark, value);
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => SetProperty(ref _createdAt, value);
    }

    public DateTime? UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

    // 导航属性：项目下的机组
    public ICollection<Unit> Units { get; set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
