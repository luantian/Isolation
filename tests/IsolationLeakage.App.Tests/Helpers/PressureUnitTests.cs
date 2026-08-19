using FluentAssertions;
using IsolationLeakage.App.Helpers;
using IsolationLeakage.App.ViewModels;
using Xunit;

namespace IsolationLeakage.App.Tests.Helpers;

/// <summary>
/// 压力单位显示层换算测试（DB/CSV/PLC/协议保持 MPa；显示×1000、输入÷1000）。
/// </summary>
public sealed class PressureUnitTests
{
    [Theory]
    [InlineData("Pressure", true)]
    [InlineData("pressure", true)]
    [InlineData("Pressure2", true)]
    [InlineData("压力", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Flow", false)]
    [InlineData("Temp", false)]
    public void IsPressureChannel_MatchesKeywords(string? channel, bool expected)
    {
        PressureUnitConverter.IsPressureChannel(channel).Should().Be(expected);
    }

    [Fact]
    public void ToDisplay_ToStorage_RoundTrip()
    {
        const decimal mpa = 0.344m;
        var kpa = PressureUnitConverter.ToDisplay(mpa);
        kpa.Should().Be(344m);
        PressureUnitConverter.ToStorage(kpa).Should().Be(mpa);

        PressureUnitConverter.ToDisplay(1.5).Should().Be(1500.0);
        PressureUnitConverter.ToStorage(1500.0).Should().Be(1.5);
    }

    [Fact]
    public void ScaleToUnit_OnlyScalesKPa()
    {
        var raw = new[] { 0.1, 0.344, 1.5 };

        PressureUnitConverter.ScaleToUnit(raw, "kPa").Should().Equal(100, 344, 1500);
        PressureUnitConverter.ScaleToUnit(raw, "KPA").Should().Equal(100, 344, 1500);
        // 非 kPa（含旧记录的 MPa）原样返回
        PressureUnitConverter.ScaleToUnit(raw, "MPa").Should().BeSameAs(raw);
        PressureUnitConverter.ScaleToUnit(raw, null).Should().BeSameAs(raw);
    }

    [Fact]
    public void RecipeEdit_PrechargePressureP2Text_ConvertsBothWays()
    {
        var vm = new RecipeEditViewModel();

        // MPa → kPa 文本
        vm.PrechargePressureP2 = 0.321m;
        vm.PrechargePressureP2Text.Should().Be("321");

        // kPa 文本 → MPa 存储
        vm.PrechargePressureP2Text = " 344 ";
        vm.PrechargePressureP2.Should().Be(0.344m);

        // 空文本 → 0
        vm.PrechargePressureP2Text = "";
        vm.PrechargePressureP2.Should().Be(0m);
    }
}
