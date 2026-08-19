using FluentAssertions;
using IsolationLeakage.App.Communication.Models;
using IsolationLeakage.App.Configuration;
using Xunit;

namespace IsolationLeakage.App.Tests.Services;

/// <summary>
/// plc-registers.json 归一化测试：
/// - 旧单装置格式（无 Devices）→ 包装为单装置 DEFAULT（连接/变量/采样周期透传）
/// - 有 Devices → 剔空 DeviceCode、SampleIntervalMs<=0 沿用全局值、同名去重
/// </summary>
public sealed class PlcRegistersNormalizationTests
{
    private static PlcVariableConfig MakeVar(string code) => new()
    {
        VariableCode = code,
        VariableName = code,
        RegisterAddress = 512,
        DataType = "real",
    };

    [Fact]
    public void LegacySection_WrappedAsSingleDefaultDevice()
    {
        var connection = new PlcConnectionConfig { PlcType = "SiemensS7", IpAddress = "10.0.0.1", Port = 102 };
        var variables = new List<PlcVariableConfig> { MakeVar("PLC_PRESSURE_P1"), MakeVar("PLC_TEMP") };
        var section = new PlcRegistersSection
        {
            Connection = connection,
            Variables = variables,
            SampleIntervalMs = 500,
        };

        AppConfiguration.NormalizeDevices(section);

        section.Devices.Should().HaveCount(1);
        var device = section.Devices![0];
        device.DeviceCode.Should().Be("DEFAULT");
        device.Connection.Should().BeSameAs(connection);
        device.Variables.Should().BeSameAs(variables);
        device.SampleIntervalMs.Should().Be(500);
    }

    [Fact]
    public void DevicesSection_KeptAsIs_WithIntervalFallback()
    {
        var section = new PlcRegistersSection
        {
            SampleIntervalMs = 2000,
            Devices =
            [
                new PlcDeviceConfig
                {
                    DeviceCode = "DEV-A",
                    Connection = new PlcConnectionConfig { IpAddress = "10.0.0.1" },
                    Variables = [MakeVar("V1")],
                    SampleIntervalMs = 0, // 沿用全局 2000
                },
                new PlcDeviceConfig
                {
                    DeviceCode = "DEV-B",
                    Connection = new PlcConnectionConfig { IpAddress = "10.0.0.2" },
                    Variables = [MakeVar("V1")], // 不同装置允许同名变量
                    SampleIntervalMs = 100,
                },
            ],
        };

        AppConfiguration.NormalizeDevices(section);

        section.Devices.Should().HaveCount(2);
        section.Devices[0].DeviceCode.Should().Be("DEV-A");
        section.Devices[0].SampleIntervalMs.Should().Be(2000);
        section.Devices[1].SampleIntervalMs.Should().Be(100);
    }

    [Fact]
    public void EmptyDeviceCode_FilledWithDefault_AndDuplicatesRemoved()
    {
        var section = new PlcRegistersSection
        {
            Devices =
            [
                new PlcDeviceConfig { DeviceCode = "", Variables = [MakeVar("V1")] },
                new PlcDeviceConfig { DeviceCode = "DEV-A", Variables = [MakeVar("V2")] },
                new PlcDeviceConfig { DeviceCode = "DEV-A", Variables = [MakeVar("V3")] }, // 同名重复
            ],
        };

        AppConfiguration.NormalizeDevices(section);

        section.Devices.Should().HaveCount(2);
        section.Devices.Should().OnlyContain(d => !string.IsNullOrWhiteSpace(d.DeviceCode));
        // 重复 DEV-A 保留第一个（V2）
        section.Devices.First(d => d.DeviceCode == "DEV-A").Variables.Single().VariableCode.Should().Be("V2");
    }

    [Fact]
    public void EmptySection_GetsSingleDefaultDevice()
    {
        var section = new PlcRegistersSection();

        AppConfiguration.NormalizeDevices(section);

        section.Devices.Should().HaveCount(1);
        section.Devices![0].DeviceCode.Should().Be("DEFAULT");
        section.Devices[0].SampleIntervalMs.Should().BeGreaterThan(0);
    }
}
