using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NModbus.IO;
using NModbus.Serial;

namespace NModbus.Extensions
{

    public enum EnumEndian { DCBA, CDAB, BADC, ABCD } 

    public static class NModbusExtensions
    {
        // MemoryPool<T> is abstract - use the shared instance instead of new()
        static MemoryPool<UInt16> p16 = MemoryPool<UInt16>.Shared;
        static MemoryPool<byte> pByte= MemoryPool<byte>.Shared;

        static ConcurrentDictionary<object, EnumEndian> dicEndian = new();

        public static void SetEndian(this TcpClient client, EnumEndian endian)
        {
            dicEndian[client] = endian;
        }

        public static void SetEndian(this SerialPort sp, EnumEndian endian)
        {
            dicEndian[sp] = endian;
        }

        public static void SetEndian(this IModbusMaster mb, EnumEndian endian)
        {
            dicEndian[mb] = endian;
        }
        public static void SetEndian(this IModbusSlave mb, EnumEndian endian)
        {
            dicEndian[mb] = endian;
        }

        public static EnumEndian GetEndian(this TcpClient client)
        {
            if (!dicEndian.ContainsKey(client)) dicEndian[client] = EnumEndian.CDAB;
            return dicEndian[client];
        }


        public static EnumEndian GetEndian(this SerialPort sp)
        {
            if (!dicEndian.ContainsKey(sp)) dicEndian[sp] = EnumEndian.CDAB;
            return dicEndian[sp];
        }

        public static EnumEndian GetEndian(this IModbusMaster mb)
        {
            if (!dicEndian.ContainsKey(mb)) dicEndian[mb] = EnumEndian.CDAB;
            return dicEndian[mb];
        }
        public static EnumEndian GetEndian(this IModbusSlave mb)
        {
            if (!dicEndian.ContainsKey(mb)) dicEndian[mb] = EnumEndian.CDAB;
            return dicEndian[mb];
        }
         
        public static UInt16 Swap(this UInt16 value)
        {
            return (UInt16)(value >> 8 | value << 8);
        }
        public static Int16 Swap(this Int16 value)
        {
            return (Int16)(value >> 8 | value << 8);
        }

        //工具方法
        //16位方法组
        public static UInt16[] ToUInt16(this Int16[] values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt16[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 2);
            return ret;
        }

        public static UInt16[] ToUInt16(this Int32[] values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt16[values.Length * 2];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 2);
            return ret;
        }

        public static UInt16[] ToUInt16(this UInt32[] values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt16[values.Length * 2];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 2);
            return ret;
        }

        public static UInt16[] ToUInt16(this float[] values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt16[values.Length * 2];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 2);
            return ret;
        }

        /// <summary>此处一律不做倒序，如果要交换高低字节，在拿到UInt16数组后再交换</summary>
        public static UInt16[] ToUInt16(this string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return null;
            var ary1 = Encoding.UTF8.GetBytes(str);
            if (ary1.Length % 2 != 0) ary1 = ary1.Append((byte)0x00).ToArray();
            var ret = new UInt16[ary1.Length / 2];
            Buffer.BlockCopy(ary1, 0, ret, 0, ary1.Length);
            return ret;
        }

        public static Int16[] ToInt16(this UInt16[] values)
        {
            var ret = new Int16[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);
            return ret;
        }

        public static Int32[] ToInt32(this UInt16[] values)
        {
            var ret = new Int32[values.Length / 2];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);
            return ret;
        }

        public static UInt32[] ToUInt32(this UInt16[] values)
        {
            var ret = new UInt32[values.Length / 2];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);
            return ret;
        }

        public static float[] ToFloat(this UInt16[] values)
        {
            var ret = new float[values.Length / 2];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);
            return ret;
        }

        public static string ToString(this UInt16[] values)
        {
            var ary = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, ary, 0, values.Length * 2);            
            return Encoding.UTF8.GetString(ary).Trim('\0');//过滤末尾的\0
        }

        //32位方法组-字符串一律使用16位
        public static UInt32[] ToUInt32(this Int32[] values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt32[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 4);
            return ret;
        }

        public static UInt32[] ToUInt32(this float[] values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt32[values.Length * 2];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 4);
            return ret;
        }

        public static Int32[] ToInt32(this UInt32[] values)
        {
            var ret = new Int32[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 4);
            return ret;
        }

        public static float[] ToFloat(this UInt32[] values)
        {
            var ret = new float[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 4);
            return ret;
        }

        //toTargetEndian 相当于主机字节序转网络字节序,C#默认字节序是DCBA的,
        public static UInt16[] ToTargetEndian(this UInt16[] values, EnumEndian targetEndian)
        {
            //16位的转换就2种情况,要么交换高低字,要么不交换
            switch (targetEndian)
            {
                case EnumEndian.ABCD:
                case EnumEndian.BADC:
                    var ret = new UInt16[values.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        ret[i] = values[i].Swap();
                    }
                    return ret;
                default://小端字节序不转换
                case EnumEndian.CDAB:
                case EnumEndian.DCBA:
                    return values;
            }
        }

        public static Int16[] ToTargetEndian(this Int16[] values, EnumEndian targetEndian)
        {
            //16位的转换就2种情况,要么交换高低字,要么不交换
            switch (targetEndian)
            {
                case EnumEndian.ABCD:
                case EnumEndian.BADC:
                    var ret = new Int16[values.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        ret[i] = values[i].Swap();
                    }
                    return ret;
                default://小端字节序不转换
                case EnumEndian.CDAB:
                case EnumEndian.DCBA:
                    return values;
            }
        }

        /// <summary>
        /// 通用字节序转换方法（适用于 Int32, UInt32, float 等 4 字节类型）
        /// </summary>
        public static T[] ConvertEndian<T>(this T[] values, EnumEndian targetEndian) where T : struct
        {
            if (values == null || values.Length == 0) return values;
            if (Marshal.SizeOf<T>() != 4)
                throw new NotSupportedException($"仅支持 4 字节类型，当前类型为 {typeof(T).Name}");

            var result = new T[values.Length];
            var srcSpan = MemoryMarshal.Cast<T, byte>(values.AsSpan());
            var dstSpan = MemoryMarshal.Cast<T, byte>(result.AsSpan());

            for (int i = 0; i < values.Length; i++)
            {
                int offset = i * 4;
                switch (targetEndian)
                {
                    case EnumEndian.ABCD: // 完全反转 
                        dstSpan[offset] = srcSpan[offset + 2];
                        dstSpan[offset + 1] = srcSpan[offset + 3];
                        dstSpan[offset + 2] = srcSpan[offset];
                        dstSpan[offset + 3] = srcSpan[offset + 1];
                        break;
                    case EnumEndian.BADC: // 双字交换 
                        dstSpan[offset] = srcSpan[offset + 3];
                        dstSpan[offset + 1] = srcSpan[offset + 2];
                        dstSpan[offset + 2] = srcSpan[offset + 1];
                        dstSpan[offset + 3] = srcSpan[offset];
                        break;
                    case EnumEndian.CDAB: // 单字交换 
                        srcSpan.Slice(offset, 4).CopyTo(dstSpan.Slice(offset, 4));
                        break;
                    default: // DCBA 不转换，直接复制
                    case EnumEndian.DCBA:
                        dstSpan[offset] = srcSpan[offset + 1];
                        dstSpan[offset + 1] = srcSpan[offset];
                        dstSpan[offset + 2] = srcSpan[offset + 3];
                        dstSpan[offset + 3] = srcSpan[offset + 2]; 
                        break;
                }
            }
            return result;
        }

        // 保留原有方法签名以兼容旧代码（内部调用泛型方法）
        public static Int32[] ToTargetEndian(this Int32[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian);
        public static UInt32[] ToTargetEndian(this UInt32[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian);
        public static float[] ToTargetEndian(this float[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian);

        // FormTargetEndian 与 ToTargetEndian 逻辑相同，可直接复用
        public static Int32[] FormTargetEndian(this Int32[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian);
        public static UInt32[] FormTargetEndian(this UInt32[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian);
        public static float[] FormTargetEndian(this float[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian);

        //从站用
        //写只读寄存器-输入寄存器
        //bool操作
        public static bool WriteBoolR(this IModbusSlave slave, int address, bool[] values)
        {
            slave.DataStore.CoilInputs.WritePoints((ushort)address, values);
            return true;
        }

        public static bool WriteBoolR(this IModbusSlave slave, int address, bool value)
        {
            return WriteBoolR(slave, address, new bool[] { value });
        }

        //16位操作
        public static bool WriteUInt16R(this IModbusSlave slave, int address, UInt16[] values)
        {
            slave.DataStore.InputRegisters.WritePoints((ushort)address, values.ToTargetEndian(GetEndian(slave)));
            return true;
        }

        public static bool WriteUInt16R(this IModbusSlave slave, int address, UInt16 value)
        {
            return WriteUInt16R(slave, address, new UInt16[] { value });
        }

        public static bool WriteInt16R(this IModbusSlave slave, int address, Int16[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(slave));
            slave.DataStore.InputRegisters.WritePoints((ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteInt16R(this IModbusSlave slave, int address, Int16 value)
        {
            return WriteInt16R(slave, address, new Int16[] { value });
        }

        //32位操作符
        public static bool WriteUInt32R(this IModbusSlave slave, int address, UInt32[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(slave));
            slave.DataStore.InputRegisters.WritePoints((ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteUInt32R(this IModbusSlave slave, int address, UInt32 value)
        {
            return WriteUInt32R(slave, address, new UInt32[] { value });
        }

        public static bool WriteInt32R(this IModbusSlave slave, int address, Int32[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(slave));
            slave.DataStore.InputRegisters.WritePoints((ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteInt32R(this IModbusSlave slave, int address, Int32 value)
        {
            return WriteInt32R(slave, address, new Int32[] { value });
        }

        //32位浮点数
        public static bool WriteFloatR(this IModbusSlave slave, int address, float[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(slave));
            slave.DataStore.InputRegisters.WritePoints((ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteFloatR(this IModbusSlave slave, int address, float value)
        {
            return WriteFloatR(slave, address, new float[] { value });
        }


        public static bool WriteStringR(this IModbusSlave slave, int stationNo, int address, string str)
        { 
            var writeData = str.ToUInt16();
            for (int i = 0; i < writeData.Length; i++)
            {
                writeData[i] = writeData[i].Swap();//交换高低字
            }
            slave.DataStore.InputRegisters.WritePoints( (ushort)address, writeData);
            return true;
        }

        /// <summary>//////////////////////////////////////////////////////////////////</summary>
        /// 	//写读写寄存器-保持寄存器
        //bool操作
        public static bool WriteBoolRW(this IModbusSlave slave, int address, bool[] values)
        {
            slave.DataStore.CoilDiscretes.WritePoints((ushort)address, values);
            return true;
        }

        public static bool WriteBoolRW(this IModbusSlave slave, int address, bool value)
        {
            return WriteBoolRW(slave, address, new bool[] { value });
        }

        //16位操作
        public static bool WriteUInt16RW(this IModbusSlave slave, int address, UInt16[] values)
        { 
            slave.DataStore.HoldingRegisters.WritePoints((ushort)address, values.ToTargetEndian(GetEndian(slave)));
            return true;
        }

        public static bool WriteUInt16RW(this IModbusSlave slave, int address, UInt16 value)
        {
            return WriteUInt16RW(slave, address, new UInt16[] { value });
        }

        public static bool WriteInt16RW(this IModbusSlave slave, int address, Int16[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(slave));
            slave.DataStore.HoldingRegisters.WritePoints((ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteInt16RW(this IModbusSlave slave, int address, Int16 value)
        {
            return WriteInt16RW(slave, address, new Int16[] { value });
        }

        //32位操作符
        public static bool WriteUInt32RW(this IModbusSlave slave, int address, UInt32[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(slave));
            slave.DataStore.HoldingRegisters.WritePoints((ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteUInt32RW(this IModbusSlave slave, int address, UInt32 value)
        {
            return WriteUInt32RW(slave, address, new UInt32[] { value });
        }

        public static bool WriteInt32RW(this IModbusSlave slave, int address, Int32[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(slave));
            slave.DataStore.HoldingRegisters.WritePoints((ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteInt32RW(this IModbusSlave slave, int address, Int32 value)
        {
            return WriteInt32RW(slave, address, new Int32[] { value });
        }

        //32位浮点数
        public static bool WriteFloatRW(this IModbusSlave slave, int address, float[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(slave));
            slave.DataStore.HoldingRegisters.WritePoints((ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteFloatRW(this IModbusSlave slave, int address, float value)
        {
            return WriteFloatRW(slave, address, new float[] { value });
        }

        public static bool WriteStringRW(this IModbusSlave slave, int stationNo, int address, string str)
        {
            var writeData = str.ToUInt16();
            for (int i = 0; i < writeData.Length; i++)
            {
                writeData[i] = writeData[i].Swap();//交换高低字
            }
            slave.DataStore.HoldingRegisters.WritePoints((ushort)address, writeData);
            return true;
        }
        //主站用


        /// <summary>//////////////////////////////////////////////////////////////////</summary>
        /// 	//写读写寄存器-保持寄存器
        //bool操作
        public static bool WriteBoolRW(this IModbusMaster mb, int stationNo, int address, bool[] values)
        {
            mb.WriteMultipleCoils((byte)stationNo, (ushort)address, values);
            return true;
        }

        public static bool WriteBoolRW(this IModbusMaster mb, int stationNo, int address, bool value)
        {
            return WriteBoolRW(mb, stationNo, address, new bool[] { value });
        }

        //16位操作
        public static bool WriteUInt16RW(this IModbusMaster mb, int stationNo, int address, UInt16[] values)
        {
            mb.WriteMultipleRegisters((byte)stationNo, (ushort)address, values.ToTargetEndian(GetEndian(mb)));
            return true;
        }

        public static bool WriteUInt16RW(this IModbusMaster mb, int stationNo, int address, UInt16 value)
        {
            mb.WriteSingleRegister((byte)stationNo, (ushort)address, value);
            return true;
        }

        public static bool WriteInt16RW(this IModbusMaster mb, int stationNo, int address, Int16[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(mb));
            mb.WriteMultipleRegisters((byte)stationNo, (ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteInt16RW(this IModbusMaster mb, int stationNo, int address, Int16 value)
        {
            mb.WriteSingleRegister((byte)stationNo, (ushort)address, (UInt16)value);
            return true;
        }

        //32位操作符
        public static bool WriteUInt32RW(this IModbusMaster mb, int stationNo, int address, UInt32[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(mb));
            mb.WriteMultipleRegisters((byte)stationNo, (ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteUInt32RW(this IModbusMaster mb, int stationNo, int address, UInt32 value)
        {
            return WriteUInt32RW(mb, stationNo, address, new UInt32[] { value });
        }

        public static bool WriteInt32RW(this IModbusMaster mb, int stationNo, int address, Int32[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(mb));
            mb.WriteMultipleRegisters((byte)stationNo, (ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteInt32RW(this IModbusMaster mb, int stationNo, int address, Int32 value)
        {
            return WriteInt32RW(mb, stationNo, address, new Int32[] { value });
        }

        //32位浮点数
        public static bool WriteFloatRW(this IModbusMaster mb, int stationNo, int address, float[] values)
        {
            var v1 = values.ToTargetEndian(GetEndian(mb));
            mb.WriteMultipleRegisters((byte)stationNo, (ushort)address, v1.ToUInt16());
            return true;
        }

        public static bool WriteFloatRW(this IModbusMaster mb, int stationNo, int address, float value)
        {
            WriteFloatRW(mb, stationNo, address, new float[] { value });
            return true;
        }

        public static bool WriteStringRW(this IModbusMaster mb, int stationNo, int address, string str)
        {
            //var ary1 = Encoding.UTF8.GetBytes(str);
            //if (ary1.Length % 2 != 0) ary1=ary1.Append((byte)0).ToArray();
            //var ary2 = new UInt16[ary1.Length/2] ;
            //Buffer.BlockCopy(ary1,0,ary2,0, ary1.Length);
            //for(int i=0;i<ary2.Length;i++)
            //{
            //    ary2[i] = ary2[i].Swap();
            //} 
            //mb.WriteMultipleRegisters((byte)stationNo, (ushort)address, ary2);
            var writeData = str.ToUInt16();
            for(int i=0;i<writeData.Length;i++)
            {
                writeData[i] = writeData[i].Swap();//交换高低字
            }
            mb.WriteMultipleRegisters((byte)stationNo, (ushort)address, writeData);
            return true;
        }

        //读取
        //bool
        public static bool[] ReadBoolR(this IModbusMaster mb, int stationNo, int address, int count)
        {
            return mb.ReadInputs((byte)stationNo, (ushort)address, (ushort)count);
        }

        public static bool ReadBoolR(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadBoolR(mb, stationNo, address, 1).FirstOrDefault();
        }

        //16位操作
        public static UInt16[] ReadUInt16R(this IModbusMaster mb, int stationNo, int address, int count)
        {
            return mb.ReadInputRegisters((byte)stationNo, (ushort)address, (ushort)count).ToTargetEndian(GetEndian(mb));
        }

        public static UInt16 ReadUInt16R(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadUInt16R(mb, stationNo, address, 1).FirstOrDefault();
        }

        public static Int16[] ReadInt16R(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters((byte)stationNo, (ushort)address, (ushort)count);
            return res.ToTargetEndian(GetEndian(mb)).ToInt16();
        }

        public static Int16 ReadInt16R(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadInt16R(mb, stationNo, address, 1).FirstOrDefault();
        }

        //32位操作
        public static UInt32[] ReadUInt32R(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters((byte)stationNo, (ushort)address, (ushort)(count * 2));
            var res2 = res.ToTargetEndian(GetEndian(mb));
            return res2.ToUInt32();
        }

        public static UInt32 ReadUInt32R(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadUInt32R(mb, stationNo, address, 1).FirstOrDefault();
        }

        public static Int32[] ReadInt32R(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters((byte)stationNo, (ushort)address, (ushort)(count * 2));
            var res2 = res.ToTargetEndian(GetEndian(mb));
            return res2.ToInt32();
        }

        public static Int32 ReadInt32R(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadInt32R(mb, stationNo, address, 1).FirstOrDefault();
        }

        //float
        public static float[] ReadFloatR(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters((byte)stationNo, (ushort)address, (ushort)(count * 2));
            var res2 = res.ToTargetEndian(GetEndian(mb));
            return res2.ToFloat();
        }

        public static float ReadFloatR(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadFloatR(mb, stationNo, address, 1).FirstOrDefault();
        }

        public static string ReadStringR(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters((byte)stationNo, (ushort)address, (ushort)(count < 2 ? 2 : count % 2 == 0 ? count / 2 : count / 2 + 1));
            for(int i=0;i<res.Length;i++)
            {
                res[i] = res[i].Swap();
            }
            return ToString(res);
        }
        public static string ReadStringR(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadStringR(mb, stationNo, address, 2);
        }

        //保持寄存器
        //读取
        //bool
        public static bool[] ReadBoolRW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            return mb.ReadCoils((byte)stationNo, (ushort)address, (ushort)count);
        }

        public static bool ReadBoolRW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadBoolRW(mb, stationNo, address, 1).FirstOrDefault();
        }

        //16位操作
        public static UInt16[] ReadUInt16RW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            return mb.ReadHoldingRegisters((byte)stationNo, (ushort)address, (ushort)count).ToTargetEndian(GetEndian(mb));
        }

        public static UInt16 ReadUInt16RW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadUInt16RW(mb, stationNo, address, 1).FirstOrDefault();
        }

        public static Int16[] ReadInt16RW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters((byte)stationNo, (ushort)address, (ushort)count);
            var ret = new Int16[count];
            var res2 = res.ToTargetEndian(GetEndian(mb));
            Buffer.BlockCopy(res2, 0, ret, 0, count * 2);
            return ret;
        }

        public static Int16 ReadInt16RW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadInt16RW(mb, stationNo, address, 1).FirstOrDefault();
        }

        //32位操作
        public static UInt32[] ReadUInt32RW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters((byte)stationNo, (ushort)address, (ushort)(count * 2));
            var res2 = res.ToTargetEndian(GetEndian(mb));
            return res2.ToUInt32();
        }

        public static UInt32 ReadUInt32RW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadUInt32RW(mb, stationNo, address, 1).FirstOrDefault();
        }

        public static Int32[] ReadInt32RW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters((byte)stationNo, (ushort)address, (ushort)(count * 2));
            var res2 = res.ToTargetEndian(GetEndian(mb));
            return res2.ToInt32();
        }

        public static Int32 ReadInt32RW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadInt32RW(mb, stationNo, address, 1).FirstOrDefault();
        }

        //float
        public static float[] ReadFloatRW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters((byte)stationNo, (ushort)address, (ushort)(count * 2));
            var res2 = res.ToTargetEndian(GetEndian(mb));
            return res2.ToFloat();
        }

        public static float ReadFloatRW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadFloatRW(mb, stationNo, address, 1).FirstOrDefault();
        }

        public static string ReadStringRW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters((byte)stationNo, (ushort)address, (ushort)(count < 2 ? 2 : count % 2 == 0 ? count / 2 : count / 2 + 1));
            for(int i=0;i<res.Length;i++)
            {
                res[i] = res[i].Swap();//交换高低字节
            }
            return ToString(res);
        }
        public static string ReadStringRW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadStringRW(mb, stationNo, address, 2);
        }
    }

}
