using System;
using IsolationLeakage.App.Security;

namespace IsolationLeakage.App.Tools;

/// <summary>
/// 连接字符串加密工具（命令行）
/// 用法：dotnet run -- encrypt-connection-string "Server=...;Password=..."
/// </summary>
public class EncryptConnectionStringTool
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || args[0] != "encrypt-connection-string")
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  dotnet run -- encrypt-connection-string \"Server=...;Password=...\"");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  dotnet run -- encrypt-connection-string \"Server=192.168.1.100\\SQLEXPRESS;Database=IsolationLeakageDb;User Id=sa;Password=Admin@123;\"");
            Console.WriteLine();
            Console.WriteLine("输出：");
            Console.WriteLine("  加密后的连接字符串（以 ENC: 开头）");
            Console.WriteLine();
            Console.WriteLine("注意：");
            Console.WriteLine("  加密后的字符串只能在本机解密");
            return 1;
        }

        string plainText = args[1];

        try
        {
            string encrypted = ConnectionStringEncryptor.Encrypt(plainText);

            Console.WriteLine("✅ 加密成功！");
            Console.WriteLine();
            Console.WriteLine("明文长度：" + plainText.Length + " 字符");
            Console.WriteLine("加密长度：" + encrypted.Length + " 字符");
            Console.WriteLine();
            Console.WriteLine("加密后的连接字符串：");
            Console.WriteLine(encrypted);
            Console.WriteLine();
            Console.WriteLine("请将上面的字符串复制到 appsettings.json 中：");
            Console.WriteLine();
            Console.WriteLine("{");
            Console.WriteLine("  \"ConnectionStrings\": {");
            Console.WriteLine("    \"DefaultConnection\": \"" + encrypted + "\"");
            Console.WriteLine("  }");
            Console.WriteLine("}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 加密失败：{ex.Message}");
            Console.WriteLine();
            Console.WriteLine("可能的原因：");
            Console.WriteLine("  1. 不在 Windows 环境下运行");
            Console.WriteLine("  2. 没有 DataProtection API 权限");
            Console.WriteLine("  3. 连接字符串格式错误");

            return 1;
        }
    }
}
