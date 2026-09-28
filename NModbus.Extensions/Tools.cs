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
    public static class Tools
    {
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

        public static IModbusSlaveNetwork CreateTcpClientNetwork(int port)
        {
            var fac = new ModbusFactory();
            var listener = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
            listener.Start();
            var ser = fac.CreateSlaveNetwork(listener);
            //ConfigureServer(ser);
            //var slave = fac.CreateSlave(1);
            //slave.DataStore.CoilInputs.WritePoints(0, new bool[] {true,false,true,false,true});
            //slave.DataStore.CoilDiscretes.WritePoints(0, new bool[] {true,false,true,false,true});
            //ser.AddSlave(slave);
            //ser.ListenAsync();
            Task.Run(() => { ser.ListenAsync(); });
            return ser;
        }

        public static IModbusSlaveNetwork CreateRtuClientNetwork(string portName)
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

            // 3. 创建从站实例并添加到网络
            //var slave = fac.CreateSlave(unitId: 1);
            //slave.DataStore.CoilInputs.WritePoints(0, new bool[] { true, false, true, false, true });
            //slave.DataStore.CoilDiscretes.WritePoints(0, new bool[] { true, false, true, false, true });
            //ser.AddSlave(slave);
            //ConfigureServer(ser);
            // 4. 初始化寄存器数据
            //slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 100, 200 });

            // 5. 开启异步监听（注意：需要 await 或者在异步环境中调用）
            // 如果当前环境允许 async，建议使用 await ser.ListenAsync();
            // 如果是在同步方法中强行调用，可以使用以下写法（仅作示例，不推荐在正式异步逻辑中使用）：
            // ser.ListenAsync().Wait(); 
            //Console.WriteLine("Modbus RTU 从站已启动并监听中...");
            Task.Run(() => ser.ListenAsync());
            return ser;
        }
        public static IModbusSlaveNetwork CreateRtuOverTcpClientNetwork(int port)
        {
            // 1. 初始化并打开串口
            var client = new TcpClient("127.0.0.1", port);
            var adp = new TcpClientAdapter(client);

            // 2. 创建 Modbus 工厂和传输层适配器
            var fac = new ModbusFactory();
            var ser = fac.CreateRtuSlaveNetwork(adp);

            // 3. 创建从站实例并添加到网络
            //var slave = fac.CreateSlave(unitId: 1);
            //slave.DataStore.CoilInputs.WritePoints(0, new bool[] { true, false, true, false, true });
            //slave.DataStore.CoilDiscretes.WritePoints(0, new bool[] { true, false, true, false, true });
            //ser.AddSlave(slave);
            //ConfigureServer(ser);
            // 4. 初始化寄存器数据
            //slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 100, 200 });

            // 5. 开启异步监听（注意：需要 await 或者在异步环境中调用）
            // 如果当前环境允许 async，建议使用 await ser.ListenAsync();
            // 如果是在同步方法中强行调用，可以使用以下写法（仅作示例，不推荐在正式异步逻辑中使用）：
            // ser.ListenAsync().Wait(); 
            //Console.WriteLine("Modbus RTU 从站已启动并监听中...");
            Task.Run(() => ser.ListenAsync());
            return ser;//串口不需要监听,从站可以前期添加,也可后期添加,也可删除
        }

        public static IModbusMaster CreateTcpMaster(string ip, int port)
        {
            var fac = new ModbusFactory();
            var client = new TcpClient(ip, port);
            return fac.CreateMaster(client);
        }

        public static IModbusMaster CreateRtuMaster(string portName, int baudRate = 9600, int dataBits = 8, Parity parity = Parity.None, StopBits stopBits = StopBits.One)
        {
            var fac = new ModbusFactory();
            var sp = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            sp.Open();
            return fac.CreateRtuMaster(sp);
        }

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

        public static IModbusSlave CreateSlave(this IModbusSlaveNetwork ser, byte stationNo)
        {
            var fac = new ModbusFactory();
            var slave = fac.CreateSlave(stationNo); 
            ser.AddSlave(slave);
            return slave;
        }

    }
}
