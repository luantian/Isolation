namespace IsolationLeakage.App.Helpers;

/// <summary>
/// 压力单位换算帮助类（显示层换算方案）：
/// 数据库、装置 CSV、PLC 原始读数、任务下发协议全部保持 MPa；
/// 仅在"显示"（×1000 标 kPa）与"输入"（÷1000 存 MPa）两端换算。
/// 曲线通道数据以 kPa 入 ChannelsJson（Unit 元数据随行，旧记录 MPa 自动按 MPa 显示）。
/// </summary>
public static class PressureUnitConverter
{
    /// <summary>显示单位标签</summary>
    public const string DisplayUnit = "kPa";

    /// <summary>存储单位标签（DB/协议层）</summary>
    public const string StorageUnit = "MPa";

    /// <summary>
    /// 判断是否压力类通道（按曲线通道标识关键字，语义与实时监视的曲线分组一致）。
    /// 用于决定该通道的值是否按 kPa 显示。
    /// </summary>
    public static bool IsPressureChannel(string? curveChannel)
    {
        var s = (curveChannel ?? string.Empty).ToLowerInvariant();
        return s.Contains("pressure") || s.Contains("压力");
    }

    /// <summary>存储值(MPa) → 显示值(kPa)</summary>
    public static double ToDisplay(double megaPascals) => megaPascals * 1000.0;

    /// <summary>显示值(kPa) → 存储值(MPa)</summary>
    public static double ToStorage(double kiloPascals) => kiloPascals / 1000.0;

    /// <summary>存储值(MPa) → 显示值(kPa)（decimal 版，保留精度）</summary>
    public static decimal ToDisplay(decimal megaPascals) => megaPascals * 1000m;

    /// <summary>显示值(kPa) → 存储值(MPa)（decimal 版，保留精度）</summary>
    public static decimal ToStorage(decimal kiloPascals) => kiloPascals / 1000m;

    /// <summary>
    /// 按单位标签换算数据：单位为 kPa 且数据是存储值(MPa)时 ×1000；
    /// 其他单位原样返回。用于导出/入库时让数值与单位标签一致。
    /// </summary>
    public static double[] ScaleToUnit(double[] data, string? unit)
    {
        if (data.Length == 0 || !IsKPa(unit)) return data;
        var result = new double[data.Length];
        for (int i = 0; i < data.Length; i++) result[i] = data[i] * 1000.0;
        return result;
    }

    /// <summary>单位标签是否为 kPa（忽略大小写）</summary>
    public static bool IsKPa(string? unit)
        => string.Equals(unit?.Trim(), DisplayUnit, StringComparison.OrdinalIgnoreCase);
}
