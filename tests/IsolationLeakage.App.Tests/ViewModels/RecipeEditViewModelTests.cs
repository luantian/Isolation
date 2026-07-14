using FluentAssertions;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.ViewModels;
using Xunit;

namespace IsolationLeakage.App.Tests.ViewModels;

/// <summary>
/// RecipeEditViewModel 验证和转换逻辑测试
/// 注：Validate() 中调用了 MessageBox.Show，需要 WPF 消息循环。
/// 这里只测试纯逻辑部分（ToEntity / LoadFromEntity），验证逻辑通过数据间接测试。
/// </summary>
public class RecipeEditViewModelTests
{
    [Fact]
    public void ToEntity_TrimStrings()
    {
        var vm = new RecipeEditViewModel
        {
            RecipeName = "  AA  ",
            System = " CAS ",
            ValveNo = " FBDF ",
            Remark = " 备注 ",
        };

        var entity = vm.ToEntity();

        entity.RecipeName.Should().Be("AA");
        entity.System.Should().Be("CAS");
        entity.ValveNo.Should().Be("FBDF");
        entity.Remark.Should().Be("备注");
    }

    [Fact]
    public void ToEntity_NullRemark_PreservedAsNull()
    {
        var vm = new RecipeEditViewModel
        {
            RecipeName = "AA",
            Remark = null,
        };

        var entity = vm.ToEntity();
        entity.Remark.Should().BeNull();
    }

    [Fact]
    public void LoadFromEntity_MapsAllFields()
    {
        var recipe = new TestRecipe
        {
            Id = 42,
            RecipeName = "TestRecipe",
            SequenceNo = 5,
            System = "CAS",
            PenetrationDiameter = 10.2m,
            ValveNo = "FBDF",
            ValveNominalDiameter = 15.5m,
            LeakageLimit = 200m,
            PrechargePressureP2 = 0.123m,
            IsEnabled = false,
            SortOrder = 3,
            Remark = "备注",
        };

        var vm = new RecipeEditViewModel();
        vm.LoadFromEntity(recipe);

        vm.Id.Should().Be(42);
        vm.RecipeName.Should().Be("TestRecipe");
        vm.SequenceNo.Should().Be(5);
        vm.System.Should().Be("CAS");
        vm.PenetrationDiameter.Should().Be(10.2m);
        vm.ValveNo.Should().Be("FBDF");
        vm.ValveNominalDiameter.Should().Be(15.5m);
        vm.LeakageLimit.Should().Be(200m);
        vm.PrechargePressureP2.Should().Be(0.123m);
        vm.IsEnabled.Should().BeFalse();
        vm.SortOrder.Should().Be(3);
        vm.Remark.Should().Be("备注");
    }

    [Fact]
    public void LoadFromEntity_ThenToEntity_Roundtrip()
    {
        var original = new TestRecipe
        {
            Id = 1,
            RecipeName = "Roundtrip",
            SequenceNo = 7,
            System = "CAM",
            PenetrationDiameter = 20.0m,
            ValveNo = "V001",
            ValveNominalDiameter = 25.0m,
            LeakageLimit = 100m,
            PrechargePressureP2 = 0.5m,
            IsEnabled = true,
            SortOrder = 10,
            Remark = "测试",
        };

        var vm = new RecipeEditViewModel();
        vm.LoadFromEntity(original);
        var roundtripped = vm.ToEntity();

        roundtripped.Id.Should().Be(original.Id);
        roundtripped.RecipeName.Should().Be(original.RecipeName);
        roundtripped.SequenceNo.Should().Be(original.SequenceNo);
        roundtripped.System.Should().Be(original.System);
        roundtripped.PenetrationDiameter.Should().Be(original.PenetrationDiameter);
        roundtripped.ValveNo.Should().Be(original.ValveNo);
        roundtripped.ValveNominalDiameter.Should().Be(original.ValveNominalDiameter);
        roundtripped.LeakageLimit.Should().Be(original.LeakageLimit);
        roundtripped.PrechargePressureP2.Should().Be(original.PrechargePressureP2);
        roundtripped.IsEnabled.Should().Be(original.IsEnabled);
        roundtripped.SortOrder.Should().Be(original.SortOrder);
        roundtripped.Remark.Should().Be(original.Remark);
    }

    [Fact]
    public void IsEditMode_TrueWhenIdGreaterThanZero()
    {
        var vm = new RecipeEditViewModel { Id = 1 };
        vm.IsEditMode.Should().BeTrue();
        vm.Title.Should().Be("编辑配方");
    }

    [Fact]
    public void IsEditMode_FalseWhenIdIsZero()
    {
        var vm = new RecipeEditViewModel { Id = 0 };
        vm.IsEditMode.Should().BeFalse();
        vm.Title.Should().Be("新增配方");
    }
}
