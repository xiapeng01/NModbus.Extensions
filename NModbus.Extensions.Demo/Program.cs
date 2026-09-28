using Dumpify;
using System;

namespace NModbus.Extensions.Demo
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                //创建Modbus从站
                var ser = Tools.CreateTcpClientNetwork(502);//ip默认为127.0.0.1,可改端口号                
                var slave = ser.CreateSlave(1);//配置从站站号1

                //创建主站
                var mb = Tools.CreateTcpMaster("127.0.0.1", 502);
                //mb.SetEndian(EnumEndian.CDAB);//这一步可以不做,默认就是CDAB的,和HslCommunication兼容,也可以切换其它字节序

                //bool
                mb.WriteBoolRW(1, 100, new bool[] { true, false, true, false, true });
                mb.ReadBoolRW(1, 100, 5).Dump("Bool");

                //int16
                mb.WriteInt16RW(1, 100, new short[] { -1, -2, -3, -4, -5 });
                mb.ReadInt16RW(1, 100, 5).Dump("Int16");

                //Uint16
                mb.WriteUInt16RW(1, 100, new ushort[] { 1, 2, 3, 4, 5 });
                mb.ReadUInt16RW(1, 100, 5).Dump("UInt16");

                //int32
                mb.WriteInt32RW(1, 100, new int[] { -10, -20, -30, -40, -50 });
                mb.ReadInt32RW(1, 100, 5).Dump("Int32");

                //Uint32
                mb.WriteUInt32RW(1, 100, new uint[] { 10, 20, 30, 40, 50 });
                mb.ReadUInt32RW(1, 100, 5).Dump("UInt32");

                //float
                mb.WriteFloatRW(1, 100, new float[] { -1.1f, -2.2f, -3.3f, -4.4f, -5.5f });
                mb.ReadFloatRW(1, 100, 5).Dump("Float");

                //string
                mb.WriteStringRW(1, 100, "ABCDE");
                mb.ReadStringRW(1, 100, 5).Dump("String");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
