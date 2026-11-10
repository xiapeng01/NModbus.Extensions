using NModbus;
using NModbus.Extensions;
using NModbus.IO;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace NModbus.Extensions.Tests;

/// <summary>
/// RTU over TCP 从站网络（Tools.CreateRtuOverTcpSlaveNetwork）测试：
/// 1) 主站作为 TCP 客户端连入后，RTU 帧读写正常（补齐 NModbus 缺失的"监听 + RTU"能力）；
/// 2) 连接接入后再添加的从站会自动同步到已接入链路；多条连接共享同一组从站数据；
/// 3) 未注册站号的请求不得拖死链路（NModbus 忽略该请求时会读一个永不到来的响应）；
/// 4) 端口被占用抛 SocketException、非法 IP 抛 ArgumentException（创建失败必须同步可见）。
/// </summary>
public class RtuOverTcpSlaveNetworkTests
{
    /// <summary>模拟主站工具连入监听端口，返回绑定该链路的 RTU 主站（5 秒收发超时，避免测试挂死）。</summary>
    async Task<IModbusMaster> ConnectMasterAsync(int port)
    {
        var client = new TcpClient();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        }
        client.NoDelay = true;
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        return new ModbusFactory().CreateRtuMaster(new TcpClientAdapter(client));
    }

    [Fact]
    public async Task Listener_Serves_Rtu_Requests_And_Syncs_Slaves_After_Connect()
    {
        var network = (RtuOverTcpSlaveNetwork)Tools.CreateRtuOverTcpSlaveNetwork("127.0.0.1", 0);
        try
        {
            int port = network.Port;

            // 主站先连入，从站后添加：验证新从站会同步到已接入的链路
            using var master1 = await ConnectMasterAsync(port);

            var slave = network.CreateSlave(1);
            slave.WriteUInt16RW(0, 12345);
            Assert.Equal(slave, network.GetSlave(1));

            ushort value1 = master1.ReadHoldingRegisters(1, 0, 1)[0];
            Assert.Equal((ushort)12345, value1);

            // 第二条链路共享同一组从站数据
            using var master2 = await ConnectMasterAsync(port);
            ushort value2 = master2.ReadHoldingRegisters(1, 0, 1)[0];
            Assert.Equal((ushort)12345, value2);
        }
        finally
        {
            try { network.Dispose(); } catch { }
        }
    }

    [Fact]
    public async Task Unknown_Slave_Request_Does_Not_Stall_Link()
    {
        var network = (RtuOverTcpSlaveNetwork)Tools.CreateRtuOverTcpSlaveNetwork("127.0.0.1", 0);
        try
        {
            int port = network.Port;
            using var master = await ConnectMasterAsync(port);
            var slave = network.CreateSlave(1);
            slave.WriteUInt16RW(0, 4321);

            // 向未注册站号 2 发请求：链路侧必须丢弃（不应答），主站单次尝试应快速失败而非挂死
            master.Transport.Retries = 0;
            master.Transport.ReadTimeout = 1500;
            master.Transport.WaitToRetryMilliseconds = 100;
            await Assert.ThrowsAnyAsync<IOException>(
                () => Task.Run(() => master.ReadHoldingRegisters(2, 0, 1)));

            // 关键断言：链路必须自愈，正常站号请求照常应答（允许少量重试吸收自愈时间窗）
            master.Transport.Retries = 3;
            ushort value = (await Task.Run(() => master.ReadHoldingRegisters(1, 0, 1)))[0];
            Assert.Equal((ushort)4321, value);
        }
        finally
        {
            try { network.Dispose(); } catch { }
        }
    }

    [Fact]
    public void Listener_Port_Busy_Throws_SocketException()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            Assert.Throws<SocketException>(
                () => Tools.CreateRtuOverTcpSlaveNetwork("127.0.0.1", port));
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Listener_InvalidIp_Throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => Tools.CreateRtuOverTcpSlaveNetwork("not-an-ip", 502));
    }

    [Fact]
    public async Task Legacy_PortOnly_Overload_Listens_On_Loopback()
    {
        // 0.2.x 的 CreateRtuOverTcpSlaveNetwork(int port, ...) 签名保持不变，实现已替换为监听模式
        var network = (RtuOverTcpSlaveNetwork)Tools.CreateRtuOverTcpSlaveNetwork(0);
        try
        {
            int port = network.Port;
            using var master = await ConnectMasterAsync(port);
            network.CreateSlave(1).WriteUInt16RW(0, 777);

            Assert.Equal((ushort)777, master.ReadHoldingRegisters(1, 0, 1)[0]);
        }
        finally
        {
            try { network.Dispose(); } catch { }
        }
    }

    [Fact]
    public async Task Legacy_ConnectTimeout_Parameter_Is_Accepted_And_Ignored()
    {
        // connectTimeoutMs 仅为 0.2.x 源码兼容保留：监听模式无"连接远端"步骤，传极小值也必须正常工作
        var network = (RtuOverTcpSlaveNetwork)Tools.CreateRtuOverTcpSlaveNetwork("127.0.0.1", 0, null, connectTimeoutMs: 1);
        try
        {
            int port = network.Port;
            using var master = await ConnectMasterAsync(port);
            network.CreateSlave(1).WriteUInt16RW(0, 888);

            Assert.Equal((ushort)888, master.ReadHoldingRegisters(1, 0, 1)[0]);
        }
        finally
        {
            try { network.Dispose(); } catch { }
        }
    }
}
