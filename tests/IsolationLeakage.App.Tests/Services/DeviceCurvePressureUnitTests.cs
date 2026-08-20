using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using IsolationLeakage.App.Models.Database;
using IsolationLeakage.App.Services;
using Xunit;

namespace IsolationLeakage.App.Tests.Services;

/// <summary>
/// 装置 CSV 导入的压力量纲测试：
/// 原始压力值为 MPa，入库 ChannelsJson 时数值 ×1000 且单位标 kPa，
/// 与实时采集链路入库量纲一致（见 PressureUnitConverter）。
/// 流量/温度等非压力通道不做换算；自定义通道（未知单位）不做隐式换算。
/// </summary>
public sealed class DeviceCurvePressureUnitTests
{
    [Fact]
    public void BuildProcessData_ScalesKnownPressureChannelsToKPa()
    {
        var points = new List<ProcessDataPoint>
        {
            new()
            {
                SampleTime = new DateTime(2026, 8, 20, 10, 0, 0),
                Channels = { ["Pressure"] = 0.5, ["Pressure2"] = 0.4, ["Flow"] = 10, ["Temp"] = 25 },
            },
            new()
            {
                SampleTime = new DateTime(2026, 8, 20, 10, 0, 1),
                Channels = { ["Pressure"] = 1.5, ["Pressure2"] = 1.2, ["Flow"] = 20, ["Temp"] = 26 },
            },
        };

        var data = InvokeBuildProcessData(points);
        var channels = JsonSerializer.Deserialize<Dictionary<string, ChannelData>>(data.ChannelsJson!)!;

        channels["Pressure"].Unit.Should().Be("kPa");
        channels["Pressure"].Data.Should().BeEquivalentTo(new[] { 500.0, 1500.0 },
            "装置 CSV 压力原始值 MPa，入库应 ×1000 对齐实时链路量纲");
        channels["Pressure2"].Unit.Should().Be("kPa");
        channels["Pressure2"].Data.Should().BeEquivalentTo(new[] { 400.0, 1200.0 });

        channels["Flow"].Unit.Should().Be("Nml/min");
        channels["Flow"].Data.Should().BeEquivalentTo(new[] { 10.0, 20.0 }, "流量不换算");
        channels["Temp"].Data.Should().BeEquivalentTo(new[] { 25.0, 26.0 }, "温度不换算");
    }

    [Fact]
    public void BuildProcessData_DoesNotScaleCustomChannels()
    {
        var points = new List<ProcessDataPoint>
        {
            new()
            {
                SampleTime = new DateTime(2026, 8, 20, 10, 0, 0),
                // 自定义列：名称含"压力"但单位未知，不得隐式换算
                Channels = { ["大气压力"] = 101.3 },
            },
        };

        var data = InvokeBuildProcessData(points);
        var channels = JsonSerializer.Deserialize<Dictionary<string, ChannelData>>(data.ChannelsJson!)!;

        channels["大气压力"].Data.Should().BeEquivalentTo(new[] { 101.3 },
            "自定义通道单位未知，不做 MPa→kPa 隐式换算");
        channels["大气压力"].Unit.Should().BeEmpty("自定义通道无已知单位元数据");
    }

    private static TestProcessData InvokeBuildProcessData(List<ProcessDataPoint> points)
    {
        var method = typeof(DataUploadService).GetMethod("BuildProcessData",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (TestProcessData)method.Invoke(null, new object[] { points })!;
    }
}
