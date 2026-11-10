using NModbus;
using NModbus.IO;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NModbus.Extensions
{
    /// <summary>
    /// 监听式 RTU over TCP 从站网络：在本机指定地址/端口监听 TCP 连接，
    /// 每个接入的连接上运行一条独立的 Modbus RTU 从站协议链路。
    /// NModbus 只提供 CreateSlaveNetwork(TcpListener)（Modbus TCP 协议）
    /// 和 CreateRtuSlaveNetwork(IStreamResource)（RTU 协议）两种从站网络，
    /// 缺少"监听 + RTU 帧"的组合，本类补齐该能力（供 Modbus Poll 等主站工具直接连入测试）。
    /// 所有连接共享同一组从站数据；AddSlave / RemoveSlave 会同步到所有已接入的链路。
    /// 链路空闲读超时 1 秒：主站访问未注册站号时 NModbus 会忽略该请求并阻塞读，
    /// 超时后自动回到监听循环（链路自愈，不会因一次错误请求永久哑火）。
    /// 通过 <see cref="Tools.CreateRtuOverTcpSlaveNetwork(string,int,Action{Exception}?,int)"/> 创建；
    /// 监听由构造函数自动启动，请勿手动调用 <see cref="ListenAsync"/>。
    /// </summary>
    public sealed class RtuOverTcpSlaveNetwork : IModbusSlaveNetwork, IDisposable
    {
        /// <summary>
        /// 从站链路的读超时（毫秒）。NModbus 收到"未注册站号"的请求时，ApplyRequest 返回 null 后
        /// 会调用 IgnoreResponse()，RTU 实现是去读一个永远不会到来的响应，链路将永久阻塞在该读上。
        /// 设置读超时后，该阻塞会在超时处抛 IOException，被 NModbus 的监听循环捕获后继续下一轮，
        /// 链路每 1 秒自愈一次；对正常帧无影响（RTU 帧由客户端一次性写入，字节间隔远小于 1 秒）。
        /// </summary>
        internal const int LinkReadTimeoutMs = 1000;

        readonly TcpListener listener;
        readonly ModbusFactory factory = new ModbusFactory();
        /// <summary>监听循环异常终止（非 Dispose 触发）时的回调，保证失败可观测而非静默死亡</summary>
        readonly Action<Exception>? onError;
        readonly string bindIp;
        readonly int bindPort;
        /// <summary>全部从站（按站号），链路接入时整体挂到该链路上</summary>
        readonly ConcurrentDictionary<byte, IModbusSlave> slaves = new ConcurrentDictionary<byte, IModbusSlave>();
        /// <summary>每个已接入连接对应的内部 RTU 从站网络（仅用于同步从站增删）</summary>
        readonly ConcurrentDictionary<IModbusSlaveNetwork, byte> connections = new ConcurrentDictionary<IModbusSlaveNetwork, byte>();
        /// <summary>在途的 TCP 连接（Dispose 时只做 Shutdown 唤醒阻塞读，不释放适配器）</summary>
        readonly ConcurrentDictionary<TcpClient, byte> clients = new ConcurrentDictionary<TcpClient, byte>();
        /// <summary>Dispose 时取消所有链路的监听</summary>
        readonly CancellationTokenSource disposeCts = new CancellationTokenSource();
        /// <summary>构造时缓存令牌：Dispose 流程中不再访问 CTS（避免释放后再取 Token 抛异常）</summary>
        readonly CancellationToken disposeToken;
        bool disposed;

        /// <param name="ip">监听绑定的本机地址（如 127.0.0.1 仅本机可访问，0.0.0.0 暴露到所有网卡）</param>
        /// <param name="port">监听端口，传 0 由系统分配（可用 <see cref="Port"/> 获取实际端口）。
        /// 端口被占用时抛出 SocketException，由调用方按创建失败处理</param>
        /// <param name="onError">可选：监听循环意外终止（如 accept 套接字故障）时的回调；
        /// 不传时失败会记录到 Console.Error / Trace，不会静默</param>
        public RtuOverTcpSlaveNetwork(string ip, int port, Action<Exception>? onError = null)
        {
            if (!IPAddress.TryParse(ip, out var address))
                throw new ArgumentException($"无效的 IP 地址：{ip}", nameof(ip));

            bindIp = ip;
            bindPort = port;
            this.onError = onError;
            listener = new TcpListener(address, port);
            listener.Start();
            disposeToken = disposeCts.Token;

            // 创建即后台运行，无需调用方手动启动监听
            _ = Task.Run(() => ListenAsync(CancellationToken.None));
        }

        /// <summary>实际监听的端口（构造传入 0 时由系统分配，创建后立即读取）</summary>
        public int Port
        {
            get
            {
                if (disposed) throw new ObjectDisposedException(nameof(RtuOverTcpSlaveNetwork));
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }

        /// <summary>
        /// 接入循环：为每个新连接创建一条独立的 RTU 从站链路（构造函数已在后台启动，请勿手动调用）。
        /// </summary>
        public async Task ListenAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeCts.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    TcpClient client;
                    // net5.0 的 AcceptTcpClientAsync 没有 CancellationToken 重载：
                    // 取消依赖 Dispose 里的 listener.Stop() 唤醒挂起的 accept
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException ex)
                    {
                        // Dispose 主动 Stop 也会以 SocketException 出现，属预期不上报；
                        // 其余 accept 故障若静默 break，监听就死了而调用方仍以为在运行
                        if (disposed) break;
                        ReportListenError(ex);
                        break;
                    }
                    if (disposed || linked.IsCancellationRequested) break;

                    // 每个连接独立链路：单个连接断开只丢弃该链路，不影响监听与其他连接。
                    // 必须放到独立线程上跑：NModbus 的 ModbusSerialSlaveNetwork.ListenAsync 是同步阻塞
                    // 方法（内部 Read 一直阻塞到链路断开才返回），若直接在 accept 循环线程上执行，
                    // 第一条链路就会把 accept 循环永久占死，后续所有连接永远无法接入
                    ConfigureKeepAlive(client);
                    _ = Task.Factory.StartNew(
                        () => ServeClientAsync(client),
                        TaskCreationOptions.LongRunning).Unwrap();
                }
            }
            catch (Exception ex)
            {
                // 兜底：accept 循环之外的意外异常同样必须可观测
                if (!disposed) ReportListenError(ex);
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        async Task ServeClientAsync(TcpClient client)
        {
            IModbusSlaveNetwork? network = null;
            try
            {
                if (disposed) return;
                client.NoDelay = true;
                // 读超时：让"未注册站号"请求触发的 IgnoreResponse 永久阻塞可自愈（详见 LinkReadTimeoutMs）
                client.ReceiveTimeout = LinkReadTimeoutMs;
                clients[client] = 0;
                network = factory.CreateRtuSlaveNetwork(new TcpClientAdapter(client));
                if (disposed) return;   // 停止竞态：不再把已释放网络登记为活动链路
                // 必须先登记进 connections 再拷贝从站：AddSlave / RemoveSlave 的同步遍历
                // 只有覆盖到本链路才不会漏；拷贝时用 GetSlave 判重，避免与并发的
                // AddSlave 同步重复添加（NModbus 对重复站号直接抛异常，会扯断本链路）
                connections[network] = 0;
                foreach (var slave in slaves.Values)
                {
                    if (network.GetSlave(slave.UnitId) is null) network.AddSlave(slave);
                }
                // NModbus 的 ListenAsync 是同步循环，只在每轮开始检查令牌，
                // 靠 Dispose 里的 Shutdown 唤醒阻塞读后自然退出
                await network.ListenAsync(disposeToken);
            }
            catch
            {
                // 连接断开 / 停止时的收尾异常：静默清理
            }
            finally
            {
                if (network is not null) connections.TryRemove(network, out _);
                clients.TryRemove(client, out _);
                try { network?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
            }
        }

        /// <summary>添加从站，并同步到所有已接入的链路；站号已存在时忽略。</summary>
        public void AddSlave(IModbusSlave slave)
        {
            ThrowIfDisposed();
            if (slaves.TryAdd(slave.UnitId, slave))
            {
                foreach (var connection in connections.Keys)
                {
                    try { connection.AddSlave(slave); } catch { }
                }
            }
        }

        /// <summary>按站号移除从站，并同步到所有已接入的链路；站号不存在时忽略。</summary>
        public void RemoveSlave(byte unitId)
        {
            ThrowIfDisposed();
            if (slaves.TryRemove(unitId, out _))
            {
                foreach (var connection in connections.Keys)
                {
                    try { connection.RemoveSlave(unitId); } catch { }
                }
            }
        }

        /// <summary>按站号获取从站；不存在时返回 null。</summary>
        public IModbusSlave? GetSlave(byte unitId)
            => slaves.TryGetValue(unitId, out var slave) ? slave : null;

        /// <summary>
        /// 停止监听并断开所有链路：取消监听循环、Shutdown 所有已接入连接唤醒阻塞读，
        /// 各链路在收尾时自行释放内部网络与套接字。可重复调用。
        /// </summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            disposeCts.Cancel();
            try { listener.Stop(); } catch { }

            // 只 Shutdown 套接字、不释放内部网络：
            // NModbus.TcpClientAdapter.Dispose() 会把内部 TcpClient 置为 null，而 ModbusSerialSlaveNetwork
            // 的监听循环在 IOException 分支里还会调用 DiscardInBuffer() → _tcpClient.GetStream()
            // → NullReferenceException（从 catch 块内抛出，NModbus 自己接不住）。
            // 这里让阻塞的 Read 立即返回(0 字节)，循环走到令牌检查后干净退出，
            // 各链路在 ServeClientAsync 的 finally 里自行释放网络与连接。
            foreach (var client in clients.Keys)
            {
                try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
            }

            connections.Clear();
            slaves.Clear();
            // disposeCts 不 Dispose：在途链路仍在读取 disposeToken，提前释放会引发访问已释放对象
        }

        /// <summary>监听异常终止的可观测出口：优先 onError 回调，否则记录到 Console.Error / Trace</summary>
        void ReportListenError(Exception ex)
        {
            if (onError is not null)
            {
                onError(ex);
                return;
            }
            var message = $"RTU over TCP 监听网络异常终止（{bindIp}:{bindPort}）：{ex}";
            Console.Error.WriteLine(message);
            Trace.TraceError(message);
        }

        void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(RtuOverTcpSlaveNetwork));
        }

        /// <summary>
        /// 启用 TCP keepalive：对端拔线/崩溃未挥手时链路阻塞在 Read 上不会自然结束，
        /// 靠内核探测失败后断开，触发链路收尾释放，避免死链路永久占用。
        /// </summary>
        static void ConfigureKeepAlive(TcpClient client)
        {
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
            }
            catch
            {
                // 个别平台/运行时不支持这些选项，忽略即可（KeepAlive 已尽力开启）
            }
        }
    }
}
