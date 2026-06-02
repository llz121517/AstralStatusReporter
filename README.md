# AstralStatusReporter

Windows 状态采集上报客户端，C# .NET 6 重构版，替代原 PowerShell 脚本。定时采集本机在线状态、前台窗口、进程信息，AES-128-CBC 加密上报至后端 API。

## 快速开始

### 环境要求

- Windows 10 / 11
- .NET 6.0 SDK（仅构建时需要，单文件发布可免运行时）

### 从源码构建

```powershell
git clone <仓库地址>
cd AstralStatusReporter
dotnet restore
dotnet build -c Release
```

发布单文件：

```powershell
dotnet publish -c Release
```

发布产物：`bin\Release\net6.0-windows\win-x64\publish\AstralStatusReporter.exe`

### 配置

首次运行自动生成 config.ini，可手动编辑：

```
apiEndpoint=http://localhost:8000/api/v1/report
sleepInterval=30
autorunName=AstralStatusReporter
envAesKeyName=ASTRAL_AES_KEY
```

修改 `apiEndpoint` 指定后端地址
`sleepInterval` 为采集间隔（秒）

### 生成 AES 密钥

```powershell
[Convert]::ToBase64String((1..16 | ForEach-Object { Get-Random -Minimum 0 -Maximum 255 }))
```

### 配置密钥（二选一）

**方式 A：系统环境变量（推荐常驻）**

```powershell
[Environment]::SetEnvironmentVariable("ASTRAL_AES_KEY", "你的Base64密钥", "User")
```

**方式 B：.env 文件（便携分发）**

在程序目录新建 .env 文件：

```
ASTRAL_AES_KEY=你的Base64密钥
```

密钥加载优先级：`.env` > 系统环境变量。

### 运行

- 调试模式（显示控制台日志）：`AstralStatusReporter.exe --debug`
- 生产模式（静默后台）：直接双击运行

首次运行自动写入 HKCU 注册表自启项。

## 加密规范

- 算法：AES-128-CBC，PKCS7 填充
- IV：每次加密随机生成 16 字节，拼接至密文头部
- 输出：二进制整体转 Base64 字符串
- 编码：UTF-8

后端解密：Base64 解码 → 前 16 字节为 IV → AES-128-CBC 解密 → 得到 JSON。

## API 接口

```
POST {apiEndpoint}
Content-Type: text/plain; charset=utf-8
```

请求体为 AES 加密后的 Base64 字符串。

解密后原始报文结构示例：

```json
{
  "hostname": "DESKTOP-ABC123",
  "timestamp": 1746501234,
  "status": "online",
  "windowTitle": "Visual Studio Code",
  "windowClass": "Chrome_WidgetWin_1",
  "processName": "Code",
  "processRealName": "Code",
  "description": "Visual Studio Code",
  "error": null
}
```

后端返回 2xx 即可（客户端不校验响应体）。

## 错误处理

| 场景 | error 字段 | 说明 |
|---|---|---|
| 自启配置失败 | 自启设置失败: xxx | 注册表权限/读写异常 |
| 自启自动修复 | 自启项参数异常，已重建 | 路径不匹配时自动修复 |
| 窗口采集失败 | 窗口信息获取失败: xxx | 句柄读取异常 |
| 进程采集失败 | 进程信息获取失败: xxx | 权限不足/进程退出 |
| 加密失败 | 不上报，仅控制台打印 | 密钥缺失/错误，跳过本次上报 |
| 网络上报失败 | 不上报，仅控制台打印 | 网络异常，不中断主循环 |

## 项目结构

```
AstralStatusReporter/
├── AstralStatusReporter.exe   # 主程序
├── config.ini                 # 配置文件（自动生成）
├── .env                       # 可选：AES 密钥
└── README.md
```
