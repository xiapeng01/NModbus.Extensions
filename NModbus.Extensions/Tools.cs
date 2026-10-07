using NModbus.IO;
using NModbus.Serial;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NModbus.Extensions
{
    /// <summary>
    /// Modbus 连接创建工具：快速创建主站（TCP / RTU 串口 / RTU over TCP）与从站网络。
    /// </summary>
    public static class Tools
    {
        /// <summary>
        /// 生成 n 个 [0,1) 区间的随机 <see cref="double"/>（调试用）。
        /// </summary>
        /// <param name="n">生成个数。</param>
        /// <returns>随机数数组。</returns>
        public static double[] CreateRandom(int n)
        {
            var rd = new Random(Guid.NewGuid().GetHashCode());
            var lst = new List<double>();
            for (int i = 0; i < n; i++)
            {
                lst.Add(rd.NextDouble());
            }
            return lst.ToArray();
        }

        //统一监听入口：ListenAsync 返回的 Task 必须被观察，否则端口被占用/串口打不开等
        //启动失败会静默进入 UnobservedTaskException，调用方误以为从站已就绪（修复7）。
        //传入 onError 回调可精确感知失败；不传时保持原有异常传播行为（任务置为 Faulted），不会额外吞掉异常。
        static void StartListening(IModbusSlaveNetwork ser, Action<Exception>? onError = null)
        {
            Task.Run(async () =>
            {
                try { await ser.ListenAsync(); }
                catch (Exception ex) { if (onError is null) throw; onError(ex); }
            });
        }

        /// <summary>
        /// 创建 Modbus TCP 从站网络：在 127.0.0.1 指定端口监听并后台异步运行。
        /// 创建后用 <see cref="CreateSlave"/> 扩展添加从站，再用 Write*R / Write*RW 系列方法填充数据。
        /// </summary>
        /// <param name="port">监听端口（如 502）。</param>
        /// <param name="onError">可选：启动/监听失败（端口被占用等）时的异常回调；不传则异常只落在后台任务上，调用方无法感知。</param>
        /// <returns>从站网络对象。</returns>
        public static IModbusSlaveNetwork CreateTcpSlaveNetwork(int port, Action<Exception>? onError = null)
        {
            var fac = new ModbusFactory();
            var listener = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
            listener.Start();
            var ser = fac.CreateSlaveNetwork(listener);
            StartListening(ser, onError);
            return ser;
        }

        /// <summary>
        /// 创建 Modbus RTU 串口从站网络（固定 115200, 8, N, 1），后台异步监听。
        /// </summary>
        /// <param name="portName">串口名（如 COM1）。</param>
        /// <param name="onError">可选：串口打开/监听失败时的异常回调。</param>
        /// <returns>从站网络对象。</returns>
        public static IModbusSlaveNetwork CreateRtuSlaveNetwork(string portName, Action<Exception>? onError = null)
        {
            // 1. 初始化并打开串口
            var sp = new SerialPort(portName)
            {
                BaudRate = 115200,
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One
            };
            sp.Open();

            // 2. 创建 Modbus 工厂和传输层适配器
            var fac = new ModbusFactory();
            var transport = new SerialPortAdapter(sp);
            var ser = fac.CreateRtuSlaveNetwork(transport);

            // 3. 开启异步监听（异常通过 onError 回传，不再静默丢失）
            StartListening(ser, onError);
            return ser;
        }
        /// <summary>
        /// 创建 RTU over TCP 从站网络：作为 TCP 客户端连接 127.0.0.1 指定端口（通常对接串口服务器/透传网关），
        /// 在该链路上运行 RTU 从站协议，后台异步监听。
        /// </summary>
        /// <param name="port">远端 TCP 端口。</param>
        /// <param name="onError">可选：连接/监听失败时的异常回调。</param>
        /// <returns>从站网络对象；从站可以前期添加、后期添加，也可删除。</returns>
        public static IModbusSlaveNetwork CreateRtuOverTcpSlaveNetwork(int port, Action<Exception>? onError = null)
        {
            // 1. 连接远端
            var client = new TcpClient("127.0.0.1", port);
            var adp = new TcpClientAdapter(client);

            // 2. 创建 Modbus 工厂和传输层适配器
            var fac = new ModbusFactory();
            var ser = fac.CreateRtuSlaveNetwork(adp);

            // 3. 开启异步监听（异常通过 onError 回传，不再静默丢失）
            StartListening(ser, onError);
            return ser;//从站可以前期添加,也可后期添加,也可删除
        }

        /// <summary>
        /// 创建 Modbus TCP 主站并连接到指定设备。
        /// 建议创建后调用 <see cref="NModbusExtensions.SetEndian(IModbusMaster, EnumEndian)"/> 设置与设备匹配的字节序（默认 CDAB）。
        /// </summary>
        /// <param name="ip">设备 IP 地址。</param>
        /// <param name="port">设备端口（默认 502）。</param>
        /// <returns>Modbus TCP 主站对象。</returns>
        public static IModbusMaster CreateTcpMaster(string ip, int port)
        {
            var fac = new ModbusFactory();
            var client = new TcpClient(ip, port);
            return fac.CreateMaster(client);
        }

        /// <summary>
        /// 创建 Modbus RTU 串口主站（打开串口并绑定协议）。
        /// </summary>
        /// <param name="portName">串口名（如 COM1）。</param>
        /// <param name="baudRate">波特率，默认 9600（注意：本库 RTU 从站默认 115200，主从直连需参数一致）。</param>
        /// <param name="dataBits">数据位，默认 8。</param>
        /// <param name="parity">校验位，默认 <see cref="Parity.None"/>。</param>
        /// <param name="stopBits">停止位，默认 <see cref="StopBits.One"/>。</param>
        /// <returns>Modbus RTU 主站对象。</returns>
        public static IModbusMaster CreateRtuMaster(string portName, int baudRate = 9600, int dataBits = 8, Parity parity = Parity.None, StopBits stopBits = StopBits.One)
        {
            var fac = new ModbusFactory();
            var sp = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            sp.Open();
            return fac.CreateRtuMaster(sp);
        }

        /// <summary>
        /// 创建 RTU over TCP 主站：作为 TCP 客户端连接指定远端（串口服务器/透传网关），在其上运行 RTU 主站协议。
        /// </summary>
        /// <param name="ip">远端 IP 地址。</param>
        /// <param name="port">远端 TCP 端口。</param>
        /// <returns>Modbus RTU 主站对象。</returns>
        public static IModbusMaster CreateRtuOverTcpMaster(string ip, int port)
        {
            var fac = new ModbusFactory();
            var client = new TcpClient(ip, port);
            var adp = new TcpClientAdapter(client);
            return fac.CreateRtuMaster(adp);//此处要用Rtu
        }

        //public static IModbusSlave ConfigureServer(IModbusSlaveNetwork ser, byte stationNo)
        //{
        //    var fac = new ModbusFactory();
        //    var slave = fac.CreateSlave(stationNo);
        //    slave.DataStore.CoilInputs.WritePoints(0, new bool[] { true, false, true, false, true });
        //    slave.DataStore.CoilDiscretes.WritePoints(0, new bool[] { true, false, true, false, true });
        //    ser.AddSlave(slave);
        //    return slave;
        //}

        /// <summary>
        /// 向从站网络添加指定站号的从站，随即可用 Write*R / Write*RW 扩展向其数据区填充数据。
        /// </summary>
        /// <param name="ser">从站网络（由 Create*SlaveNetwork 创建）。</param>
        /// <param name="stationNo">从站站号（1-247）。</param>
        /// <returns>新创建的从站对象。</returns>
        public static IModbusSlave CreateSlave(this IModbusSlaveNetwork ser, byte stationNo)
        {
            var fac = new ModbusFactory();
            var slave = fac.CreateSlave(stationNo); 
            ser.AddSlave(slave);
            return slave;
        }

    }
}
