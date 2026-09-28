# NModbus.Extensions 

为 [NModbus](https://github.com/NModbus/NModbus) 提供的扩展方法和工具库，解决工业现场最常见的两大痛点：**字节序（字序）问题**和**多类型寄存器读写**。一行代码即可读写 `Int16 / UInt16 / Int32 / UInt32 / float / string`，默认字节序与 HslCommunication 兼容（CDAB）。

[![license](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![frameworks](https://img.shields.io/badge/net5.0--net10.0-512BD4)](NModbus.Extensions/NModbus.Extensions.csproj)
[![build](https://img.shields.io/github/actions/workflow/status/xiapeng01/NModbus.Extensions/main.yml?branch=master)](https://github.com/xiapeng01/NModbus.Extensions/actions)

## 目录

- [为什么用它](#为什么用它)
- [安装](#安装)
- [快速上手](#快速上手)
- [功能详解](#功能详解)
- [方法命名约定](#方法命名约定)
- [目标框架](#目标框架)
- [仓库结构](#仓库结构)
- [持续集成与发布](#持续集成与发布)
- [许可](#许可)

## 为什么用它

NModbus 本身只提供 `UInt16` 级别的寄存器读写，实际对接 PLC、仪表、网关时你需要自己处理：

1. **字节序**：不同设备的大小端、高低字序组合五花八门（ABCD / BADC / CDAB / DCBA），拼错一个字节读出来的浮点数就是乱码；
2. **类型转换**：`float`、`Int32` 等占两个寄存器，需要反复拆分、拼接、符号转换；
3. **字符串**：ASCII/UTF8 字符串与寄存器数组之间的互转，还要处理末尾补零。

本库把这些全部封装为**扩展方法**，直接挂在 `IModbusMaster`、`IModbusSlave`、`TcpClient`、`SerialPort` 上，`using NModbus.Extensions;` 后即可链式使用，不改变 NModbus 的任何原有行为。

## 安装

```bash
dotnet add package NModbus.Extensions
```

或在 Visual Studio 的 NuGet 包管理器中搜索 `NModbus.Extensions`。

依赖：`NModbus 3.0.83`、`NModbus.Serial 3.0.83`（自动引入）。

## 快速上手

```csharp
using NModbus.Extensions;   // 一行 using，解锁全部扩展方法

// 创建主站（TCP 或 RTU 串口，见 Tools 工具类）
var mb = Tools.CreateTcpMaster("127.0.0.1", 502);

// 可选：设置字节序，默认为 CDAB（与 HslCommunication 兼容）
mb.SetEndian(EnumEndian.CDAB);

// 直接读写各种类型，无需手工拆拼寄存器
mb.WriteFloatRW(1, 100, new float[] { -1.1f, -2.2f, -3.3f });   // 站号1，地址100
float[] vals = mb.ReadFloatRW(1, 100, 3);

mb.WriteStringRW(1, 200, "ABCDE");                               // 字符串
string s = mb.ReadStringRW(1, 200, 5);

mb.WriteInt32RW(1, 300, 123456);                                 // 单个 32 位整数
int v = mb.ReadInt32RW(1, 300);
```

做从站（模拟设备）同样简单：

```csharp
var ser = Tools.CreateTcpClientNetwork(502);   // 监听 127.0.0.1:502
var slave = ser.CreateSlave(1);                // 站号 1

// 把数据"放进"设备，供主站读取
slave.WriteFloatR(100, 3.14159f);              // 写入输入寄存器（主站只能读）
slave.WriteInt16RW(200, new short[] { 1, 2, 3 }); // 写入保持寄存器（主站可读写）
```

可运行的完整示例见 [NModbus.Extensions.Demo/Program.cs](NModbus.Extensions.Demo/Program.cs)。

## 功能详解

### 1. 字节序设置与转换

```csharp
client.SetEndian(EnumEndian.ABCD);   // 可作用于 TcpClient / SerialPort / IModbusMaster / IModbusSlave
var e = client.GetEndian();          // 未设置时默认返回 CDAB
```

命名规则：把 32 位值（`Int32 / UInt32 / float`）的 4 个字节按有效性从高到低记为 A、B、C、D，**枚举名即字节在总线上的发送顺序**（与源码 `ConvertEndian` 的实际变换一致）：

| 枚举值 | 通行叫法 | 发送字节顺序 | 说明 |
|--------|----------|--------------|------|
| `ABCD` | 大端序 | A B C D | Modbus 网络标准序，字序、字节序均为高位在前 |
| `BADC` | 字内字节交换 | B A D C | 字的先后顺序不变，每个寄存器内高低字节颠倒 |
| `CDAB` | 高低字交换（**默认**，兼容 HslCommunication） | C D A B | 低字先发送，字内字节保持高位在前 |
| `DCBA` | 小端序 | D C B A | 与 C# 内存原生字节序一致，最低字节先发 |

> 对 16 位类型（`Int16 / UInt16`）只有一个寄存器，字内字节序由 Modbus 协议固定为高位在前；本库对 16 位数组在 `ABCD`/`BADC` 时交换寄存器高低字节，`CDAB`/`DCBA` 时不做转换。

任意数组也可手动转换：`values.ToTargetEndian(EnumEndian.ABCD)`、`ConvertEndian<T>()`，单字节交换用 `((ushort)0x1234).Swap()`。

### 2. 主站读写（IModbusMaster 扩展）

所有方法返回 `bool` 表示写入成功；读取方法提供 `count` 重载与单个值重载：

```csharp
mb.WriteUInt16RW(stationNo, address, ushort[] values);
ushort[] ReadUInt16RW(stationNo, address, count);
ushort   ReadUInt16RW(stationNo, address);        // 单个值简写
```

32 位类型（`Int32 / UInt32 / float`）自动占用两个连续寄存器；`string` 按 UTF8 编码、奇数长度自动补 `0x00`，读取时自动过滤末尾零字符。

### 3. 从站数据准备（IModbusSlave 扩展）

`R` 系列写入**输入寄存器/线圈输入**（主站只读区），`RW` 系列写入**保持寄存器/线圈**（主站可读写区），均按从站配置的字节序自动转换：

```csharp
slave.WriteBoolR(0, new bool[] { true, false, true });
slave.WriteUInt32RW(100, 4294967295);
slave.WriteStringR(1, 200, "DEVICE-A");
```

### 4. 类型转换工具

独立于 Modbus 连接，可单独使用：

```csharp
ushort[] regs = ((int[]){ 1, 2 }).ToUInt16();     // Int32[] → UInt16[]（每个值拆 2 个寄存器）
float[]  fs   = regs.ToFloat();                    // UInt16[] → float[]
string   str  = regs.ToString();                   // UInt16[] → UTF8 字符串（过滤末尾 \0）
ushort[] back = "hello".ToUInt16();                // 字符串 → UInt16[]
```

支持 `Int16 / UInt16 / Int32 / UInt32 / float / string` 六种形态之间的相互转换（字符串一律走 16 位通道）。

### 5. 连接创建工具（Tools）

| 方法 | 用途 |
|------|------|
| `Tools.CreateTcpMaster(ip, port)` | 创建 Modbus TCP 主站 |
| `Tools.CreateRtuMaster(portName, baud, ...)` | 创建 RTU 串口主站（默认 9600,8,N,1） |
| `Tools.CreateRtuOverTcpMaster(ip, port)` | 创建 RTU over TCP 主站 |
| `Tools.CreateTcpClientNetwork(port)` | 创建 TCP 从站网络（监听 127.0.0.1，后台异步运行） |
| `Tools.CreateRtuClientNetwork(portName)` | 创建 RTU 串口从站网络（115200,8,N,1） |
| `Tools.CreateRtuOverTcpClientNetwork(port)` | 创建 RTU over TCP 从站网络 |
| `network.CreateSlave(stationNo)` | 向从站网络添加指定站号的从站 |
| `Tools.CreateRandom(n)` | 生成 n 个随机 `double`（调试用） |

## 方法命名约定

| 后缀 | 寄存器区 | 主站视角 | 从站写入的数据区 |
|------|----------|----------|------------------|
| `R` | 输入寄存器 / 线圈输入 | 只读（`ReadInputs` / `ReadInputRegisters`） | `DataStore.InputRegisters` / `CoilInputs` |
| `RW` | 保持寄存器 / 线圈 | 可读写（`Read/WriteMultipleRegisters`、`Coils`） | `DataStore.HoldingRegisters` / `CoilDiscretes` |

方法名格式统一为 `{操作}{类型}{区域}`，如 `ReadFloatRW` = 主站读保持寄存器区 float 数组。

## 目标框架

`net5.0`、`net6.0`、`net7.0`、`net8.0`、`net9.0`、`net10.0` 多目标。任何 >= .NET 5 的项目引用后会自动匹配对应框架版本。

> 注意：本地构建需要与目标框架匹配或更高的 .NET SDK（如要编译 net10.0，需 .NET 10 SDK）。

## 仓库结构

```
NModbus.Extensions/
├── NModbus.Extensions/           # 类库（NuGet 包本体）
│   ├── NModbusExtensions.cs      # 字节序、类型转换、主从站读写扩展方法
│   └── Tools.cs                  # 主站/从站网络与连接创建工具
├── NModbus.Extensions.Demo/      # 控制台示例（TCP 主从自发自收演示）
├── .github/workflows/main.yml    # CI：构建 + 打包 + 发布到 NuGet.org
└── NModbus.Extensions.slnx       # 解决方案（需 .NET 9.0.2xx+ SDK 打开）
```

## 持续集成与发布

推送到 `master` 分支或提 PR 时，GitHub Actions（[main.yml](.github/workflows/main.yml)）会自动完成 restore → build → pack → 发布到 NuGet.org。发布需要在仓库 Settings → Secrets 中配置 `NUGET_API_KEY`，且该 API Key 的 Glob Pattern 必须覆盖包 ID `NModbus.Extensions`（如 `NModbus*`）。

## 许可

MIT © [xiapeng](https://github.com/xiapeng01) · [云母工艺 (Mica Apps)](https://github.com/MicaApps) — 详见 [LICENSE](LICENSE)
