using System.Security.Cryptography;
using System.Text;

namespace IsolationLeakage.App.Security;

/// <summary>
/// 连接字符串加密器（使用 Windows DPAPI）
/// 加密后的数据只能在本机解密，确保密码安全存储
/// </summary>
public static class ConnectionStringEncryptor
{
    /// <summary>
    /// 加密前缀标识（用于区分加密和未加密的连接字符串）
    /// </summary>
    private const string EncryptedPrefix = "ENC:";

    /// <summary>
    /// 加密连接字符串
    /// </summary>
    /// <param name="plainText">明文连接字符串</param>
    /// <returns>加密后的连接字符串（Base64 格式，带 ENC: 前缀）</returns>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return plainText;

        // 如果已经是加密的，不再重复加密
        if (plainText.StartsWith(EncryptedPrefix))
            return plainText;

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encryptedBytes = ProtectedData.Protect(
                plainBytes,
                null, // 可选的额外熵（null 表示不使用）
                DataProtectionScope.LocalMachine // 使用机器密钥，只有本机可以解密
            );

            string base64 = Convert.ToBase64String(encryptedBytes);
            return $"{EncryptedPrefix}{base64}";
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "连接字符串加密失败");
            throw new InvalidOperationException("连接字符串加密失败，请确保在 Windows 环境下运行", ex);
        }
    }

    /// <summary>
    /// 解密连接字符串
    /// </summary>
    /// <param name="encryptedText">加密的连接字符串（带 ENC: 前缀）</param>
    /// <returns>明文连接字符串</returns>
    public static string Decrypt(string encryptedText)
    {
        if (string.IsNullOrWhiteSpace(encryptedText))
            return encryptedText;

        // 如果不是加密的，直接返回
        if (!encryptedText.StartsWith(EncryptedPrefix))
            return encryptedText;

        try
        {
            string base64 = encryptedText[EncryptedPrefix.Length..];
            byte[] encryptedBytes = Convert.FromBase64String(base64);
            byte[] plainBytes = ProtectedData.Unprotect(
                encryptedBytes,
                null,
                DataProtectionScope.LocalMachine
            );

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "连接字符串解密失败");
            throw new InvalidOperationException(
                "连接字符串解密失败！可能的原因：\n" +
                "1. 此连接字符串是在其他机器上加密的\n" +
                "2. 当前用户没有解密权限\n" +
                "3. 连接字符串已损坏",
                ex);
        }
    }

    /// <summary>
    /// 判断连接字符串是否已加密
    /// </summary>
    public static bool IsEncrypted(string connectionString)
    {
        return !string.IsNullOrWhiteSpace(connectionString) &&
               connectionString.StartsWith(EncryptedPrefix);
    }

    /// <summary>
    /// 确保连接字符串已加密（如果未加密则加密）
    /// </summary>
    public static string EnsureEncrypted(string connectionString)
    {
        if (IsEncrypted(connectionString))
            return connectionString;

        return Encrypt(connectionString);
    }

    /// <summary>
    /// 确保连接字符串已解密（如果已加密则解密）
    /// </summary>
    public static string EnsureDecrypted(string connectionString)
    {
        if (!IsEncrypted(connectionString))
            return connectionString;

        return Decrypt(connectionString);
    }
}
