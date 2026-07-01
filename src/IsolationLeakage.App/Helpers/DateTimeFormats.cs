namespace IsolationLeakage.App.Helpers;

/// <summary>
/// 全局日期时间格式常量。
/// 所有 UI 展示统一精确到秒，文件名/ID 等内部用途可省略秒。
/// </summary>
public static class DateTimeFormats
{
    // ==================== UI 展示用（精确到秒） ====================

    /// <summary>完整日期时间：yyyy-MM-dd HH:mm:ss</summary>
    public const string FullDateTime = "yyyy-MM-dd HH:mm:ss";

    /// <summary>仅时间（含秒）：HH:mm:ss</summary>
    public const string TimeWithSeconds = "HH:mm:ss";

    /// <summary>短日期+时间（含秒）：MM-dd HH:mm:ss</summary>
    public const string ShortDateFullTime = "MM-dd HH:mm:ss";

    /// <summary>仅日期+时间（含秒），空格分隔：yyyy-MM-dd HH:mm:ss</summary>
    public const string DisplayDateTime = "yyyy-MM-dd HH:mm:ss";

    /// <summary>仅日期：yyyy-MM-dd</summary>
    public const string DisplayDate = "yyyy-MM-dd";

    /// <summary>仅时间：HH:mm</summary>
    public const string DisplayTime = "HH:mm";

    // ==================== XAML StringFormat 用 ====================

    /// <summary>XAML 绑定格式：完整日期时间</summary>
    public const string XamlFullDateTime = "yyyy-MM-dd HH:mm:ss";

    /// <summary>XAML 绑定格式：短日期+时间</summary>
    public const string XamlShortDateFullTime = "MM-dd HH:mm:ss";

    // ==================== 文件名 / ID 用（可省略秒） ====================

    /// <summary>文件名时间戳：yyyyMMdd_HHmmss</summary>
    public const string FileNameTimestamp = "yyyyMMdd_HHmmss";

    /// <summary>紧凑ID时间戳：yyyyMMddHHmmss</summary>
    public const string CompactIdTimestamp = "yyyyMMddHHmmss";

    /// <summary>日志文件名日期：yyyyMMdd</summary>
    public const string LogFileDate = "yyyyMMdd";

    /// <summary>高精度日志时间：HH:mm:ss.fff</summary>
    public const string LogEntryTime = "HH:mm:ss.fff";

    // ==================== 辅助方法 ====================

    /// <summary>安全格式化 DateTime?，null 时返回空字符串</summary>
    public static string Format(DateTime? dateTime, string format = FullDateTime)
        => dateTime?.ToString(format) ?? string.Empty;

    /// <summary>安全格式化 DateTime，统一输出</summary>
    public static string Format(DateTime dateTime, string format = FullDateTime)
        => dateTime.ToString(format);
}
