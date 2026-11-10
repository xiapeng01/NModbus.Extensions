# NModbus.Extensions 

为 [NModbus](https://github.com/NModbus/NModbus) 提供的扩展方法和工具库，解决工业现场最常见的三大痛点：**字节序（字序）问题**、**多类型寄存器读写**和**主从链路搭建**。一行代码即可读写 `Int16 / UInt16 / Int32 / UInt32 / float / string`，默认字节序与 HslCommunication 兼容（CDAB）；`Tools` 覆盖 TCP / RTU 串口 / RTU over TCP 的主从链路创建，并补齐 NModbus 缺失的**监听式 RTU over TCP 从站**（主站工具直连场景）。

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
- [变更说明](#变更说明)
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
var ser = Tools.CreateTcpSlaveNetwork(502);          // 监听 0.0.0.0:502（本机所有网卡）
// var ser = Tools.CreateTcpSlaveNetwork("127.0.0.1", 502); // 仅本机可访问
var slave = ser.CreateSlave(1);                // 站号 1

// 把数据"放进"设备，供主站读取
slave.WriteFloatR(100, 3.14159f);              // 写入输入寄存器（主站只能读）
slave.WriteInt16RW(200, new short[] { 1, 2, 3 }); // 写入保持寄存器（主站可读写）
```

如果主站工具（Modbus Poll、组态软件等）是 **TCP 客户端直连**，且线路上要求 RTU 帧（透传网关常见的接线方式），用监听式 RTU over TCP：

```csharp
var rtuServer = Tools.CreateRtuOverTcpSlaveNetwork("0.0.0.0", 502);          // 本机 502 端口监听，跑 RTU 帧
rtuServer.CreateSlave(1).WriteUInt16RW(0, 100);                              // 主站连入后即可读到
```

可运行的完整示例见 [NModbus.Extensions.Demo/Program.cs](NModbus.Extensions.Demo/Program.cs)。

## 功能详解

### 1. 字节序设置与转换

```csharp
client.SetEndian(EnumEndian.ABCD);   // 可作用于 TcpClient / SerialPort / IModbusMaster / IModbusSlave
var e = client.GetEndian();          // 未设置时默认返回 CDAB
```

> **注意**：字节序保存在"你调用 `SetEndian` 的那个对象"上，按对象实例各自存储。`TcpClient` / `SerialPort` 上设置的值**不会**自动传导给由它创建的 `IModbusMaster` / `IModbusSlaveNetwork`——实际读写用哪个对象，就要在该对象上单独调用一次 `SetEndian`。

命名规则：把 32 位值（`Int32 / UInt32 / float`）的 4 个字节按有效性从高到低记为 A、B、C、D，**枚举名即字节在总线上的发送顺序**（与源码 `ConvertEndian` 的实际变换一致）：

| 枚举值 | 通行叫法 | 发送字节顺序 | 说明 |
|--------|----------|--------------|------|
| `ABCD` | 大端序 | A B C D | Modbus 网络标准序，字序、字节序均为高位在前 |
| `BADC` | 字内字节交换 | B A D C | 字的先后顺序不变，每个寄存器内高低字节颠倒 |
| `CDAB` | 高低字交换（**默认**，兼容 HslCommunication） | C D A B | 低字先发送，字内字节保持高位在前 |
| `DCBA` | 小端序 | D C B A | 与 C# 内存原生字节序一致，最低字节先发 |

> 对 16 位类型（`Int16 / UInt16`）只有一个寄存器，字序无意义，只看寄存器内高低字节：本库在 `BADC`/`DCBA` 时交换该寄存器的高低字节，`ABCD`/`CDAB` 时不做转换。单值重载与数组重载共用同一套判定，结果一致。

任意数组也可手动转换：`values.ToTargetEndian(EnumEndian.ABCD)`（16 位单值同样支持 `value.ToTargetEndian(...)`）、`ConvertEndian<T>()`，单字节交换用 `((ushort)0x1234).Swap()`。

### 2. 主站读写（IModbusMaster 扩展）

所有方法返回 `bool` 表示写入成功；读取方法提供 `count` 重载与单个值重载：

```csharp
mb.WriteUInt16RW(stationNo, address, ushort[] values);
ushort[] ReadUInt16RW(stationNo, address, count);
ushort   ReadUInt16RW(stationNo, address);        // 单个值简写
```

32 位类型（`Int32 / UInt32 / float`）自动占用两个连续寄存器。

`string` 按 UTF8 编码写入，字节数为奇数时自动补 `0x00`，读取时自动过滤末尾零字符。**注意：`ReadStringR` / `ReadStringRW` 的 `count` 参数是"字节数"（UTF8 编码后的长度），不是字符数**——中文等一个字符占 3 字节，若按字符个数传 `count` 会读不全。例如写入 `"中文测试"`（UTF8 共 12 字节）应使用 `mb.ReadStringRW(1, addr, 12)` 才能完整读回。

### 3. 从站数据准备（IModbusSlave 扩展）

`R` 系列写入**输入寄存器/线圈输入**（主站只读区），`RW` 系列写入**保持寄存器/线圈**（主站可读写区），均按从站配置的字节序自动转换：

```csharp
slave.WriteBoolR(0, new bool[] { true, false, true });
slave.WriteUInt32RW(100, 4294967295);
slave.WriteStringR(200, "DEVICE-A");
```

### 4. 类型转换工具

独立于 Modbus 连接，可单独使用：

```csharp
ushort[] regs = ((int[]){ 1, 2 }).ToUInt16();     // Int32[] → UInt16[]（每个值拆 2 个寄存器）
float[]  fs   = regs.ToFloat();                   // UInt16[] → float[]
ushort[] back = "hello".ToUInt16();               // 字符串 → UInt16[]

// UInt16[] → UTF8 字符串：必须静态调用，不能写成 regs.ToString()
string str = NModbusExtensions.ToString(regs);    // 已过滤末尾 \0
```

> `ToString` 虽然以扩展方法形式提供，但数组实例自带的 `object.ToString()` 优先级更高，`regs.ToString()` 实际会返回 `System.UInt16[]`。请使用上面的静态调用形式。

支持 `Int16 / UInt16 / Int32 / UInt32 / float / string` 六种形态之间的相互转换（字符串一律走 16 位通道）。

### 5. 连接创建工具（Tools）

| 方法 | 用途 |
|------|------|
| `Tools.CreateTcpMaster(ip, port)` | 创建 Modbus TCP 主站 |
| `Tools.CreateRtuMaster(portName, baud, ...)` | 创建 RTU 串口主站（默认 9600,8,N,1） |
| `Tools.CreateRtuOverTcpMaster(ip, port)` | 创建 RTU over TCP 主站 |
| `Tools.CreateTcpSlaveNetwork(port, onError?)` | 创建 TCP 从站网络（监听 `0.0.0.0` 即本机所有网卡，后台异步运行）；0.2.1 起默认地址由 `127.0.0.1` 改为 `0.0.0.0`，从站会暴露到局域网 |
| `Tools.CreateTcpSlaveNetwork(ip, port, onError?)` | 0.2.1 新增：创建 TCP 从站网络并指定监听 IP（如 `"127.0.0.1"` 仅本机可访问） |
| `Tools.CreateRtuSlaveNetwork(portName, baudRate?, dataBits?, parity?, stopBits?, onError?)` | 创建 RTU 串口从站网络（0.2.1 起串口参数可传，默认 115200,8,N,1） |
| `Tools.CreateRtuOverTcpSlaveNetwork(ip, port, onError?, connectTimeoutMs?)` | 创建 RTU over TCP 从站网络（**监听模式**）：在本机 `ip:port` 监听，主站工具作为 TCP 客户端直连，每条连接一条独立 RTU 链路、共享同一组从站数据 |
| `Tools.CreateRtuOverTcpSlaveNetwork(port, onError?, connectTimeoutMs?)` | 同上，监听地址固定为 `127.0.0.1`（仅本机可访问）；`connectTimeoutMs` 为 0.2.x 兼容参数，**监听模式不再生效** |
| `network.CreateSlave(stationNo)` | 向从站网络添加指定站号的从站 |
| `Tools.CreateRandom(n)` | 生成 n 个随机 `double`（调试用） |

**RTU over TCP 从站只有一种拓扑：本程序监听，主站工具连入。**

```csharp
var server = Tools.CreateRtuOverTcpSlaveNetwork("0.0.0.0", 502);   // 指定监听地址
int actualPort = ((RtuOverTcpSlaveNetwork)server).Port;            // 传 0 时取系统分配的端口

var loopback = Tools.CreateRtuOverTcpSlaveNetwork(502);            // 仅监听 127.0.0.1
```

`AddSlave` / `RemoveSlave` 自动同步到所有已接入链路；可同时接入多个主站。链路启用 TCP keepalive（探测拔线/掉电的死链）与 1 秒空闲读超时——主站访问**未注册站号**时 NModbus 会去读一个永远不来的响应而永久阻塞，超时能让链路自动回到监听循环（自愈），不会因一次错误请求永久哑火。

> 0.2.x 的这两个同名方法原本是"作为 TCP 客户端连入透传网关"的实现（未正确实现，且不适用于从站模拟）。0.3.0 起**方法名与参数列表保持不变，实现整体替换为监听模式**，因此旧调用代码无需修改即可编译，但运行语义从"连出去"变为"本机监听"——若原端口已被别的主站/服务占用，会在创建时抛 `SocketException`。主站侧的 `CreateRtuOverTcpMaster` 不受影响。

> 三个从站网络创建方法在 0.2.0 之前名为 `CreateTcpClientNetwork` / `CreateRtuClientNetwork` / `CreateRtuOverTcpClientNetwork`，因实际返回的是从站服务端、名称易误解，已统一改为 `...SlaveNetwork`，详见[变更说明](#变更说明)。
> `onError` 为可选回调：从站启动失败（端口被占用、串口打不开等）会把异常回传给你；不传时（0.3.0 起）异常会记录到 `Console.Error` / `Trace`，不会再变成无人观察的 `UnobservedTaskException`。生产环境仍建议显式传入。

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
├── NModbus.Extensions/                 # 类库（NuGet 包本体）
│   ├── NModbusExtensions.cs            # 字节序、类型转换、主从站读写扩展方法
│   ├── Tools.cs                        # 主站/从站网络与连接创建工具
│   └── RtuOverTcpSlaveNetwork.cs       # 监听式 RTU over TCP 从站网络（NModbus 未提供）
├── NModbus.Extensions.Demo/            # 控制台示例（TCP 主从自发自收，四种字节序往返读写）
├── NModbus.Extensions.Tests/           # xUnit 测试：字节序往返、参数校验、RTU over TCP 两种拓扑回归
├── .github/workflows/main.yml    # CI：构建 + 打包 + 发布到 NuGet.org
└── NModbus.Extensions.slnx       # 解决方案（需 .NET 9.0.2xx+ SDK 打开）
```

## 变更说明

### 0.3.0（当前版本，2026-11）

> **版本合并说明**：`0.2.2` / `0.2.3` / `0.2.4` 曾规划为三次独立发布，但**均未发布到 nuget.org**（线上最新仍为 `0.2.1`）。为避免"线上版本跳号"的困惑，这些内容全部并入本版；下文仍按原始编号保留实现细节，仅作历史记录。

**⚠️ 行为不兼容（方法签名保持不变）**

- 类 `RtuOverTcpListenerSlaveNetwork` → **`RtuOverTcpSlaveNetwork`**（文件名同步）；工厂方法 `CreateRtuOverTcpListenerSlaveNetwork` 并入 `CreateRtuOverTcpSlaveNetwork`。
- **旧的 RTU over TCP 从站实现整体替换为监听模式，方法名与参数列表原样保留**：

  | 方法（0.2.x 与 0.3.0 签名一致） | 0.2.x 行为 | 0.3.0 行为 |
  |---|---|---|
  | `CreateRtuOverTcpSlaveNetwork(int port, onError? = null, connectTimeoutMs = 3000)` | 作为 TCP 客户端连 `127.0.0.1:port` | 在 `127.0.0.1:port` **监听** |
  | `CreateRtuOverTcpSlaveNetwork(string ip, int port, onError? = null, connectTimeoutMs = 3000)` | 作为 TCP 客户端连 `ip:port` | 在 `ip:port` **监听** |

  - 旧实现（连出去）未正确实现且不适用于从站模拟，代码已删除；仅其使用的 `ConnectWithTimeout` / `EnableKeepAlive` 私有辅助方法一并移除。
  - `connectTimeoutMs` 仅为源码兼容保留：监听模式没有"连接远端"这一步，**该参数不再生效**。
  - 源码无需改动即可编译，但**语义反转仍属行为不兼容**：原先创建即代表"已连上网关"，现在代表"已开始监听"，端口被占用时会抛 `SocketException`。
- 内部调整：链路读超时常量由 `Tools.SlaveLinkReadTimeoutMs` 移入 `RtuOverTcpSlaveNetwork.LinkReadTimeoutMs`。

```csharp
// 0.2.x 与 0.3.0 都能编译通过，但语义不同：
var server = Tools.CreateRtuOverTcpSlaveNetwork(502);          // 0.3.0：本机 502 监听，主站工具连入
var server2 = Tools.CreateRtuOverTcpSlaveNetwork("0.0.0.0", 502); // 0.3.0：监听所有网卡
```

**测试**

- 监听模式用例随类/方法改名；新增两个旧签名回归用例：`Legacy_PortOnly_Overload_Listens_On_Loopback`、`Legacy_ConnectTimeout_Parameter_Is_Accepted_And_Ignored`；移除客户端模式用例 3 个，总数 75 → 74（全部通过）。

### 0.2.4（未发布，内容并入 0.3.0）

**缺陷修复**

- **未注册站号会永久拖死链路**：主站访问一个没有注册的站号时，NModbus 在忽略该请求后会调用 `IgnoreResponse()`，RTU 实现是去读一个永远不会到来的响应，该链路从此不再响应任何请求（不报错、也不重连）。现在 RTU over TCP 链路（当时的客户端模式与监听模式）都设置了 1 秒空闲读超时：阻塞在读超时处抛 `IOException`，被 NModbus 监听循环捕获后进入下一轮，链路每秒自愈一次。对正常帧无影响（RTU 帧由主站/网关一次性写入，字节间隔为毫秒级）。

**测试**

- 新增 `Unknown_Slave_Request_Does_Not_Stall_Link`：先向未注册站号发请求（必须快速失败而非挂死），随后正常站号必须恢复应答。

### 0.2.3（未发布，内容并入 0.3.0）

**新增**

- `Tools.CreateRtuOverTcpListenerSlaveNetwork(string ip, int port, Action<Exception>? onError = null)` 及公共类 `RtuOverTcpListenerSlaveNetwork`：**监听式 RTU over TCP 从站网络**。NModbus 只提供"监听 + Modbus TCP"（`CreateSlaveNetwork(TcpListener)`）和"RTU + `IStreamResource`"（`CreateRtuSlaveNetwork`）两种组合，缺少"监听 + RTU 帧"，本类补齐：本机监听、每条连接一条独立 RTU 链路、所有连接共享同一组从站数据、`AddSlave`/`RemoveSlave` 自动同步、TCP keepalive、监听异常可观测、可 `Dispose`；`Port` 属性返回实际端口（构造传 0 时由系统分配）。

**缺陷修复**

- **监听链路被串行化**：每条连接的 RTU 链路改为在独立线程上运行。NModbus 的 `ModbusSerialSlaveNetwork.ListenAsync` 是同步阻塞方法（内部 `Read` 阻塞到链路断开才返回），若直接在 accept 循环线程上执行，**第一条链路会把 accept 循环永久占死**，之后所有主站连接都无法接入——单主站测试完全暴露不出这个问题。

**测试**

- 新增 `RtuOverTcpListenerSlaveNetworkTests`：连入后 RTU 读写、接入后再加从站自动同步、多连接共享数据、端口占用抛 `SocketException`、非法 IP 抛 `ArgumentException`。

### 0.2.2（未发布，内容并入 0.3.0）

**缺陷修复**

- `Tools.CreateRtuOverTcpSlaveNetwork(port)` 无参重载把 `"0.0.0.0"` 当作远端地址去连接（Windows 上碰巧当回环，Linux/macOS 直接抛 `SocketException`，且与注释"连接 127.0.0.1"矛盾）。现改为连接 `127.0.0.1`，并统一委托给带 IP 的重载，消除重复代码。
- 连接阶段无超时：同步 `Connect` 在对端不可达时可阻塞 20 秒以上且不可取消。现改用 `ConnectAsync` + 超时，新增可选参数 `connectTimeoutMs`（默认 3000ms），超时抛带原因的 `TimeoutException`，失败时释放半开连接。
- 链路半开无处理：新增 TCP keepalive（15s 空闲 / 3s 间隔 / 5 次重试），对端拔线或掉电未挥手时由内核探测后断开链路。
- 从站启动失败静默：`StartListening` 在未传 `onError` 时不再裸 `throw`（否则任务无人观察，GC 时可能触发 `UnobservedTaskException` 终止进程），改为记录到 `Console.Error` + `Trace`。该改动作用于全部四个 `Create*SlaveNetwork`。

**测试**

- 新增 `RtuOverTcpSlaveNetworkTests`：无参/带 IP 两种重载的端到端读写（回归 `0.0.0.0` bug）、连接被拒绝时立即抛异常不阻塞。

### 0.2.1（2026-10）

**⚠️ 破坏性变更（两处，升级需改调用代码）**

- **从站侧 `WriteStringR` / `WriteStringRW` 移除多余的 `stationNo` 参数**：从站自身不需要站号，旧签名 `WriteStringR(slave, stationNo, address, str)` 的首个参数极易被误当成地址，且是同类从站方法中唯一带站号的两个。现统一为 `WriteStringR(slave, address, str)`。
  ```csharp
  slave.WriteStringR(1, 200, "DEVICE-A");   // ❌ 0.2.1 起编译失败（无此重载）
  slave.WriteStringR(200, "DEVICE-A");      // ✅
  ```
  主站侧 `WriteStringRW(mb, stationNo, address, str)` **不受影响**（主站确实需要站号）。
- **`Tools.CreateTcpSlaveNetwork(port)` 默认监听地址由 `127.0.0.1` 改为 `0.0.0.0`（本机所有网卡）**：旧行为下模拟从站只能被本机访问，外部采集端/联调程序连不上。升级后从站会暴露到局域网，**若只需本机访问请显式传 IP**：`Tools.CreateTcpSlaveNetwork("127.0.0.1", 502)`。

**新增**

- `Tools.CreateTcpSlaveNetwork(string ip, int port, Action<Exception>? onError = null)` 重载：可指定监听的本地 IP（如 `"127.0.0.1"`、`"192.168.1.10"`），默认走 `IPAddress.Any`。
- `Tools.CreateRtuSlaveNetwork` 串口参数化：新增 `baudRate`（默认 115200）、`dataBits`（8）、`parity`（None）、`stopBits`（One），此前四个值写死在方法体内无法调整；默认值与 0.2.0 一致，不传参行为不变。主站 `CreateRtuMaster` 仍默认 9600，主从直连请显式对齐。

**工程修正**

- 移除库与 Demo 工程里硬编码的 `AssemblyOriginatorKeyFile`（`D:\MyKey.snk`）——指向作者本机的强名密钥文件，在其他机器上会直接导致构建失败。
- NuGet 包元数据修正：`Authors` / `Copyright` 中的 `Mica App` → `Mica Apps`。

**兼容性**

- 除上述两处破坏性变更外，0.2.1 的线上数据布局与 0.2.0 **完全一致**：字节序规则、字符串寄存器布局（`EncodeString` / `DecodeString` 固定每寄存器交换高低字节）、四类字节序下的读写往返均无任何改动，已有设备数据无需重新写入。

### 0.2.0

**⚠️ 破坏性变更**

- 三个从站网络创建方法改名（原名易误解为客户端）：
  `Tools.CreateTcpClientNetwork` → `Tools.CreateTcpSlaveNetwork`
  `Tools.CreateRtuClientNetwork` → `Tools.CreateRtuSlaveNetwork`
  `Tools.CreateRtuOverTcpClientNetwork` → `Tools.CreateRtuOverTcpSlaveNetwork`
  旧名不存在于 0.2.0，升级时需全局替换。

**缺陷修复**

- **32 位读写字节序错乱（严重）**：0.1.x 中 `Int32 / UInt32 / float` 的写入与读取使用了两套不对称的变换——写入按 4 字节整体置换、读取却先做 16 位寄存器级转换再拼装。导致除默认 `CDAB` 外的所有字节序模式下"写入值 ≠ 读回值"（例如 `ABCD` 下写入 `-123456789` 读回 `-349002504`）。现在读写共用同一套 32 位变换，四种字节序均往返自洽，并与 HslCommunication 对应模式互读一致（见 Demo）。
- **16 位单值与数组行为不一致**：0.1.x 中单值写入走独立的高低字节判定，与数组路径不同，`ABCD`/`DCBA` 下同一数值走两个重载会写入不同的线上数据。现在单值重载与数组重载统一走同一规则（`BADC`/`DCBA` 交换寄存器高低字节，`ABCD`/`CDAB` 不交换）。
- **从站启动异常静默丢失**：`ListenAsync` 后台任务中的异常（端口被占用、串口打不开等）0.1.x 会静默进入未观察任务，调用方误以为从站已就绪。0.2.0 起可传入可选 `onError` 回调感知失败。
- **长时间运行内存泄漏**：字节序字典 0.1.x 按强引用存储连接对象，对象销毁后条目不回收。现改用弱引用表，随连接对象回收自动消失。
- **程序集版本与包版本不一致**：0.1.1 的 NuGet 包版本为 `0.1.1` 但程序集版本是 `0.9.x`。现由工程文件 `<Version>` 自动生成，二者始终一致。

**新增**

- 16 位单值版 `ToTargetEndian` 扩展（`UInt16` / `Int16`），与数组版共用同一判定。

**兼容性**

- 默认 `CDAB`（兼容 HslCommunication 的线上布局）下，16 位 / 32 位 / 字符串各类型读写产生的线上数据与 0.1.1 **完全一致**，升级后可直接互读旧数据，不会乱码。
- `ABCD` / `BADC` / `DCBA` 模式：0.1.1 中这些模式本身就读写错乱、无可用语义，0.2.0 修正为与 HslCommunication 同名模式一致的布局；若你在 0.1.1 上曾依赖其"错误布局"存储过数据，升级后需重新写入。

### 0.1.x（2026-09）

- 初版：字节序扩展方法、`Int16 / UInt16 / Int32 / UInt32 / float / string` 主从站读写扩展、Tools 连接创建工具。默认 `CDAB` 可用；非默认字节序存在上述 0.2.0 已修复的读写不对称问题。

## 许可

MIT © [xiapeng](https://github.com/xiapeng01) · [云母工艺 (Mica Apps)](https://github.com/MicaApps) — 详见 [LICENSE](LICENSE)
