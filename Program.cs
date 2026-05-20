using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AstralStatusReporter
{
    class Program
    {
        // ==================== 配置参数 ====================
        private const string ApiEndpoint = "http://localhost:8000/api/v1/report";
        private const int SleepIntervalSeconds = 30;
        private const string AutorunName = "AstralStatusReporter";
        private const string EnvAesKeyName = "ASTRAL_AES_KEY";
        // ================================================

        private static readonly HttpClient HttpClient = new();
        private static byte[]? _aesKey = null;
        private static bool _encryptFailedOnce = false;
        private static bool _autorunWarnReported = false;
        private static string? _initialError = null;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        static async Task Main(string[] args)
        {
            Console.WriteLine("\n=== Astral 状态上报客户端 启动 ===");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  当前上报间隔时间: {SleepIntervalSeconds} 秒");

            // Load AES key from .env or environment variable
            LoadAesKey();

            // Setup autorun
            _initialError = SetAutorun();

            while (true)
            {
                string? windowTitle = null;
                string? runtimeError = null;

                // --- Get active window title ---
                try
                {
                    windowTitle = GetActiveWindowTitle();
                }
                catch (Exception ex)
                {
                    runtimeError = ex.Message;
                }

                // --- Get process info ---
                var procInfo = GetFocusProcessInfo();
                if (!string.IsNullOrEmpty(procInfo.Error))
                {
                    runtimeError = string.IsNullOrEmpty(runtimeError)
                        ? procInfo.Error
                        : $"{runtimeError}; {procInfo.Error}";
                }

                // --- Determine error to report ---
                string? reportError = null;
                if (_initialError != null && !_autorunWarnReported)
                {
                    reportError = _initialError;
                    _autorunWarnReported = true;
                }
                else
                {
                    reportError = runtimeError;
                }

                // --- Build payload ---
                var payload = new
                {
                    hostname = Environment.MachineName,
                    window_title = windowTitle,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    status = "online",
                    error = reportError,

                    process_name = procInfo.ProcessName,
                    process_real_name = procInfo.ProcessRealName,
                    description = procInfo.Description
                };

                string jsonBody = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });

                // --- Encrypt ---
                string? encryptedBody = ProtectJsonAes(jsonBody);
                if (encryptedBody == null)
                {
                    if (!_encryptFailedOnce)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FAIL 首次加密失败，后续将不再重复告警");
                        _encryptFailedOnce = true;
                    }
                    await Task.Delay(SleepIntervalSeconds * 1000);
                    continue;
                }

                // --- Send ---
                try
                {
                    var content = new StringContent(encryptedBody, Encoding.UTF8, "text/plain");
                    var response = await HttpClient.PostAsync(ApiEndpoint, content);
                    response.EnsureSuccessStatusCode();

                    string displayTitle = windowTitle ?? "NULL";
                    string displayErr = reportError ?? "None";
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] OKay! 窗口: {displayTitle} | 错误: {displayErr}");

                    string displayProcName = procInfo.ProcessName ?? "NULL";
                    string displayProcReal = procInfo.ProcessRealName ?? "NULL";
                    string displayDesc = procInfo.Description ?? "NULL";
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] OKay! 进程: {displayProcName} | {displayProcReal} | {displayDesc}");
                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FAIL 上报失败: {ex.Message}");
                }

                await Task.Delay(SleepIntervalSeconds * 1000);
            }
        }

        private static void LoadAesKey()
        {
            string scriptDir = AppContext.BaseDirectory;
            string envPath = Path.Combine(scriptDir, ".env");

            string? aesKey = null;

            // Try .env file
            if (File.Exists(envPath))
            {
                var lines = File.ReadAllLines(envPath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                        continue;

                    int eqIndex = trimmed.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        string key = trimmed[..eqIndex].Trim();
                        if (key == EnvAesKeyName)
                        {
                            aesKey = trimmed[(eqIndex + 1)..].Trim();
                            break;
                        }
                    }
                }
            }

            if (aesKey == null)
            {
                aesKey = Environment.GetEnvironmentVariable(EnvAesKeyName, EnvironmentVariableTarget.User);
            }

            if (aesKey != null)
            {
                try
                {
                    _aesKey = Convert.FromBase64String(aesKey);
                    if (_aesKey.Length != 16)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  AES 密钥必须为 128 位（16 字节），当前解码长度为 {_aesKey.Length} 字节");
                        _aesKey = null;
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  AES 密钥已加载");
                    }
                }
                catch (FormatException)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  AES 密钥 Base64 格式无效");
                }
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  未找到 AES 密钥");
            }
        }

        private static string? SetAutorun()
        {
            try
            {
                string scriptPath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                string expectedCommand = $"\"{scriptPath}\"";

                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) throw new InvalidOperationException("无法打开注册表 Run 键");

                string? currentValue = key.GetValue(AutorunName)?.ToString();

                if (currentValue != expectedCommand)
                {
                    key.SetValue(AutorunName, expectedCommand);
                    if (currentValue == null)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  自启项已安装");
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  自启命令不匹配，已自动修复");
                        return "自启项参数异常，已重建";
                    }
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  自启配置正常");
                }

                return null;
            }
            catch (Exception ex)
            {
                string errorMsg = $"自启设置失败: {ex.Message}";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR {errorMsg}");
                return errorMsg;
            }
        }

        private static string? GetActiveWindowTitle()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            var sb = new StringBuilder(256);
            int len = GetWindowText(hwnd, sb, sb.Capacity);
            if (len > 0)
            {
                string title = sb.ToString();
                return string.IsNullOrWhiteSpace(title) ? null : title;
            }
            return null;
        }

        private record ProcessInfo(string? ProcessName, string? ProcessRealName, string? Description, string? Error);

        private static ProcessInfo GetFocusProcessInfo()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return new(null, null, null, null);

                uint pid = 0;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return new(null, null, null, null);

                var proc = Process.GetProcessById((int)pid);
                string? name = proc.ProcessName;
                string? realName = proc.ProcessName;
                string? desc = null;

                // Try Description
                if (!string.IsNullOrWhiteSpace(proc.MainModule?.FileVersionInfo?.FileDescription))
                {
                    desc = proc.MainModule.FileVersionInfo.FileDescription;
                }

                return new(name, realName, desc, null);
            }
            catch (Exception ex)
            {
                return new(null, null, null, $"进程信息获取失败: {ex.Message}");
            }
        }

        private static string? ProtectJsonAes(string plainText)
        {
            if (_aesKey == null)
            {
                return null;
            }

            try
            {
                using var aes = Aes.Create();
                aes.Key = _aesKey;
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var encryptor = aes.CreateEncryptor();
                byte[] data = Encoding.UTF8.GetBytes(plainText);
                byte[] encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);

                byte[] result = new byte[aes.IV.Length + encrypted.Length];
                Array.Copy(aes.IV, result, aes.IV.Length);
                Array.Copy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

                return Convert.ToBase64String(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] AES 加密失败: {ex.Message}");
                return null;
            }
        }
    }
}