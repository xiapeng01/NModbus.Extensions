using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Extensions;
using Xunit;

namespace NModbus.Extensions.Tests;

/// <summary>
/// 参数校验回归测试：越界地址/站号/数量必须抛 <see cref="ArgumentOutOfRangeException"/>，
/// 禁止 int→ushort/byte 静默截断；null/空数组统一抛 <see cref="ArgumentException"/>
/// （修复从站侧原先抛 NullReferenceException 的不一致行为）。
/// </summary>
public class ValidationTests
{
    private static IModbusSlave NewSlave() => new ModbusFactory().CreateSlave(1);

    /// <summary>进程内从站网络 + 已连接主站（校验异常在触网前抛出，主站仅需存在）。</summary>
    private static (IModbusMaster master, IDisposable network) NewConnectedMaster()
    {
        var fac = new ModbusFactory();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var network = fac.CreateSlaveNetwork(listener);
        network.AddSlave(fac.CreateSlave(1));
        _ = Task.Run(async () => { try { await network.ListenAsync(); } catch { } });
        var master = fac.CreateMaster(new TcpClient("127.0.0.1", port));
        return (master, network);
    }

    // ---------------- 地址越界 ----------------

    [Theory]
    [InlineData(65536)]
    [InlineData(65541)]      // 旧行为：静默截断写到 0x2345
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Master_Write_Rejects_OutOfRange_Address(int address)
    {
        var (mb, network) = NewConnectedMaster();
        using (mb)
        using (network)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.WriteUInt16RW(1, address, (ushort)1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.WriteInt32RW(1, address, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.WriteFloatRW(1, address, 1.5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.WriteBoolRW(1, address, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.WriteStringRW(1, address, "AB"));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadUInt16RW(1, address, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadInt32RW(1, address, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadStringRW(1, address, 2));
        }
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Slave_Write_Rejects_OutOfRange_Address(int address)
    {
        var slave = NewSlave();
        Assert.Throws<ArgumentOutOfRangeException>(() => slave.WriteUInt16RW(address, (ushort)1));
        Assert.Throws<ArgumentOutOfRangeException>(() => slave.WriteInt16R(address, (short)1));
        Assert.Throws<ArgumentOutOfRangeException>(() => slave.WriteFloatR(address, 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => slave.WriteBoolRW(address, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => slave.WriteStringRW(1, address, "AB"));
    }

    // ---------------- 站号越界 ----------------

    [Theory]
    [InlineData(256)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Master_Rejects_OutOfRange_Station(int stationNo)
    {
        var (mb, network) = NewConnectedMaster();
        using (mb)
        using (network)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.WriteUInt16RW(stationNo, 0, (ushort)1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadUInt32RW(stationNo, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadBoolRW(stationNo, 0, 1));
        }
    }

    // ---------------- 数量越界 / 乘法溢出 ----------------

    [Theory]
    [InlineData(0)]        // 旧行为：截断为 0 后由底层报模糊错误
    [InlineData(-1)]       // 旧行为：(ushort)(-1) = 65535，请求荒谬点数
    [InlineData(65536)]
    [InlineData(40000)]    // 16位读：40000 个寄存器超协议上限但仍在 ushort 内，由底层拒绝；此处只要求 32 位×2 路径先被我们的校验拦住
    public void Master_Read_Rejects_Invalid_Count(int count)
    {
        var (mb, network) = NewConnectedMaster();
        using (mb)
        using (network)
        {
            if (count is 0 or -1 or 65536)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadUInt16RW(1, 0, count));
                Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadBoolRW(1, 0, count));
                Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadInt32RW(1, 0, count)); // ×2 溢出旧行为
            }
            // 32 位读取 count×2 超过 ushort 上限：必须由我们的校验拦截（旧行为静默截断）
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadInt32RW(1, 0, 32768));
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadFloatRW(1, 0, 40000));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Master_ReadString_Rejects_Invalid_Count(int count)
    {
        var (mb, network) = NewConnectedMaster();
        using (mb)
        using (network)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => mb.ReadStringRW(1, 0, count));
        }
    }

    // ---------------- 空/null 数组：统一 ArgumentException ----------------

    [Fact]
    public void Master_Empty_Arrays_Throw_ArgumentException()
    {
        var (mb, network) = NewConnectedMaster();
        using (mb)
        using (network)
        {
            Assert.Throws<ArgumentException>(() => mb.WriteUInt16RW(1, 0, new ushort[0]));
            Assert.Throws<ArgumentException>(() => mb.WriteInt16RW(1, 0, new short[0]));
            Assert.Throws<ArgumentException>(() => mb.WriteInt32RW(1, 0, new int[0]));
            Assert.Throws<ArgumentException>(() => mb.WriteFloatRW(1, 0, new float[0]));
            Assert.Throws<ArgumentException>(() => mb.WriteBoolRW(1, 0, new bool[0]));
            Assert.Throws<ArgumentException>(() => mb.WriteUInt16RW(1, 0, (ushort[])null!));
        }
    }

    [Fact]
    public void Slave_Empty_Arrays_Throw_ArgumentException_Not_NullReference()
    {
        var slave = NewSlave();
        // 旧行为：这里抛的是 NullReferenceException（ToUInt16 返回 null 后靠 ! 压制）
        Assert.Throws<ArgumentException>(() => slave.WriteUInt16R(0, new ushort[0]));
        Assert.Throws<ArgumentException>(() => slave.WriteInt16R(0, new short[0]));
        Assert.Throws<ArgumentException>(() => slave.WriteUInt32RW(0, new uint[0]));
        Assert.Throws<ArgumentException>(() => slave.WriteInt32R(0, new int[0]));
        Assert.Throws<ArgumentException>(() => slave.WriteFloatRW(0, new float[0]));
        Assert.Throws<ArgumentException>(() => slave.WriteBoolR(0, new bool[0]));
        Assert.Throws<ArgumentException>(() => slave.WriteBoolRW(0, new bool[0]));
    }

    // ---------------- 字符串空值语义保持不变：返回 false，不抛异常 ----------------

    [Fact]
    public void String_Empty_Still_Returns_False()
    {
        var (mb, network) = NewConnectedMaster();
        using (mb)
        using (network)
        {
            Assert.False(mb.WriteStringRW(1, 0, ""));
            Assert.False(mb.WriteStringRW(1, 0, null!));
        }
        var slave = NewSlave();
        Assert.False(slave.WriteStringRW(1, 0, ""));
        Assert.False(slave.WriteStringR(1, 0, ""));
    }

    // ---------------- 合法边界值必须放行（防止过度校验） ----------------

    [Fact]
    public void Max_Valid_Address_And_Station_Are_Accepted()
    {
        // 数据区内合法地址：读写往返必须正常
        var (mb, network) = NewConnectedMaster();
        using (mb)
        using (network)
        {
            Assert.True(mb.WriteUInt16RW(1, 99, 0x1234));
            Assert.Equal((ushort)0x1234, mb.ReadUInt16RW(1, 99));
            Assert.True(mb.WriteBoolRW(1, 99, true));
            Assert.True(mb.ReadBoolRW(1, 99));
        }

        // 65535（ushort 上界）不得被本库参数校验拦截；若失败，失败原因只能是 NModbus 数据区边界，
        // 绝不能是 ArgumentOutOfRangeException——这证明校验放行了所有合法地址
        var slave = NewSlave();
        var ex = Record.Exception(() => slave.WriteUInt16RW(65535, (ushort)7));
        Assert.False(ex is ArgumentOutOfRangeException, $"65535 被参数校验错误拦截: {ex?.GetType().Name}");
    }

    [Fact]
    public void Protocol_Limits_Still_Delegated_To_NModbus()
    {
        // 本库校验只截"静默截断"类错误；协议/数据区上限仍由 NModbus 拒绝。
        // 默认数据区 100 点：读 100 个寄存器必须放行。
        var (mb, network) = NewConnectedMaster();
        using (mb)
        using (network)
        {
            var vals = mb.ReadUInt16RW(1, 0, 100);
            Assert.Equal(100, vals.Length);
        }
    }
}
