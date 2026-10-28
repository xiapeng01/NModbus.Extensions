# NModbus.Extensions 全项目问题清单

> 代码走查范围：`NModbusExtensions.cs` (1523 行)、`Tools.cs` (182 行)、两个测试文件、三个 csproj、CI workflow、README。
> 本地构建因代理无法访问 nuget.org（NU1301）未能完成，问题清单基于源码静态走查 + 测试用例锁定语义的分析。

---

## 一、严重：会导致误用或线上事故（建议下个版本必修）

### 1. `EnumEndian` 枚举值 0 不是文档默认值
```csharp
public enum EnumEndian { DCBA, CDAB, BADC, ABCD }   // DCBA = 0
```
- `default(EnumEndian)`、`(EnumEndian)0`、未初始化字段都得到 **DCBA**，而文档承诺默认 **CDAB**。
- 任何把字节序存进 DTO / 用 `default` 初始化的调用方会静默拿到错误字节序，且读写仍然"自洽"不报错 —— 典型现场事故。
- **修复**：把 `CDAB` 挪到第一位（`CDAB, DCBA, BADC, ABCD`）。这是二进制不兼容变更，但枚举值很少被持久化，成本远低于风险。

### 2. `ConvertEndian<T>` 的 XML 注释把字节序标反了
```
把内存中的字节记为 a b c d（d 为最高有效字节）
ABCD → c d a b
```
**实际**：`srcSpan[offset]` 是该 4 字节值的**最低**有效字节（`a` 是最低，`d` 才是最高）。注释里的 `d 为最高有效字节` 是错的。
- 代码实现本身正确（已被 `Int32_Wire_Layout_Known_Answer` 四个用例锁定），但注释会误导任何要复用/移植这段逻辑的人。建议直接改写为"记为 a b c d（a 为最低有效字节）"并配一个具体数值示例。

### 3. 字符串通道与 16 位数值通道的字节序规则冲突（同一 API 内两套规则）
- `EncodeString` / `DecodeString` **无条件对每个寄存器做 `Swap()`**，完全忽略 `SetEndian`。
- 结果：默认 `CDAB` 下，`WriteUInt16RW(0x4142)` 线上是 `0x4142`，而 `WriteStringRW("AB")` 线上是 `0x4241`。同一地址两种布局。
- 注释与 README 都承认"刻意为之，为兼容已发布版本"。但这是**长期技术债**：库已经 0.2.0，破坏窗口已过，应该给一个开关：
  ```csharp
  public enum StringByteOrder { SwapEachRegister, KeepRegister }  // 默认 SwapEachRegister
  slave.SetStringByteOrder(...)  /  master.SetStringByteOrder(...)
  ```
  兼容旧行为的同时，让能接受破坏的用户拿到一致语义。

### 4. `StringRegCount` 的 `count < 2` 分支多读一个寄存器
```csharp
ushort bytes = CheckedCount(count);
return bytes < 2 ? (ushort)2 : (ushort)((bytes + 1) / 2);
```
`count = 1` 时返回 2，会从设备多读一个寄存器。设备恰好只有 1 个寄存器时会读失败。
- **修复**：`return (ushort)Math.Max(1, (bytes + 1) / 2);`

### 5. 写入地址 + 数量未做上界联合校验
`CheckedAddress` 只校验 `address ≤ 65535`，`CheckedCount` 只校验 `count ≤ 65535`，两者之和溢出后由底层 NModbus 抛异常。用户在 `address = 65534, count = 4` 时拿到的不是本库清晰的 `ArgumentOutOfRangeException`，而是 NModbus 的底层异常。
- **修复**：增加 `CheckedRange(address, count)` 联合校验。

### 6. `Tools.CreateTcpSlaveNetwork` 硬编码 `127.0.0.1`
```csharp
var listener = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
```
模拟从站**只能被本机访问**。真实场景（上位机模拟 PLC 给采集端读、联调外部程序）必须监听 `0.0.0.0`。`CreateRtuOverTcpSlaveNetwork` 同样硬编码 `127.0.0.1`。
- **修复**：加 `IPAddress? localIp = null` 可选参数，默认 `IPAddress.Any` 或保持 `Loopback` 但允许覆盖。

### 7. 从站方法带无意义的 `stationNo` 参数（API 债务）
```csharp
public static bool WriteStringR(this IModbusSlave slave, int stationNo, int address, string str)
public static bool WriteStringRW(this IModbusSlave slave, int stationNo, int address, string str)
```
从站自己不需要站号。README 里 `slave.WriteStringR(1, 200, "DEVICE-A")` 的第一个 `1` 完全无意义，**极易被误当成地址**。只有这两个方法带此参数，其他从站方法都没有 —— 不一致。
- **修复**：0.3.0 移除该参数（破坏性变更），并同步修正 README 和测试。

---

## 二、中等：API 缺陷 / 可靠性问题

### 8. `ToTargetEndian` 在"不交换"时返回入参数组引用
```csharp
case EnumEndian.ABCD:
case EnumEndian.CDAB:
    return values;   // 返回调用方的原数组
```
作为公共 API，返回入参数组是坏味道。调用方若 `var r = regs.ToTargetEndian(CDAB); r[0] = 999;` 会**改到自己的源数组**。虽然库内部调用路径不做修改所以当前无害，但 README 只在注释里提了一句。
- **修复**：至少在 README 的 API 表格里显著标注"返回原引用，请勿修改返回值"，或直接改为总是返回副本。

### 9. 类型转换 API 的 null 策略完全不统一
| 方法 | null 输入 |
|---|---|
| `ToUInt16(this Int16[]?)` | 返回 `null` ✅ |
| `ToUInt16(this Int32[]?)` | 返回 `null` ✅ |
| `ToUInt16(this string?)` | 返回 `null` ✅ |
| `ToInt16(this UInt16[])` | **NullReferenceException** ❌ |
| `ToInt32(this UInt16[])` | **NullReferenceException** ❌ |
| `ToUInt32(this UInt16[])` | **NullReferenceException** ❌ |
| `ToFloat(this UInt16[])` | **NullReferenceException** ❌ |
| `ToString(this UInt16[])` | **NullReferenceException** ❌ |
| `ToTargetEndian(this UInt16[], ...)` | **NullReferenceException** ❌ |

一半容忍 null 返回 null，一半直接崩。而且 `ToUInt16(Int16[])` 返回 `UInt16[]?` 可空，`ToInt16(UInt16[])` 返回非空，**可空标注本身就在提示这个不一致**。
- **修复**：要么全部统一为"null 抛 `ArgumentNullException`"，要么全部统一为返回 null。

### 10. 奇数长度静默丢数据
```csharp
public static Int32[] ToInt32(this UInt16[] values)
{
    var ret = new Int32[values.Length / 2];      // 奇数长度：末尾一个寄存器被丢弃
    Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);  // 且会越界写！
```
`values.Length = 3` 时 `ret.Length = 1`，`BlockCopy` 却拷 `3 * 2 = 6` 字节到 4 字节的数组 —— **直接 `ArgumentException` 越界**。抛的是 .NET 内部异常，不是本库清晰的说明。XML 注释只说"被忽略"，与实际行为（抛异常）**不符**。
- **修复**：显式校验 `values.Length % 2 == 0`，否则抛 `ArgumentException("寄存器个数必须为偶数...")`。`ToUInt32` / `ToFloat` 同样问题。

### 11. `Marshal.SizeOf<T>()` 用在泛型热路径
```csharp
if (Marshal.SizeOf<T>() != 4) throw new NotSupportedException(...);
```
- `Marshal.SizeOf<T>()` 对泛型每次调用都走反射式的尺寸查询，比 `Unsafe.SizeOf<T>()` 慢一个数量级。
- 更严重：`MemoryMarshal.Cast<T, byte>` 对**非 blittable** 的 `T`（含引用字段的 struct，如 `KeyValuePair<int,string>`）行为是未定义的 —— `SizeOf` 恰好返回 4 时不会拦住，然后 `Cast` 出的 span 指向托管引用对象上的字节，**可能读到 GC 堆内存或触发 GC 崩溃**。
- **修复**：改用 `Unsafe.SizeOf<T>()`，并显式拒绝含引用的类型（例如尝试 `MemoryMarshal.IsReferenceOrContainsReferences<T>()`，或直接对白名单类型 `typeof(T) == typeof(int) || ... ` 白名单校验）。注意 net5.0 没有 `IsReferenceOrContainsReferences`，需条件编译。

### 12. `ConvertEndian<T>` 把 switch 放在循环内
```csharp
for (int i = 0; i < values.Length; i++) {
    int offset = i * 4;
    switch (targetEndian) { ... }   // 每轮都重新跳转判断
}
```
`targetEndian` 在整个循环中是循环不变量。应该提到循环外，switch 分支各自整段循环。
- **修复**：重构为 switch 外层、for 内层。读写各一次分配，这个函数每次读写都会调。

### 13. `Tools` 中资源泄漏路径
- `CreateTcpSlaveNetwork`：`listener.Start()` 之后若 `fac.CreateSlaveNetwork(listener)` 抛异常，`listener` 不会被释放（端口占用残留）。
- `CreateRtuSlaveNetwork` / `CreateRtuMaster`：`sp.Open()` 之后若后续抛异常，**串口句柄泄漏**（串口被独占，下次再也开不了，只能重启进程）。
- **修复**：全部包 try/catch，失败时释放已获取的资源再抛。

### 14. `StartListening` 在无 `onError` 时仍然静默丢异常
```csharp
try { await ser.ListenAsync(); }
catch (Exception ex) { if (onError is null) throw; onError(ex); }
```
`throw` 发生在 `Task.Run` 内部 → 异常变成 `UnobservedTaskException` → **和 0.1.x 一样调用方感知不到**。注释说"保持原有异常传播行为（任务置为 Faulted）"，但 Faulted 的 Task 没人 await 就是不存在。
- **修复**：无 `onError` 时也应把异常送到一个可观测的位置 —— 至少 `Console.Error.WriteLine` + `Trace.TraceError`，或者干脆记入一个 `LastError` 属性让调用方轮询。

### 15. `Tools` 类完全无测试覆盖
`CreateTcpMaster` / `CreateRtuMaster` / `CreateRtuOverTcpMaster` / `CreateTcpSlaveNetwork` / `CreateRtuOverTcpSlaveNetwork` / `CreateSlave` / `CreateRandom` —— **一个测试都没有**。测试只覆盖了手工构造的 `Harness`。`CreateRandom` 里 `new Random(Guid.NewGuid().GetHashCode())` 每次调用创建新种子（正确做法，但值得测）。

---

## 三、代码卫生 / 质量

### 16. `NModbusExtensions.cs` 单文件 1523 行，应拆分
字节序、类型转换、从站写入、主站读写、字符串通道全在一个类里。建议拆为：
- `Endian/EndianExtensions.cs`（SetEndian/GetEndian/Swap/ToTargetEndian/ConvertEndian）
- `Conversion/TypeConversionExtensions.cs`
- `Master/ModbusMasterExtensions.cs`
- `Slave/ModbusSlaveExtensions.cs`
- `Internal/Validation.cs`（所有 Checked* / NonEmpty）

### 17. 未使用的 using
- `NModbusExtensions.cs`：`System.Net`、`System.Net.Sockets` 未使用。
- `Tools.cs`：`System.Text`、`System.Net`（用了 `IPAddress.Parse` —— 实际用了，`System.Net` 可保留）。
- `Demo/Program.cs`：`using Microsoft.VisualBasic;` 完全无意义，且 Microsoft.VisualBasic 在 net5.0+ 已是独立/精简程序集。

### 18. 完全未使用的字段
```csharp
static MemoryPool<UInt16> p16 = MemoryPool<UInt16>.Shared;
static MemoryPool<byte> pByte = MemoryPool<byte>.Shared;
```
**零引用**。而且是非 readonly 的 static 字段（可被误改）。直接删除。

### 19. `ToString(this ushort[])` 是设计缺陷而非"使用注意"
```csharp
public static string ToString(this UInt16[] values)
```
扩展方法永远无法通过实例语法调用（`object.ToString()` 优先级更高）。README 只能靠大段警告提醒用户"必须静态调用 `NModbusExtensions.ToString(regs)`"。
- **修复**：改名为 `ToUtf8String()`，保留 `ToString` 作为 `[Obsolete]` 别名一个版本周期，然后删掉。

### 20. `ReadInt16RW` 实现与 `ReadInt16R` 不一致
```csharp
// ReadInt16R —— 简洁
return res.ToTargetEndian(GetEndian(mb)).ToInt16();

// ReadInt16RW —— 手写 BlockCopy
var ret = new Int16[count];
Buffer.BlockCopy(res2, 0, ret, 0, count * 2);
```
同一语义两种写法，`ReadInt16RW` 还多一次分配。统一为前一种。

### 21. 返回 `bool` 但恒为 `true` —— 返回值无意义
所有从站侧 `Write*R` / `Write*RW` 全部 `return true`，主站侧也全部 `return true`（除了空字符串返回 `false`）。签名暗示"可能失败"，实际只有空串一个特例。
- **修复**：要么改成 `void`，要么保留 `bool` 但在异常时可返回 `false` 而非抛出。目前"抛异常 + 恒 true"的组合最容易让调用方写出无意义的 `if (ok)`。

### 22. `FormTargetEndian` × 3 是重复 API
与 `ToTargetEndian` 逻辑完全相同，只是名字不同。三个别名 = 三份文档 + 三份维护面。

### 23. `Swap(Int16)` 实现绕
```csharp
var u = (UInt16)value;
return (Int16)(u >> 8 | u << 8);   // u << 8 是 int 运算，结果靠截断
```
`UInt16` 版本 `(UInt16)(value >> 8 | value << 8)` 同样靠隐式 int 提升 + 截断。正确但易误读。
- **修复**：`return (UInt16)((value >> 8) | (value << 8));` — 结果本来就在 ushort 范围内，显式截断表达意图。

### 24. `GetEndianCore` 有写副作用
```csharp
dicEndian.GetValue(key, static _ => new StrongBox<EnumEndian>(EnumEndian.CDAB))
```
只读操作 `GetEndian` 会**创建**一个 `StrongBox` 条目。对从未 `SetEndian` 的对象调一次 `GetEndian` 就会在弱引用表里留下条目。
- **修复**：改用 `TryGetValue`，miss 时直接返回 `EnumEndian.CDAB`，不创建条目。

### 25. `ToUInt16(this string?)` 的零拷贝优化写反了
```csharp
var ary1 = Encoding.UTF8.GetBytes(str);
if (ary1.Length % 2 != 0) ary1 = ary1.Append((byte)0x00).ToArray();  // 二次分配
```
`Append().ToArray()` 分配新数组再拷一次。`Array.Resize(ref ary1, ary1.Length + 1)` 只多一次拷贝且无 LINQ 依赖。

### 26. `CreateRtuMaster` / `CreateRtuSlaveNetwork` 默认波特率不一致
- 主站默认 **9600**
- 从站固定 **115200**

主从直连时两者不一致，Demo 里也没覆盖 RTU 直连。用户必须自己记住去传参。**从站也应该把波特率做成参数**，默认值对齐。

### 27. Demo 不会退出且不释放资源
`Program.cs` 创建了 TCP 从站网络和主站，从不 `Dispose`，从不退出进程。作为"可运行完整示例"，示范了错误的资源管理习惯。
- **修复**：`using` 包裹 + 演示 `Tools.CreateTcpSlaveNetwork(600, ex => Console.WriteLine(ex))` 的正确用法。

### 28. Demo 未覆盖 README 声称的"HslCommunication 互读对照"
README 仓库结构写：*"Demo ... 四种字节序与 HslCommunication 互读对照"*。但 `Demo.csproj` 只引用 `Dumpify`，**根本没有 HslCommunication 依赖**，Demo 里也没有互读对照代码。README 描述与实际不符。

---

## 四、工程化 / CI

### 29. `<GeneratePackageOnBuild>True</GeneratePackageOnBuild>` 应当移除
每次 `dotnet build` 都会打包一次，既拖慢本地构建，又在 `bin/` 下留下 `.nupkg`。CI 里已经显式 `dotnet pack` 了，重复。
- **修复**：移除该属性，或改为仅在 Release + CI 环境开启。

### 30. 库多目标到 net5.0，但 CI 只有 .NET 10 SDK 且测试只跑 net10.0
- `net5.0`（2022-05 EOL）、`net6.0`（2024-11 EOL）、`net7.0`（2024-05 EOL）均已停止支持。
- 多目标让 CI 构建时间 ×6，而**行为测试只跑 net10.0**，低版本目标只是"能编译"级别验证。
- **建议**：至少砍掉 `net5.0`（`System.Buffers.MemoryPool`、`ConditionalWeakTable.GetValue(key, factory)` 等 API 在 net5.0 上都可用，但收益为零）；或保留但用 `#if` 明确低版本差异。测试项目可做 `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` 增加真实覆盖。

### 31. CI 未做 `--framework` 拆分，构建慢
`dotnet build` 一次编 6 个 TFM。CI 里若缓存失效会很慢。可考虑矩阵化或至少缓存 NuGet。

### 32. CI 缺少 `dotnet format` / 静态分析 / 覆盖率
无 `TreatWarningsAsErrors`、无分析器、无覆盖率报告。上面 17/18/22 这类问题本可被静态分析直接拦下。

### 33. csproj 缺少 `<ImplicitUsings>` 的库项目未开
类库项目不开是对的（显式 using 更好），但 `LangVersion` 锁 `10.0` 而 TFM 到 net10.0 —— `LangVersion 10.0` 会**限制住 net10.0 上可用的新 C# 语法**。多目标时 `LangVersion` 应为最新或随 TFM 分级。

### 34. 无 CHANGELOG、无 RELEASE notes 流程
变更历史全塞在 README 的"变更说明"里，随 README 越改越长。建议独立 `CHANGELOG.md`（Keep a Changelog 格式）。

### 35. `AssemblyInfo.cs` 里手写 `[assembly: Guid]` + `[assembly: ComVisible(false)]`
`ComVisible`/`Guid` 是 .NET Framework 时代的遗留，SDK 项目会自动生成，且 Guid 无实际意义。可以删掉两个手写特性，只留注释。

### 36. 解决方案缺少 `.editorconfig` / `Directory.Build.props`
`Nullable enable` 只在 csproj 里逐个写；三项目共享的元数据（版本、作者、License）没有统一入口。

---

## 五、文档与 README 不符之处

| README 位置 | 声称 | 实际 |
|---|---|---|
| 仓库结构 | Demo 含"与 HslCommunication 互读对照" | 无该依赖、无该代码 |
| 3. 从站数据准备 | `slave.WriteStringR(1, 200, "DEVICE-A")` | 第一个 `1` 是废弃的 `stationNo`，极易误读为地址 |
| 4. 类型转换 | "`((int[]){ 1, 2 }).ToUInt16()`" 等示例 | 部分输入路径返回 `null` 未在示例中体现 |
| 字节序表 | `DCBA` = "与 C# 内存原生字节序一致" | 一致（这个是对的），但 `ConvertEndian` 的 XML 注释又说 DCBA 要做 `b a d c` 变换，**两处自相矛盾** |
| 目标框架 | net5.0–net10.0 | 前三个已 EOL |

---

## 建议的处理顺序

**立刻（下个补丁版本，只改不改语义）**
1. 删 `p16` / `pByte` 未使用字段（#18）
2. 修 `StringRegCount` 的 `count < 2` 分支（#4）
3. 清理未使用 using（#17）
4. 修 `ConvertEndian` 的错误 XML 注释（#2）
5. `ToInt32`/`ToUInt32`/`ToFloat` 奇数长度显式校验（#10）
6. README 更正三处不符（#28、#5、#35）

**下一个次版本（0.3.0，破坏性）**
7. `EnumEndian` 重排使 `CDAB = 0`（#1）
8. 移除从站 `WriteStringR/RW` 的 `stationNo` 参数（#7）
9. `ToString` → `ToUtf8String`（#19）
10. 统一转换 API 的 null 策略（#9）
11. `ToTargetEndian` 不再返回原引用（#8）

**中期重构**
12. 拆分单文件（#16）、`Marshal.SizeOf` → `Unsafe.SizeOf` + blittable 白名单（#11）
13. `Tools` 补参数化（监听地址、波特率、超时）+ 资源释放（#6、#13、#26）
14. `Tools` 补测试（#15）、引入 `StringByteOrder` 开关（#3）
15. CI 引入静态分析与格式化门禁（#32）、精简 TFM（#30）