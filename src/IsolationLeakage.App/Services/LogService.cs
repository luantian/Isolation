using System;
using System.IO;
using System.Text;
using Serilog;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 全局日志服务
/// </summary>
public static class LogService
{
    private static readonly object _lock = new();
    private static string? _logDirectory;
    private static string? _currentLogFile;

    /// <summary>
    /// 初始化日志服务
    /// </summary>
    public static void Initialize()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logDirectory = Path.Combine(appData, "IsolationLeakage", "Logs");
        Directory.CreateDirectory(_logDirectory);

        _currentLogFile = Path.Combine(_logDirectory, $"error_{DateTime.Now:yyyyMMdd}.log");
    }

    /// <summary>
    /// 记录异常
    /// </summary>
    public static void LogError(Exception ex, string? context = null)
    {
        lock (_lock)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("==================================================");
                sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                if (!string.IsNullOrWhiteSpace(context))
                    sb.AppendLine($"场景：{context}");
                sb.AppendLine($"异常类型：{ex.GetType().Name}");
                sb.AppendLine($"异常消息：{ex.Message}");
                sb.AppendLine($"堆栈跟踪：\n{ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"内部异常：{ex.InnerException.Message}");
                    sb.AppendLine($"内部堆栈：\n{ex.InnerException.StackTrace}");
                }
                sb.AppendLine();

                File.AppendAllText(_currentLogFile!, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志写入失败也不能抛异常
            }
        }
    }

    /// <summary>
    /// 记录信息
    /// </summary>
    public static void LogInfo(string message)
    {
        lock (_lock)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] INFO: {message}{Environment.NewLine}";
                File.AppendAllText(_currentLogFile!, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "写入日志文件失败");
            }
        }
    }

    /// <summary>
    /// 获取当前日志文件路径
    /// </summary>
    public static string GetLogFilePath() => _currentLogFile ?? string.Empty;

    /// <summary>
    /// 打开日志所在文件夹
    /// </summary>
    public static void OpenLogDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_logDirectory) && Directory.Exists(_logDirectory))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _logDirectory,
                UseShellExecute = true
            });
        }
    }
}
