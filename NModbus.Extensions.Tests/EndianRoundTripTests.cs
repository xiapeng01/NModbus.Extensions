using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Extensions;
using Xunit;

namespace NModbus.Extensions.Tests;

/// <summary>
/// 字节序往返回归测试：主站写入的值必须能原样读回，且线上裸寄存器布局符合
/// 与 HslCommunication 对齐的既定语义（0.2.0 修复的核心，防止再次分叉）。
/// 每个测试使用独立的进程内 TCP 主从对，互不干扰。
/// </summary>
public class EndianRoundTripTests : IDisposable
{
    private static readonly EnumEndian[] AllEndians =
        { EnumEndian.ABCD, EnumEndian.BADC, EnumEndian.CDAB, EnumEndian.DCBA };

    /// <summary>进程内 Modbus TCP 从站 + 主站（随机端口）</summary>
    private sealed class Harness : IDisposable
    {
        public IModbusMaster Master { get; }
        public IModbusSlave Slave { get; }
        private readonly IModbusSlaveNetwork _network;
        private readonly TcpListener _listener;

        public Harness()
        {
            var fac = new ModbusFactory();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _network = fac.CreateSlaveNetwork(_listener);
            Slave = fac.CreateSlave(1);
            _network.AddSlave(Slave);
            _ = Task.Run(async () => { try { await _network.ListenAsync(); } catch { /* 测试内忽略监听结束异常 */ } });

            Master = fac.CreateMaster(new TcpClient("127.0.0.1", port));
        }

        /// <summary>主从站同设字节序</summary>
        public Harness WithEndian(EnumEndian e)
        {
            Master.SetEndian(e);
            Slave.SetEndian(e);
            return this;
        }

        public ushort[] RawHolding(ushort address, ushort count)
            => Slave.DataStore.HoldingRegisters.ReadPoints(address, count);

        public void Dispose()
        {
            Master.Dispose();
            (_network as IDisposable)?.Dispose();
            _listener.Stop();
        }
    }

    private readonly List<Harness> _created = new();
    private Harness NewHarness(EnumEndian? endian = null)
    {
        var h = new Harness();
        if (endian is not null) h.WithEndian(endian.Value);
        _created.Add(h);
        return h;
    }

    public void Dispose()
    {
        foreach (var h in _created) h.Dispose();
    }

    // ---------------------------------------------------------------
    // 1. 往返自洽：主站写、主站读，四种字节序 × 六种类型 × 单值/数组
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void UInt16_Scalar_RoundTrip(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        foreach (ushort v in new ushort[] { 0x1234, 1, 12345, 0xFF00, 0x00FF })
        {
            Assert.True(h.Master.WriteUInt16RW(1, 0, v));
            Assert.Equal(v, h.Master.ReadUInt16RW(1, 0));
        }
    }

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void Int16_Scalar_RoundTrip(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        foreach (short v in new short[] { -12345, -1, 32767, -32768, 0x1234 })
        {
            Assert.True(h.Master.WriteInt16RW(1, 0, v));
            Assert.Equal(v, h.Master.ReadInt16RW(1, 0));
        }
    }

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void Int32_Scalar_RoundTrip(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        foreach (int v in new int[] { -123456789, 0x11223344, -1, int.MaxValue, 1 })
        {
            Assert.True(h.Master.WriteInt32RW(1, 0, v));
            Assert.Equal(v, h.Master.ReadInt32RW(1, 0));
        }
    }

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void UInt32_Scalar_RoundTrip(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        foreach (uint v in new uint[] { 0x11223344, 1, 123456789, 0xFFFFFFFF })
        {
            Assert.True(h.Master.WriteUInt32RW(1, 0, v));
            Assert.Equal(v, h.Master.ReadUInt32RW(1, 0));
        }
    }

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void Float_Scalar_RoundTrip(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        foreach (float v in new float[] { -1.23456f, 3.14159f, -1.1f, 1e10f, -0.001f })
        {
            Assert.True(h.Master.WriteFloatRW(1, 0, v));
            Assert.Equal(v, h.Master.ReadFloatRW(1, 0));
        }
    }

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void Array_RoundTrip_AllTypes(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        var us = new ushort[] { 0x1234, 1, 12345, 0xFF00, 0x00FF };
        var s = new short[] { -12345, -23456, -14567, -15678, -16789 };
        var i = new int[] { -123456789, -234567890, -345678901 };
        var u = new uint[] { 123456789, 234567890, 345678901 };
        var f = new float[] { -1.23456f, -2.34567f, -3.45678f };

        Assert.True(h.Master.WriteUInt16RW(1, 0, us));
        Assert.Equal(us, h.Master.ReadUInt16RW(1, 0, us.Length));

        Assert.True(h.Master.WriteInt16RW(1, 0, s));
        Assert.Equal(s, h.Master.ReadInt16RW(1, 0, s.Length));

        Assert.True(h.Master.WriteInt32RW(1, 10, i));
        Assert.Equal(i, h.Master.ReadInt32RW(1, 10, i.Length));

        Assert.True(h.Master.WriteUInt32RW(1, 20, u));
        Assert.Equal(u, h.Master.ReadUInt32RW(1, 20, u.Length));

        Assert.True(h.Master.WriteFloatRW(1, 30, f));
        Assert.Equal(f, h.Master.ReadFloatRW(1, 30, f.Length));
    }

    // ---------------------------------------------------------------
    // 2. 单值重载与数组重载必须落到同一线上数据（16 位分叉 bug 的回归锁）
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void Scalar_And_Array_Overloads_Produce_Same_Wire_Bytes(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        foreach (ushort v in new ushort[] { 0x1234, 1, 0xFF00, 0x00FF })
        {
            h.Master.WriteUInt16RW(1, 0, v);
            h.Master.WriteUInt16RW(1, 1, new ushort[] { v });
            Assert.True(h.RawHolding(0, 1)[0] == h.RawHolding(1, 1)[0], $"值 {v:X4} 在 {endian} 下两个写重载线上数据不同");

            h.Master.WriteInt16RW(1, 2, (short)v);
            h.Master.WriteInt16RW(1, 3, new short[] { (short)v });
            Assert.True(h.RawHolding(2, 1)[0] == h.RawHolding(3, 1)[0], $"Int16 {v:X4} 在 {endian} 下两个写重载线上数据不同");
        }
    }

    // ---------------------------------------------------------------
    // 3. 已知答案：线上裸寄存器布局（与 HslCommunication DataFormat 对齐的既定语义）
    //    写 0x11223344：ABCD=[高字在前], CDAB=[低字在前]，字内高低字节按各模式定义
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("ABCD", 0x1234)]
    [InlineData("CDAB", 0x1234)]
    [InlineData("BADC", 0x3412)]
    [InlineData("DCBA", 0x3412)]
    public void UInt16_Wire_Layout_Known_Answer(string endian, ushort expectedRaw)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        h.Master.WriteUInt16RW(1, 0, (ushort)0x1234);
        Assert.Equal(expectedRaw, h.RawHolding(0, 1)[0]);
    }

    [Theory]
    [InlineData("ABCD", 0x1122, 0x3344)] // 线上字节 11 22 33 44 = A B C D
    [InlineData("BADC", 0x2211, 0x4433)] // 线上字节 22 11 44 33 = B A D C
    [InlineData("CDAB", 0x3344, 0x1122)] // 线上字节 33 44 11 22 = C D A B
    [InlineData("DCBA", 0x4433, 0x2211)] // 线上字节 44 33 22 11 = D C B A
    public void Int32_Wire_Layout_Known_Answer(string endian, ushort expectedRegAtAddr, ushort expectedRegAtAddrPlus1)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        h.Master.WriteInt32RW(1, 0, 0x11223344);
        var raw = h.RawHolding(0, 2);
        Assert.Equal(new ushort[] { expectedRegAtAddr, expectedRegAtAddrPlus1 }, raw);
    }

    // ---------------------------------------------------------------
    // 4. 字符串：往返 + count 为 UTF8 字节数口径 + 不随 SetEndian 变化（设计如此）
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void String_RoundTrip(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));
        Assert.True(h.Master.WriteStringRW(1, 0, "Hello World"));
        Assert.Equal("Hello World", h.Master.ReadStringRW(1, 0, 11)); // count=UTF8字节数
    }

    [Fact]
    public void String_Count_Is_Utf8_Byte_Count_Not_Char_Count()
    {
        var h = NewHarness(EnumEndian.CDAB);
        Assert.True(h.Master.WriteStringRW(1, 0, "中文测试")); // UTF8 = 12 字节
        Assert.Equal("中文测试", h.Master.ReadStringRW(1, 0, 12));
        Assert.NotEqual("中文测试", h.Master.ReadStringRW(1, 0, 4)); // 按字符数传 4 会读不全
    }

    [Fact]
    public void String_Wire_Layout_Independent_Of_Endian()
    {
        // 字符串通道固定为 UTF8 文本顺序，不随 SetEndian 改变（源码注释的设计约定）
        var h = NewHarness();
        h.Master.SetEndian(EnumEndian.CDAB);
        h.Master.WriteStringRW(1, 0, "ABCD");
        h.Master.SetEndian(EnumEndian.ABCD);
        Assert.Equal("ABCD", h.Master.ReadStringRW(1, 0, 4));
    }

    [Fact]
    public void String_Empty_Returns_False_Without_Throwing()
    {
        var h = NewHarness(EnumEndian.CDAB);
        Assert.False(h.Master.WriteStringRW(1, 0, ""));
    }

    // ---------------------------------------------------------------
    // 5. 从站侧写入（R/RW）→ 主站同字节序读取，往返自洽
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("ABCD")]
    [InlineData("BADC")]
    [InlineData("CDAB")]
    [InlineData("DCBA")]
    public void Slave_Write_Master_Read_RoundTrip(string endian)
    {
        var h = NewHarness(Enum.Parse<EnumEndian>(endian));

        h.Slave.WriteFloatR(0, 3.14159f);
        Assert.Equal(3.14159f, h.Master.ReadFloatR(1, 0));

        h.Slave.WriteInt32R(2, -123456789);
        Assert.Equal(-123456789, h.Master.ReadInt32R(1, 2));

        h.Slave.WriteUInt16R(4, (ushort)0x1234);
        Assert.Equal((ushort)0x1234, h.Master.ReadUInt16R(1, 4));

        h.Slave.WriteInt16RW(6, (short)-12345);
        Assert.Equal((short)-12345, h.Master.ReadInt16RW(1, 6));

        h.Slave.WriteBoolR(0, new bool[] { true, false, true });
        Assert.Equal(new bool[] { true, false, true }, h.Master.ReadBoolR(1, 0, 3));

        h.Slave.WriteStringRW(1, 20, "DEVICE-A");
        Assert.Equal("DEVICE-A", h.Master.ReadStringRW(1, 20, 8));
    }

    // ---------------------------------------------------------------
    // 6. SetEndian 按对象存储：TcpClient 上的设置不传导到 Master（当前设计，显式锁定）
    // ---------------------------------------------------------------

    [Fact]
    public void Endian_Is_Stored_Per_Object_And_Does_Not_Propagate()
    {
        using var client = new TcpClient();
        client.SetEndian(EnumEndian.ABCD);
        Assert.Equal(EnumEndian.ABCD, client.GetEndian());

        // 用该 client 新建的 master 未显式 SetEndian 时仍是默认 CDAB
        var fac = new ModbusFactory();
        using var master = fac.CreateMaster(client);
        Assert.Equal(EnumEndian.CDAB, master.GetEndian());
    }
}
