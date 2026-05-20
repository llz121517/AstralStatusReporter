using Microsoft.Win32;
using System;
using System.ComponentModel;
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
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
        public string WindowClass { get; set; }
        public string Error { get; set; }
    }

    // 上报数据类
    public class ReportPayload
    {
        public string hostname { get; set; }
        public long timestamp { get; set; }
        public string status { get; set; } = "online";
        public string windowTitle { get; set; }
        public string windowClass { get; set; }
        public string processName { get; set; }
        public string processRealName { get; set; }
        public string description { get; set; }
        public string error { get; set; }
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
            // 判断运行模式：开发模式显示控制台，生产模式隐藏
            bool isDevelopment = Debugger.IsAttached ||
                                 args.Contains("--debug") ||
                                 args.Contains("-d");

            if (!isDevelopment)
            {
                HideConsoleWindow();
            }

            // 强制 TLS 1.2
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            Console.WriteLine("\n=== Astral 状态上报客户端 启动 ===");

            // 初始化路径
            _scriptDir = AppDomain.CurrentDomain.BaseDirectory;

            // 配置加载错误将被捕获并记录
            try
            {
                LoadConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  配置加载异常: {ex.Message}");
                _config = new Config(); // 使用默认配置
            }

            // AES密钥加载错误将被捕获并记录
            try
            {
                LoadAesKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  AES密钥加载异常: {ex.Message}");
                _aesKey = null;
            }

            // 自启动设置错误被捕获并存储
            try
            {
                _initialError = SetAutorun();
            }
            catch (Exception ex)
            {
                _initialError = $"自启动设置异常: {ex.Message}";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR {_initialError}");
            }

            // 初始化HTTP客户端
            _httpClient = new HttpClient();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  当前上报间隔时间: {_config.SleepInterval} 秒");

            try
            {
                // 无限循环上报
                while (true)
                {
                    string windowTitle = null;
                    string windowClass = null;
                    string runtimeError = null;

                    // 窗口信息获取错误被捕获并记录
                    try
                    {
                        var windowInfo = GetActiveWindowInfo();
                        windowTitle = windowInfo.Item1;
                        windowClass = windowInfo.Item2;
                    }
                    catch (Exception ex)
                    {
                        runtimeError = $"窗口信息获取失败: {ex.Message}";
                    }

                    // 进程信息获取错误被捕获并记录
                    ProcessInfo procInfo;
                    try
                    {
                        procInfo = GetFocusProcessInfo();
                        if (!string.IsNullOrEmpty(procInfo.Error))
                        {
                            runtimeError = AppendError(runtimeError, procInfo.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        procInfo = new ProcessInfo();
                        runtimeError = AppendError(runtimeError, $"进程信息异常: {ex.Message}");
                    }

                    // 确保所有非致命错误都被包含
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
                        hostname = Environment.MachineName,
                        timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                        status = "online",
                        windowTitle = windowTitle,
                        windowClass = windowClass,
                        processName = procInfo.ProcessName,
                        processRealName = procInfo.ProcessRealName,
                        description = procInfo.Description,
                        error = reportError  // 所有非致命错误上报字段
                    };

                    string jsonBody = Newtonsoft.Json.JsonConvert.SerializeObject(payload);

                    // 加密失败 - 无法通信，只打印控制台
                    string encryptedBody = ProtectJsonAes(jsonBody);
                    if (string.IsNullOrEmpty(encryptedBody))
                    {
                        if (!_encryptFailedOnce)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FAIL 首次加密失败，后续将不再重复告警");
                            _encryptFailedOnce = true;
                        }
                        System.Threading.Thread.Sleep(_config.SleepInterval * 1000);
                        continue;  // 跳过本次上报
                    }

                    // 网络请求失败 - 无法通信，只打印控制台
                    try
                    {
                        var content = new StringContent(encryptedBody, Encoding.UTF8, "text/plain");
                        var response = _httpClient.PostAsync(_config.ApiEndpoint, content).Result;
                        response.EnsureSuccessStatusCode();

                        string displayTitle = !string.IsNullOrEmpty(windowTitle) ? windowTitle : "NULL";
                        string displayClass = !string.IsNullOrEmpty(windowClass) ? windowClass : "NULL";
                        string displayErr = !string.IsNullOrEmpty(reportError) ? reportError : "None";
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Okay! 窗口: {displayTitle} | 类名: {displayClass} | 错误: {displayErr}");

                        string displayProcName = !string.IsNullOrEmpty(procInfo.ProcessName) ? procInfo.ProcessName : "NULL";
                        string displayProcReal = !string.IsNullOrEmpty(procInfo.ProcessRealName) ? procInfo.ProcessRealName : "NULL";
                        string displayDesc = !string.IsNullOrEmpty(procInfo.Description) ? procInfo.Description : "NULL";
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Okay! 进程: {displayProcName} | {displayProcReal} | {displayDesc}");
                        Console.WriteLine();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FAIL 网络请求失败: {ex.Message}");
                        // 继续下一次循环
                    }

                    System.Threading.Thread.Sleep(_config.SleepInterval * 1000);
                }
            }
            finally
            {
                // 释放 HttpClient 资源
                _httpClient?.Dispose();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  程序退出，资源已释放");
            }
        }

        // 追加错误信息
        private static string AppendError(string existing, string newError)
        {
            if (string.IsNullOrEmpty(existing))
                return newError;
            return $"{existing}; {newError}";
        }

        // 隐藏控制台窗口
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

        // 获取当前活动窗口标题和类名
        private static Tuple<string, string> GetActiveWindowInfo()
        {
            IntPtr hwnd = Win32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return Tuple.Create<string, string>(null, null);
            }

            string title = null;
            string className = null;

            try
            {
                // 分别捕获标题和类名的错误
                var titleSb = new StringBuilder(256);
                int titleLen = Win32.GetWindowText(hwnd, titleSb, titleSb.Capacity);
                if (titleLen > 0)
                {
                    title = titleSb.ToString();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"窗口标题获取失败: {ex.Message}");
            }

            try
            {
                var classSb = new StringBuilder(256);
                int classLen = Win32.GetClassName(hwnd, classSb, classSb.Capacity);
                if (classLen > 0)
                {
                    className = classSb.ToString();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"窗口类名获取失败: {ex.Message}");
            }

            return Tuple.Create(title, className);
        }

        // 获取当前进程信息
        private static ProcessInfo GetFocusProcessInfo()
        {
            IntPtr hwnd = Win32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return new ProcessInfo
                {
                    ProcessName = null,
                    ProcessRealName = null,
                    Description = null,
                    WindowClass = null,
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
                    WindowClass = null,
                    Error = null
                };
            }

            try
            {
                using (var proc = Process.GetProcessById((int)focusPid))
                {
                    string name = proc.ProcessName;
                    string realName = proc.ProcessName;
                    string desc = null;
                    string winClass = null;
                    string error = null;

                    // MainModule 可能为 null
                    if (proc.MainModule != null)
                    {
                        try
                        {
                            if (proc.MainModule.FileVersionInfo != null &&
                                !string.IsNullOrWhiteSpace(proc.MainModule.FileVersionInfo.FileDescription))
                            {
                                desc = proc.MainModule.FileVersionInfo.FileDescription;
                            }
                        }
                        catch (Exception ex)
                        {
                            error = AppendError(error, $"文件描述获取失败: {ex.Message}");
                        }
                    }
                    else
                    {
                        error = AppendError(error, "MainModule 为空");
                    }

                    // 分别捕获窗口类名的错误
                    try
                    {
                        var classSb = new StringBuilder(256);
                        int classLen = Win32.GetClassName(hwnd, classSb, classSb.Capacity);
                        if (classLen > 0)
                        {
                            winClass = classSb.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        error = AppendError(error, $"窗口类名获取失败: {ex.Message}");
                    }

                    return new ProcessInfo
                    {
                        ProcessName = name,
                        ProcessRealName = realName,
                        Description = desc,
                        WindowClass = winClass,
                        Error = error
                    };
                }
            }
            catch (ArgumentException)
            {
                // 进程不存在
                return new ProcessInfo { Error = "进程不存在" };
            }
            catch (InvalidOperationException)
            {
                // 进程已退出
                return new ProcessInfo { Error = "进程已退出" };
            }
            catch (Win32Exception ex)
            {
                // 访问权限问题
                return new ProcessInfo { Error = $"进程访问失败: {ex.Message}" };
            }
            catch (Exception ex)
            {
                // 其他异常
                return new ProcessInfo { Error = $"进程信息获取失败: {ex.Message}" };
            }
        }

        // 加载INI配置
        private static void LoadConfig()
        {
            _config = new Config();
            string iniPath = Path.Combine(_scriptDir, "config.ini");

            if (!File.Exists(iniPath))
            {
                GenerateDefaultConfig(iniPath);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO  配置文件不存在，已生成默认配置: {iniPath}");
                return;
            }

            try
            {
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
            catch (Exception ex)
            {
                throw new Exception($"配置文件读取失败: {ex.Message}");
            }
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

            if (!File.Exists(envPath))
            {
                return envVars;
            }

            try
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
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN  .env文件读取失败: {ex.Message}");
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
                return $"自启设置失败: {ex.Message}";
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