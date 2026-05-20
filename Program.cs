using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AstralStatusReporter
{
    // Win32 API 导入
    public static class Win32
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

    // 配置类
    public class Config
    {
        public string ApiEndpoint { get; set; } = "http://localhost:8000/api/v1/report";
        public int SleepInterval { get; set; } = 30;
        public string AutorunName { get; set; } = "AstralStatusReporter";
        public string EnvAesKeyName { get; set; } = "ASTRAL_AES_KEY";
    }

    // 进程信息类
    public class ProcessInfo
    {
        public string ProcessName { get; set; }
        public string ProcessRealName { get; set; }
        public string Description { get; set; }
        public string Error { get; set; }
    }

    // 上报数据类
    public class ReportPayload
    {
        public string Hostname { get; set; }
        public string WindowTitle { get; set; }
        public long Timestamp { get; set; }
        public string Status { get; set; } = "online";
        public string Error { get; set; }
        public string ProcessName { get; set; }
        public string ProcessRealName { get; set; }
        public string Description { get; set; }
    }

    // 主程序
    public class Program
    {
        private static Config _config;
        private static string _scriptDir;
        private static string _aesKey;
        private static bool _autorunWarnReported = false;
        private static string _initialError = null;
        private static bool _encryptFailedOnce = false;
        private static HttpClient _httpClient;

        [STAThread]
        public static void Main(string[] args)
        {
            // 控制台窗口处理：DLL模式显示，EXE模式隐藏
            if (!IsDllMode())
            {
                HideConsoleWindow();
            }

            // 强制 TLS 1.2
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            Console.WriteLine("\n=== Astral 状态上报客户端 启动 ===");

            // 初始化路径
            _scriptDir = AppDomain.CurrentDomain.BaseDirectory;

            // 加载配置
            LoadConfig();

            // 加载AES密钥
            LoadAesKey();

            // 设置自启
            _initialError = SetAutorun();

            // 初始化HTTP客户端
            _httpClient = new HttpClient();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  当前上报间隔时间: {_config.SleepInterval} 秒");

            // 无限循环上报
            while (true)
            {
                string windowTitle = null;
                string runtimeError = null;

                // 获取窗口标题
                try
                {
                    windowTitle = GetActiveWindowTitle();
                }
                catch (Exception ex)
                {
                    runtimeError = ex.Message;
                }

                // 获取进程信息
                var procInfo = GetFocusProcessInfo();
                if (!string.IsNullOrEmpty(procInfo.Error))
                {
                    if (!string.IsNullOrEmpty(runtimeError))
                    {
                        runtimeError += "; " + procInfo.Error;
                    }
                    else
                    {
                        runtimeError = procInfo.Error;
                    }
                }

                // 错误上报逻辑
                string reportError = null;
                if (!string.IsNullOrEmpty(_initialError) && !_autorunWarnReported)
                {
                    reportError = _initialError;
                    _autorunWarnReported = true;
                }
                else
                {
                    reportError = runtimeError;
                }

                // 构建JSON payload
                var payload = new ReportPayload
                {
                    Hostname = Environment.MachineName,
                    WindowTitle = windowTitle,
                    Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    Status = "online",
                    Error = reportError,
                    ProcessName = procInfo.ProcessName,
                    ProcessRealName = procInfo.ProcessRealName,
                    Description = procInfo.Description
                };

                string jsonBody = Newtonsoft.Json.JsonConvert.SerializeObject(payload);

                // 加密
                string encryptedBody = ProtectJsonAes(jsonBody);
                if (string.IsNullOrEmpty(encryptedBody))
                {
                    if (!_encryptFailedOnce)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FAIL 首次加密失败，后续将不再重复告警");
                        _encryptFailedOnce = true;
                    }
                    System.Threading.Thread.Sleep(_config.SleepInterval * 1000);
                    continue;
                }

                // 发送
                try
                {
                    var content = new StringContent(encryptedBody, Encoding.UTF8, "text/plain");
                    var response = _httpClient.PostAsync(_config.ApiEndpoint, content).Result;
                    response.EnsureSuccessStatusCode();

                    string displayTitle = !string.IsNullOrEmpty(windowTitle) ? windowTitle : "NULL";
                    string displayErr = !string.IsNullOrEmpty(reportError) ? reportError : "None";
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Okay! 窗口: {displayTitle} | 错误: {displayErr}");

                    string displayProcName = !string.IsNullOrEmpty(procInfo.ProcessName) ? procInfo.ProcessName : "NULL";
                    string displayProcReal = !string.IsNullOrEmpty(procInfo.ProcessRealName) ? procInfo.ProcessRealName : "NULL";
                    string displayDesc = !string.IsNullOrEmpty(procInfo.Description) ? procInfo.Description : "NULL";
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Okay! 进程: {displayProcName} | {displayProcReal} | {displayDesc}");
                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FAIL 上报失败: {ex.Message}");
                }

                System.Threading.Thread.Sleep(_config.SleepInterval * 1000);
            }
        }

        // 判断是否为DLL模式
        private static bool IsDllMode()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            return assembly.ManifestModule.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        // 隐藏控制台窗口（仅EXE模式）
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private static void HideConsoleWindow()
        {
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_HIDE);
            }
        }

        // 加载INI配置
        private static void LoadConfig()
        {
            _config = new Config();
            string iniPath = Path.Combine(_scriptDir, "config.ini");

            if (!File.Exists(iniPath))
            {
                // 生成默认配置文件
                GenerateDefaultConfig(iniPath);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  配置文件不存在，已生成默认配置: {iniPath}");
            }

            // 读取INI文件
            var lines = File.ReadAllLines(iniPath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                    continue;

                var parts = trimmed.Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                    continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                switch (key.ToLower())
                {
                    case "apiendpoint":
                        _config.ApiEndpoint = value;
                        break;
                    case "sleepinterval":
                        if (int.TryParse(value, out int interval))
                            _config.SleepInterval = interval;
                        break;
                    case "autorunname":
                        _config.AutorunName = value;
                        break;
                    case "envaeskeyname":
                        _config.EnvAesKeyName = value;
                        break;
                }
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  配置加载完成");
        }

        // 生成默认配置文件
        private static void GenerateDefaultConfig(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# AstralStatusReporter 配置文件");
            sb.AppendLine("# 后端接收接口地址");
            sb.AppendLine($"apiEndpoint={_config.ApiEndpoint}");
            sb.AppendLine();
            sb.AppendLine("# 上报间隔（秒）");
            sb.AppendLine($"sleepInterval={_config.SleepInterval}");
            sb.AppendLine();
            sb.AppendLine("# 注册表自启名称");
            sb.AppendLine($"autorunName={_config.AutorunName}");
            sb.AppendLine();
            sb.AppendLine("# AES 密钥环境变量名称");
            sb.AppendLine($"envAesKeyName={_config.EnvAesKeyName}");
            sb.AppendLine();
            sb.AppendLine("# 注意：AES密钥不在此文件中配置，请使用.env文件或系统环境变量");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        // 加载.env文件
        private static Dictionary<string, string> LoadEnvFile()
        {
            var envVars = new Dictionary<string, string>();
            string envPath = Path.Combine(_scriptDir, ".env");

            if (File.Exists(envPath))
            {
                var lines = File.ReadAllLines(envPath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                        continue;

                    var parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2)
                        continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    envVars[key] = value;
                }
            }

            return envVars;
        }

        // 加载AES密钥
        private static void LoadAesKey()
        {
            // 优先从.env文件加载
            var envVars = LoadEnvFile();
            if (envVars.ContainsKey(_config.EnvAesKeyName))
            {
                _aesKey = envVars[_config.EnvAesKeyName];
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  AES 密钥已从 .env 加载");
                return;
            }

            // 其次从系统环境变量加载
            _aesKey = Environment.GetEnvironmentVariable(_config.EnvAesKeyName, EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(_aesKey))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  AES 密钥已从系统环境变量加载");
                return;
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  未找到 AES 密钥");
        }

        // 设置自启
        private static string SetAutorun()
        {
            try
            {
                string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string expectedCommand = $"\"{exePath}\"";

                using (var key = Registry.CurrentUser.OpenSubKey(registryPath, true))
                {
                    if (key == null)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR  无法打开注册表");
                        return "无法打开注册表";
                    }

                    var currentValue = key.GetValue(_config.AutorunName) as string;

                    if (string.IsNullOrEmpty(currentValue))
                    {
                        key.SetValue(_config.AutorunName, expectedCommand);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  自启项已安装");
                        return null;
                    }

                    if (currentValue != expectedCommand)
                    {
                        key.SetValue(_config.AutorunName, expectedCommand);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  自启命令不匹配，已自动修复");
                        return "自启项参数异常，已重建";
                    }

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  自启配置正常");
                    return null;
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"自启设置失败: {ex.Message}";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR {errorMsg}");
                return errorMsg;
            }
        }

        // 获取当前活动窗口标题
        private static string GetActiveWindowTitle()
        {
            try
            {
                var sb = new StringBuilder(256);
                IntPtr hwnd = Win32.GetForegroundWindow();
                int len = Win32.GetWindowText(hwnd, sb, sb.Capacity);

                if (len > 0)
                {
                    string title = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        return title;
                    }
                }
            }
            catch
            {
                // 忽略异常
            }

            return null;
        }

        // 获取当前进程信息
        private static ProcessInfo GetFocusProcessInfo()
        {
            try
            {
                IntPtr hwnd = Win32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    return new ProcessInfo
                    {
                        ProcessName = null,
                        ProcessRealName = null,
                        Description = null,
                        Error = null
                    };
                }

                uint focusPid = 0;
                Win32.GetWindowThreadProcessId(hwnd, out focusPid);

                if (focusPid == 0)
                {
                    return new ProcessInfo
                    {
                        ProcessName = null,
                        ProcessRealName = null,
                        Description = null,
                        Error = null
                    };
                }

                var proc = Process.GetProcessById((int)focusPid);

                string name = proc.ProcessName;
                string realName = proc.ProcessName;
                string desc = null;

                // 优先使用 Description
                try
                {
                    if (!string.IsNullOrWhiteSpace(proc.MainModule.FileVersionInfo.FileDescription))
                    {
                        desc = proc.MainModule.FileVersionInfo.FileDescription;
                    }
                }
                catch
                {
                    // 忽略异常
                }

                return new ProcessInfo
                {
                    ProcessName = name,
                    ProcessRealName = realName,
                    Description = desc,
                    Error = null
                };
            }
            catch (Exception ex)
            {
                return new ProcessInfo
                {
                    ProcessName = null,
                    ProcessRealName = null,
                    Description = null,
                    Error = $"进程信息获取失败: {ex.Message}"
                };
            }
        }

        // AES加密
        private static string ProtectJsonAes(string plainText)
        {
            try
            {
                if (string.IsNullOrEmpty(_aesKey))
                {
                    throw new Exception("AES 密钥未加载");
                }

                byte[] keyBytes = Convert.FromBase64String(_aesKey);
                if (keyBytes.Length != 16)
                {
                    throw new Exception($"AES 密钥必须为 128 位（16 字节），当前解码长度为 {keyBytes.Length} 字节");
                }

                using (var aes = Aes.Create())
                {
                    aes.Key = keyBytes;
                    aes.GenerateIV();
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var encryptor = aes.CreateEncryptor())
                    {
                        byte[] data = Encoding.UTF8.GetBytes(plainText);
                        byte[] encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);

                        byte[] result = new byte[aes.IV.Length + encrypted.Length];
                        Array.Copy(aes.IV, result, aes.IV.Length);
                        Array.Copy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

                        return Convert.ToBase64String(result);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] AES 加密失败: {ex.Message}");
                return null;
            }
        }
    }
}