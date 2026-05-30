# AstralStatusReporter

Windows 状态采集上报端（C\# 重构版），替代原 PowerShell 脚本。定时采集本机在线状态、前台窗口、进程信息、运行时异常，通过 HTTP 加密上报至后端 API。支持 AES 对称加密传输，密钥支持**系统环境变量 / 程序目录 \.env 文件**双模式加载，常驻后台静默运行。

## ✨ 特性

- **无感静默运行** — 用户级注册表开机自启，无需管理员权限，生产模式自动隐藏控制台后台常驻

- **极致稳定容错** — 全链路异常捕获隔离，单模块采集失败不崩溃主程序，所有非致命错误远程上报

- **原始数据保真** — 纯系统原生 Win32 API 采集，不篡改、不硬编码映射窗口信息，语义渲染交由前端处理

- **自动自愈修复** — 自启项丢失、路径变更、配置异常自动检测并修复

- **安全加密传输** — 完整报文 AES\-128\-CBC 加密，密钥零硬编码，双模式优先级加载

- **零侵入低消耗** — 纯用户态运行，无弹窗、无冗余日志、无需 UAC 提权，内存占用极低

- **双运行模式** — 调试模式展示控制台日志，生产模式静默后台运行

- **资源自动管理** — 网络连接复用、程序退出自动回收资源，无内存泄漏

## 🚀 快速开始

### 1\. 环境依赖

- 系统：Windows 10 / Windows 11

- 运行时：\.NET 6\.0 Windows 运行时

### 2\. 项目配置

程序首次运行自动生成 `config\.ini` 配置文件，可自行编辑：

```ini
# 后端上报接口地址
apiEndpoint=http://localhost:8000/api/v1/report
# 上报间隔（秒）
sleepInterval=30
# 注册表自启名称
autorunName=AstralStatusReporter
# AES密钥环境变量名称
envAesKeyName=ASTRAL_AES_KEY
```

### 3\. 生成 AES 密钥

PowerShell 执行命令，生成 16 字节 AES\-128 密钥并转为 Base64 格式：

```powershell
[Convert]::ToBase64String((1..16 | ForEach-Object { Get-Random -Minimum 0 -Maximum 255 }))
```

### 4\. 密钥配置（二选一）

#### 方式 A：系统用户环境变量（推荐常驻）

```powershell
# 设置密钥
[Environment]::SetEnvironmentVariable("ASTRAL_AES_KEY", "你的Base64密钥", "User")

# 删除密钥
[Environment]::SetEnvironmentVariable("ASTRAL_AES_KEY", $null, "User")
```

#### 方式 B：\.env 文件（便携分发）

程序同目录创建 `\.env` 文件：

```env
ASTRAL_AES_KEY=你的Base64密钥
```

### 5\. 运行程序

- **调试模式**（显示控制台日志）：`AstralStatusReporter\.exe \-\-debug`

- **生产模式**（静默后台运行）：直接双击运行 `AstralStatusReporter\.exe`

首次运行自动写入注册表自启，后续开机自动后台启动，无需手动操作。

## 📌 密钥加载优先级

1. 程序目录 `\.env` 文件（最高优先级）

2. 系统用户环境变量（兜底优先级）

3. 均未找到则打印警告，跳过加密上报

## 🔐 加密规范

### 加密算法

- 算法：AES\-128\-CBC

- 填充模式：PKCS7

- 向量规则：每次加密随机生成 16 字节 IV，拼接至密文头部

- 输出格式：二进制整体转 Base64 字符串

- 字符编码：UTF\-8

### 后端解密流程

1. 接收请求体 Base64 密文字符串

2. 解码为二进制数组

3. 前16字节截取为 IV，剩余内容为加密载荷

4. 使用对应 AES 密钥，以 AES\-128\-CBC、PKCS7 模式解密

5. 解析原始 JSON 报文

## 📡 API 接口规范

### 请求信息

```http
POST {apiEndpoint}
Content-Type: text/plain; charset=utf-8
```

### 解密后原始报文结构

```json
{
  "hostname": "DESKTOP-ABC123",
  "windowTitle": "Visual Studio Code",
  "windowClass": "Chrome_WidgetWin_1",
  "timestamp": 1746501234,
  "status": "online",
  "processName": "Code",
  "processRealName": "Code",
  "description": "Visual Studio Code",
  "error": null
}
```

### 字段说明

|字段|类型|说明|
|---|---|---|
|hostname|string|设备计算机名|
|windowTitle|string \| null|当前焦点窗口原生标题，无标题/获取失败为 null|
|windowClass|string \| null|系统原生窗口类名，用于前端语义映射|
|timestamp|int|秒级 Unix 时间戳|
|status|string|固定为 online，标识设备在线|
|processName|string \| null|前台进程名称|
|processRealName|string \| null|前台进程真实名称|
|description|string \| null|程序文件描述（应用友好名称）|
|error|string \| null|运行时非致命错误信息，无异常为 null|

### 后端响应规范

客户端不校验响应内容，后端返回 2xx 状态码即可：

```json
{ "status": "ok" }
```

## ⚠️ 错误上报场景

|异常场景|error 字段内容|说明|
|---|---|---|
|自启配置失败|自启设置失败: xxx|注册表权限/读写异常|
|自启自动修复|自启项参数异常，已重建|非致命，已自动修复，仅首次上报|
|窗口采集失败|窗口信息获取失败: xxx|窗口句柄读取异常|
|进程采集失败|进程信息获取失败: xxx|进程权限不足/已退出/不存在|
|加密失败|不上报，仅控制台打印|密钥错误/缺失，跳过本次上报|
|网络上报失败|不上报，仅控制台打印|网络中断/接口异常，不中断主流程|

## 💡 后端适配建议

- 以`hostname` 为唯一键，内存覆盖更新最新设备状态，避免频繁写库

- 通过 `timestamp` 超时判断设备离线状态

- 前端基于 `windowClass` 做桌面/任务栏/开始菜单等系统界面友好名称映射

- 所有 error 字段为非致命诊断信息，仅用于日志排查，不影响设备在线状态

## 📁 项目文件结构

```Plain Text
AstralStatusReporter/
├── AstralStatusReporter.exe  # 主程序（单文件发布）
├── config.ini                # 自定义配置文件（自动生成）
├── .env                      # 可选：密钥配置文件
└── README.md                 # 项目文档
```

> （注：文档部分内容可能由 AI 生成）
