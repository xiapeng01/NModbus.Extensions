using Dumpify;
using Microsoft.VisualBasic;
using System;

namespace NModbus.Extensions.Demo
{
    class Program
    { 
        private static void Main(string[] args)
        {
            var nw = Tools.CreateTcpSlaveNetwork(600);
            var slave = nw.CreateSlave(1);

            var mb = Tools.CreateTcpMaster("127.0.0.1", 600);

            //CDAB
            Console.WriteLine("----------------------CDAB--------------------------");
            mb.SetEndian(EnumEndian.CDAB);
            Foo(mb);//读写单个
            Foo2(mb);//读写多个

            //DCBA
            Console.WriteLine("----------------------DCBA--------------------------");
            mb.SetEndian(EnumEndian.DCBA);
            Foo(mb);//读写单个
            Foo2(mb);//读写多个

            ////ABCD
            Console.WriteLine("----------------------ABCD--------------------------");
            mb.SetEndian(EnumEndian.ABCD);
            Foo(mb);//读写单个
            Foo2(mb);//读写多个

            ////BADC
            Console.WriteLine("----------------------BADC--------------------------");
            mb.SetEndian(EnumEndian.BADC);
            Foo( mb);//读写单个
            Foo2( mb);//读写多个

        }

        static void Foo(IModbusMaster mb)
        {
            //bool值
            mb.WriteBoolRW(1, 0, true);//用Nmodbus.Extensions写入
            mb.ReadBoolRW(1, 0).Dump("NModbus.Extensions.bool");//用Nmodbus.Extensions读取

            //Int16
            mb.WriteInt16RW(1, 0, -12345);
            mb.ReadInt16RW(1, 0).Dump("NModbus.Extensions.Int16");

            //UInt16
            mb.WriteUInt16RW(1, 0, 12345);
            mb.ReadUInt16RW(1, 0).Dump("NModbus.Extensions.UInt16");

            //Int32
            mb.WriteInt32RW(1, 0, -123456789);
            mb.ReadInt32RW(1, 0).Dump("NModbus.Extensions.Int32");

            //UInt32
            mb.WriteUInt32RW(1, 0, 123456789);
            mb.ReadUInt32RW(1,0).Dump("NModbus.Extensions.UInt32");

            //float
            mb.WriteFloatRW(1, 0, -1.23456F);
            mb.ReadFloatRW(1, 0).Dump("NModbus.Extensions.Float");

            //string
            mb.WriteStringRW(1, 0, "He");
            mb.ReadStringRW(1, 0, 2).Dump("NModbus.Extensions.String");
        }
        static void Foo2(IModbusMaster mb)
        {
            //bool值
            mb.WriteBoolRW(1, 0, new bool[] { true, false, true, false, true });//用Nmodbus.Extensions写入
            mb.ReadBoolRW(1, 0, 5).Dump("NModbus.Extensions.bool");//用Nmodbus.Extensions读取

            //Int16
            mb.WriteInt16RW(1, 0, new short[] { -12345, -23456, -14567, -15678, -16789 });
            mb.ReadInt16RW(1, 0, 5).Dump("NModbus.Extensions.Int16");

            //UInt16
            mb.WriteUInt16RW(1, 0, new ushort[] { 12345, 23456, 34567, 45678, 56789 });
            mb.ReadUInt16RW(1, 0, 5).Dump("NModbus.Extensions.UInt16");

            //Int32
            mb.WriteInt32RW(1, 0, new int[] { -123456789, -234567890, -345678901, -456789012, -567890123 });
            mb.ReadInt32RW(1, 0, 5).Dump("NModbus.Extensions.Int32");

            //UInt32
            mb.WriteUInt32RW(1, 0, new uint[] { 123456789, 234567890, 345678901, 456789012, 567890123 });
            mb.ReadUInt32RW(1, 0, 5).Dump("NModbus.Extensions.UInt32");

            //float
            mb.WriteFloatRW(1, 0, new float[] { -1.23456F, -2.34567F, -3.45678F, -4.56789F, -5.67890F });
            mb.ReadFloatRW(1, 0, 5).Dump("NModbus.Extensions.Float");

            //string
            mb.WriteStringRW(1, 0, "Hello World");
            mb.ReadStringRW(1, 0, 11).Dump("NModbus.Extensions.String");
        }
    }
}