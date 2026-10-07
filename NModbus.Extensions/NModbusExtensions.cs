using System;
using System.Buffers;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NModbus.IO;
using NModbus.Serial;

namespace NModbus.Extensions
{

    /// <summary>
    /// 字节序（字序）模式。把 32 位值的 4 个字节按有效性从高到低记为 A、B、C、D，
    /// 枚举名即该值转换后在线上寄存器序列中的字节发送顺序；16 位类型只涉及寄存器内高低字节是否交换。
    /// </summary>
    public enum EnumEndian
    {
        /// <summary>小端序：线上字节 D C B A，与 C# 内存原生字节序一致（对 16 位交换高低字节）。</summary>
        DCBA,
        /// <summary>高低字交换：线上字节 C D A B。默认模式，与 HslCommunication 的 CDAB 布局兼容（对 16 位不交换）。</summary>
        CDAB,
        /// <summary>字内字节交换：线上字节 B A D C，字的先后顺序不变、每个寄存器内高低字节颠倒（对 16 位交换高低字节）。</summary>
        BADC,
        /// <summary>大端序：线上字节 A B C D，Modbus 网络标准序，字序与字节序均高位在前（对 16 位不交换）。</summary>
        ABCD
    }

    /// <summary>
    /// NModbus 扩展方法集合：字节序设置与转换、寄存器数据类型互转、主站/从站多类型读写。
    /// 全部为静态扩展方法，不改变 NModbus 原有行为。
    /// </summary>
    public static class NModbusExtensions
    {
        // MemoryPool<T> is abstract - use the shared instance instead of new()
        static MemoryPool<UInt16> p16 = MemoryPool<UInt16>.Shared;
        static MemoryPool<byte> pByte= MemoryPool<byte>.Shared;

        //弱引用表：连接对象被回收后条目自动消失，避免字典无限增长（修复4）；GetValue/StrongBox 写入均为原子操作（修复3）
        static readonly ConditionalWeakTable<object, StrongBox<EnumEndian>> dicEndian = new();

        static void SetEndianCore(object key, EnumEndian endian)
        {
            //GetValue 原子：不存在则创建；已存在则直接改盒子内容，不会覆盖成默认值（修复3）
            var box = dicEndian.GetValue(key, _ => new StrongBox<EnumEndian>(endian));
            box.Value = endian;
        }

        static EnumEndian GetEndianCore(object key)
            => dicEndian.GetValue(key, static _ => new StrongBox<EnumEndian>(EnumEndian.CDAB)).Value;

        //参数校验辅助：地址/站号/数量越界立即抛异常，禁止 int->ushort/byte 的静默截断。
        //截断会把数据写到错误的寄存器或请求错误的点数，属于产线事故级行为（见 0.2.0 修复说明）。
        static ushort CheckedAddress(int address)
            => (uint)address > ushort.MaxValue
                ? throw new ArgumentOutOfRangeException(nameof(address), address, $"address 超出 0-{ushort.MaxValue} 范围（当前 {address}），拒绝截断写入。")
                : unchecked((ushort)(uint)address);

        static ushort CheckedCount(int count)
            => (uint)count == 0 || (uint)count > ushort.MaxValue
                ? throw new ArgumentOutOfRangeException(nameof(count), count, $"单次数量必须在 1-{ushort.MaxValue} 范围（当前 {count}）。")
                : unchecked((ushort)(uint)count);

        static byte CheckedStation(int stationNo)
            => (uint)stationNo > byte.MaxValue
                ? throw new ArgumentOutOfRangeException(nameof(stationNo), stationNo, $"stationNo 超出 0-{byte.MaxValue} 范围（当前 {stationNo}）。")
                : unchecked((byte)(uint)stationNo);

        //32 位类型（Int32/UInt32/float）每个值占 2 个寄存器：先校验个数再乘 2，杜绝 count*2 溢出后静默截断。
        static ushort WordRegCount(int values)
            => (uint)values > ushort.MaxValue / 2
                ? throw new ArgumentOutOfRangeException(nameof(values), values, $"32 位值个数 ×2 不能超过 {ushort.MaxValue}，上限为 {ushort.MaxValue / 2}（当前 {values}）。")
                : (ushort)(values * 2);

        //数组参数空值保护：null 或空数组统一抛 ArgumentException（修复从站侧此前对空数组抛 NullReferenceException、
        //与主站侧 ArgumentException 不一致的问题；ToUInt16() 对空数组返回 null 后原先靠 ! 压制，现集中拦截）。
        static T[] NonEmpty<T>(T[]? values, string paramName) where T : struct
            => values is null || values.Length == 0
                ? throw new ArgumentException("数组参数不能为 null 或空数组。", paramName)
                : values;

        /// <summary>
        /// 设置该 <see cref="TcpClient"/> 的字节序。
        /// 注意：字节序按对象实例各自存储，不会自动传导给由它创建的 <see cref="IModbusMaster"/>，
        /// 读写前应在使用对象上单独设置。
        /// </summary>
        /// <param name="client">要关联字节序的 TCP 连接对象。</param>
        /// <param name="endian">字节序模式，未设置时默认 <see cref="EnumEndian.CDAB"/>。</param>
        public static void SetEndian(this TcpClient client, EnumEndian endian)
        {
            SetEndianCore(client, endian);
        }

        /// <summary>
        /// 设置该 <see cref="SerialPort"/> 的字节序。
        /// 注意：字节序按对象实例各自存储，不会自动传导给 RTU 主站/从站对象，读写前应在使用对象上单独设置。
        /// </summary>
        /// <param name="sp">要关联字节序的串口对象。</param>
        /// <param name="endian">字节序模式，未设置时默认 <see cref="EnumEndian.CDAB"/>。</param>
        public static void SetEndian(this SerialPort sp, EnumEndian endian)
        {
            SetEndianCore(sp, endian);
        }

        /// <summary>
        /// 设置该主站的字节序，之后主站所有读写扩展按此模式自动转换寄存器数据。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="endian">字节序模式，未设置时默认 <see cref="EnumEndian.CDAB"/>（兼容 HslCommunication）。</param>
        public static void SetEndian(this IModbusMaster mb, EnumEndian endian)
        {
            SetEndianCore(mb, endian);
        }

        /// <summary>
        /// 设置该从站的字节序，之后从站侧写入（Write*R / Write*RW）按此模式转换后放入数据区。
        /// </summary>
        /// <param name="mb">Modbus 从站对象。</param>
        /// <param name="endian">字节序模式，未设置时默认 <see cref="EnumEndian.CDAB"/>。</param>
        public static void SetEndian(this IModbusSlave mb, EnumEndian endian)
        {
            SetEndianCore(mb, endian);
        }

        /// <summary>
        /// 获取该 <see cref="TcpClient"/> 已设置的字节序；未设置过返回默认值 <see cref="EnumEndian.CDAB"/>。
        /// </summary>
        /// <param name="client">TCP 连接对象。</param>
        /// <returns>当前字节序模式。</returns>
        public static EnumEndian GetEndian(this TcpClient client)
        {
            return GetEndianCore(client);
        }


        /// <summary>
        /// 获取该 <see cref="SerialPort"/> 已设置的字节序；未设置过返回默认值 <see cref="EnumEndian.CDAB"/>。
        /// </summary>
        /// <param name="sp">串口对象。</param>
        /// <returns>当前字节序模式。</returns>
        public static EnumEndian GetEndian(this SerialPort sp)
        {
            return GetEndianCore(sp);
        }

        /// <summary>
        /// 获取该主站已设置的字节序；未设置过返回默认值 <see cref="EnumEndian.CDAB"/>。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <returns>当前字节序模式。</returns>
        public static EnumEndian GetEndian(this IModbusMaster mb)
        {
            return GetEndianCore(mb);
        }

        /// <summary>
        /// 获取该从站已设置的字节序；未设置过返回默认值 <see cref="EnumEndian.CDAB"/>。
        /// </summary>
        /// <param name="mb">Modbus 从站对象。</param>
        /// <returns>当前字节序模式。</returns>
        public static EnumEndian GetEndian(this IModbusSlave mb)
        {
            return GetEndianCore(mb);
        }
         
        /// <summary>
        /// 交换 16 位值的高低字节（0x1234 → 0x3412）。
        /// </summary>
        /// <param name="value">原值。</param>
        /// <returns>高低字节交换后的值。</returns>
        public static UInt16 Swap(this UInt16 value)
        {
            return (UInt16)(value >> 8 | value << 8);
        }

        /// <summary>
        /// 交换 16 位有符号值的高低字节（按位交换，符号随位模式变化）。
        /// </summary>
        /// <param name="value">原值。</param>
        /// <returns>高低字节交换后的值。</returns>
        public static Int16 Swap(this Int16 value)
        {
            var u = (UInt16)value;
            return (Int16)(u >> 8 | u << 8);
        }

        //工具方法
        //16位方法组
        /// <summary>
        /// 将 <see cref="Int16"/> 数组按位转换为 <see cref="UInt16"/> 数组（元素个数不变，仅符号形态变化）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>转换后的数组；<paramref name="values"/> 为 null 或空时返回 null。</returns>
        public static UInt16[]? ToUInt16(this Int16[]? values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt16[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 2);
            return ret;
        }

        /// <summary>
        /// 将 <see cref="Int32"/> 数组拆为寄存器数组：每个 32 位值占两个 <see cref="UInt16"/>（按 C# 内存小端顺序拆字）。
        /// 如需目标字节序，请在得到结果后自行调用 <see cref="ToTargetEndian(int[], EnumEndian)"/>。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>长度为源数组两倍的寄存器数组；<paramref name="values"/> 为 null 或空时返回 null。</returns>
        public static UInt16[]? ToUInt16(this Int32[]? values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt16[values.Length * 2];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 2);
            return ret;
        }

        /// <summary>
        /// 将 <see cref="UInt32"/> 数组拆为寄存器数组：每个 32 位值占两个 <see cref="UInt16"/>（按 C# 内存小端顺序拆字）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>长度为源数组两倍的寄存器数组；<paramref name="values"/> 为 null 或空时返回 null。</returns>
        public static UInt16[]? ToUInt16(this UInt32[]? values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt16[values.Length * 2];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 2);
            return ret;
        }

        /// <summary>
        /// 将 <see cref="float"/> 数组拆为寄存器数组：每个 float 占两个 <see cref="UInt16"/>（按 IEEE754 小端内存布局拆字）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>长度为源数组两倍的寄存器数组；<paramref name="values"/> 为 null 或空时返回 null。</returns>
        public static UInt16[]? ToUInt16(this float[]? values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt16[values.Length * 2];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 2);
            return ret;
        }

        /// <summary>
        /// 将字符串按 UTF8 编码为寄存器数组：奇数字节长度自动补 0x00；
        /// 此处一律不做字节倒序，如需交换寄存器高低字节，拿到数组后自行调用 <see cref="Swap(ushort)"/>。
        /// </summary>
        /// <param name="str">源字符串。</param>
        /// <returns>寄存器数组；<paramref name="str"/> 为 null 或空串时返回 null。</returns>
        public static UInt16[]? ToUInt16(this string? str)
        {
            if (string.IsNullOrEmpty(str)) return null;
            var ary1 = Encoding.UTF8.GetBytes(str);
            if (ary1.Length % 2 != 0) ary1 = ary1.Append((byte)0x00).ToArray();
            var ret = new UInt16[ary1.Length / 2];
            Buffer.BlockCopy(ary1, 0, ret, 0, ary1.Length);
            return ret;
        }

        /// <summary>
        /// 将 <see cref="UInt16"/> 数组按位转换为 <see cref="Int16"/> 数组（元素个数不变，仅符号形态变化）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>转换后的数组。</returns>
        public static Int16[] ToInt16(this UInt16[] values)
        {
            var ret = new Int16[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);
            return ret;
        }

        /// <summary>
        /// 将寄存器数组按每两个 <see cref="UInt16"/> 合并为一个 <see cref="Int32"/>（小端字序拼装）。
        /// 元素个数为奇数时末尾单个寄存器被忽略，请保证长度为偶数。
        /// </summary>
        /// <param name="values">寄存器数组。</param>
        /// <returns>长度为源数组一半的 32 位数组。</returns>
        public static Int32[] ToInt32(this UInt16[] values)
        {
            var ret = new Int32[values.Length / 2];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);
            return ret;
        }

        /// <summary>
        /// 将寄存器数组按每两个 <see cref="UInt16"/> 合并为一个 <see cref="UInt32"/>（小端字序拼装）。
        /// 元素个数为奇数时末尾单个寄存器被忽略，请保证长度为偶数。
        /// </summary>
        /// <param name="values">寄存器数组。</param>
        /// <returns>长度为源数组一半的 32 位数组。</returns>
        public static UInt32[] ToUInt32(this UInt16[] values)
        {
            var ret = new UInt32[values.Length / 2];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);
            return ret;
        }

        /// <summary>
        /// 将寄存器数组按每两个 <see cref="UInt16"/> 合并为一个 <see cref="float"/>（IEEE754 小端字序拼装）。
        /// 元素个数为奇数时末尾单个寄存器被忽略，请保证长度为偶数。
        /// </summary>
        /// <param name="values">寄存器数组。</param>
        /// <returns>长度为源数组一半的 float 数组。</returns>
        public static float[] ToFloat(this UInt16[] values)
        {
            var ret = new float[values.Length / 2];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 2);
            return ret;
        }

        /// <summary>
        /// 将寄存器数组按 UTF8 解码为字符串并过滤末尾 0x00。
        /// 必须以静态方式调用 <c>NModbusExtensions.ToString(regs)</c>——数组实例自带的 <c>object.ToString()</c>
        /// 优先级更高，写成 <c>regs.ToString()</c> 会得到 "System.UInt16[]"。
        /// </summary>
        /// <param name="values">寄存器数组。</param>
        /// <returns>解码后的字符串。</returns>
        public static string ToString(this UInt16[] values)
        {
            var ary = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, ary, 0, values.Length * 2);            
            return Encoding.UTF8.GetString(ary).Trim('\0');//过滤末尾的\0
        }

        //32位方法组-字符串一律使用16位
        /// <summary>
        /// 将 <see cref="Int32"/> 数组按位转换为 <see cref="UInt32"/> 数组（元素个数不变，仅符号形态变化）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>转换后的数组；<paramref name="values"/> 为 null 或空时返回 null。</returns>
        public static UInt32[]? ToUInt32(this Int32[]? values)
        {
            if (values is null || values.Length == 0) return null;
            var ret = new UInt32[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 4);
            return ret;
        }

        /// <summary>
        /// 将 <see cref="float"/> 数组按 IEEE754 位模式转换为 <see cref="UInt32"/> 数组（每个 float 对应一个 32 位值）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>转换后的数组；<paramref name="values"/> 为 null 或空时返回 null。</returns>
        public static UInt32[]? ToUInt32(this float[]? values)
        {
            if (values is null || values.Length == 0) return null;
            //每个 float 转一个 UInt32（4字节对4字节），输出长度与输入一致
            var ret = new UInt32[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, ret.Length * 4);
            return ret;
        }

        /// <summary>
        /// 将 <see cref="UInt32"/> 数组按位转换为 <see cref="Int32"/> 数组（元素个数不变，仅符号形态变化）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>转换后的数组。</returns>
        public static Int32[] ToInt32(this UInt32[] values)
        {
            var ret = new Int32[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 4);
            return ret;
        }

        /// <summary>
        /// 将 <see cref="UInt32"/> 数组按 IEEE754 位模式还原为 <see cref="float"/> 数组。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <returns>转换后的数组。</returns>
        public static float[] ToFloat(this UInt32[] values)
        {
            var ret = new float[values.Length];
            Buffer.BlockCopy(values, 0, ret, 0, values.Length * 4);
            return ret;
        } 

        //字符串通道统一实现。
        //说明：字符串在线上的字节顺序固定为 UTF8 文本顺序（每寄存器内交换高低字节，与主流 PLC/HslCommunication 的
        //字符串寄存器布局一致），不受 SetEndian 影响——若让字符串跟随 SetEndian，会改变已发布版本在默认 CDAB 下的
        //线上数据布局，造成新老数据互读乱码，因此这里保持原线上语义，仅统一读写路径并补充空值保护。
        static UInt16[] EncodeString(string? str)
        {
            var regs = str.ToUInt16();
            if (regs is null) return Array.Empty<UInt16>();
            for (int i = 0; i < regs.Length; i++) regs[i] = regs[i].Swap();
            return regs;
        }

        static string DecodeString(UInt16[]? regs)
        {
            if (regs is null || regs.Length == 0) return string.Empty;
            var data = (UInt16[])regs.Clone();
            for (int i = 0; i < data.Length; i++) data[i] = data[i].Swap();
            return ToString(data);//静态调用扩展方法，避免绑定到 object.ToString()
        }

        //count 为 UTF8 字节数（与写入侧口径一致），换算为需要读取的寄存器数；先做范围校验，杜绝负数与溢出静默变成错误点数
        static ushort StringRegCount(int count)
        {
            ushort bytes = CheckedCount(count);
            return bytes < 2 ? (ushort)2 : (ushort)((bytes + 1) / 2);
        }

        /// <summary>
        /// 将 16 位无符号寄存器数组转换为目标字节序。16 位只有一个寄存器，只涉及寄存器内高低字节是否交换：
        /// <see cref="EnumEndian.BADC"/> / <see cref="EnumEndian.DCBA"/> 交换高低字节，
        /// <see cref="EnumEndian.ABCD"/> / <see cref="EnumEndian.CDAB"/> 原样返回。
        /// </summary>
        /// <param name="values">寄存器数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的数组；不交换时返回原数组引用。</returns>
        public static UInt16[] ToTargetEndian(this UInt16[] values, EnumEndian targetEndian)
        {
            //16位的转换就2种情况,要么交换高低字,要么不交换
            switch (targetEndian)
            {
                case EnumEndian.BADC:
                case EnumEndian.DCBA: 
                    var ret = new UInt16[values.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        ret[i] = values[i].Swap();
                    }
                    return ret;
                default:
                case EnumEndian.ABCD:
                case EnumEndian.CDAB:
                    return values;
            }
        }

        /// <summary>
        /// 将 16 位有符号寄存器数组转换为目标字节序，交换规则与 <see cref="ToTargetEndian(ushort[], EnumEndian)"/> 一致。
        /// </summary>
        /// <param name="values">寄存器数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的数组；不交换时返回原数组引用。</returns>
        public static Int16[] ToTargetEndian(this Int16[] values, EnumEndian targetEndian)
        {
            //16位的转换就2种情况,要么交换高低字,要么不交换
            switch (targetEndian)
            {
                case EnumEndian.BADC:
                case EnumEndian.DCBA:
                    var ret = new Int16[values.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        ret[i] = values[i].Swap();
                    }
                    return ret;
                default:
                case EnumEndian.ABCD:
                case EnumEndian.CDAB:
                    return values;
            }
        }
        /// <summary>
        /// 将单个 16 位无符号值转换为目标字节序，交换规则与数组版完全一致（单值写重载与数组写重载共用本判定）。
        /// </summary>
        /// <param name="value">寄存器值。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的值。</returns>
        public static UInt16 ToTargetEndian(this UInt16 value, EnumEndian targetEndian)
        {
            //16位的转换就2种情况,要么交换高低字,要么不交换
            switch (targetEndian)
            {
                case EnumEndian.BADC:
                case EnumEndian.DCBA:
                    return value.Swap();
                default:
                case EnumEndian.ABCD:
                case EnumEndian.CDAB:
                    return value;
            }
        }

        /// <summary>
        /// 将单个 16 位有符号值转换为目标字节序，交换规则与数组版完全一致。
        /// </summary>
        /// <param name="value">寄存器值。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的值。</returns>
        public static Int16 ToTargetEndian(this Int16 value, EnumEndian targetEndian)
        {
            //16位的转换就2种情况,要么交换高低字,要么不交换
            switch (targetEndian)
            {
                case EnumEndian.BADC:
                case EnumEndian.DCBA:
                    return value.Swap();
                default:
                case EnumEndian.ABCD:
                case EnumEndian.CDAB:
                    return value;
            }
        }

        /// <summary>
        /// 通用字节序转换方法（适用于 <see cref="Int32"/>、<see cref="UInt32"/>、<see cref="float"/> 等 4 字节类型）。
        /// 对每个 4 字节值，把内存中的字节记为 a b c d（d 为最高有效字节），按目标模式重排：
        /// <see cref="EnumEndian.ABCD"/> → c d a b、<see cref="EnumEndian.BADC"/> → d c b a（整值反转）、
        /// <see cref="EnumEndian.CDAB"/> → 不变（默认模式，即设备线上布局）、<see cref="EnumEndian.DCBA"/> → b a d c。
        /// 转换结果再写入/还原寄存器即可与对应字节序的设备互读。
        /// </summary>
        /// <typeparam name="T">必须为 4 字节结构类型，否则抛出 <see cref="NotSupportedException"/>。</typeparam>
        /// <param name="values">源数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的新数组；<paramref name="values"/> 为 null 或空时原样返回。</returns>
        /// <exception cref="NotSupportedException">T 的大小不是 4 字节。</exception>
        public static T[]? ConvertEndian<T>(this T[]? values, EnumEndian targetEndian) where T : struct
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
                    case EnumEndian.ABCD: // 内存 a b c d -> c d a b：高低字交换
                        dstSpan[offset] = srcSpan[offset + 2];
                        dstSpan[offset + 1] = srcSpan[offset + 3];
                        dstSpan[offset + 2] = srcSpan[offset];
                        dstSpan[offset + 3] = srcSpan[offset + 1];
                        break;
                    case EnumEndian.BADC: // 内存 a b c d -> d c b a：整体反转
                        dstSpan[offset] = srcSpan[offset + 3];
                        dstSpan[offset + 1] = srcSpan[offset + 2];
                        dstSpan[offset + 2] = srcSpan[offset + 1];
                        dstSpan[offset + 3] = srcSpan[offset];
                        break;
                    case EnumEndian.CDAB: // 默认模式：不转换，直接复制
                        srcSpan.Slice(offset, 4).CopyTo(dstSpan.Slice(offset, 4));
                        break;
                    default:
                    case EnumEndian.DCBA: // 内存 a b c d -> b a d c：寄存器内高低字节交换
                        dstSpan[offset] = srcSpan[offset + 1];
                        dstSpan[offset + 1] = srcSpan[offset];
                        dstSpan[offset + 2] = srcSpan[offset + 3];
                        dstSpan[offset + 3] = srcSpan[offset + 2]; 
                        break;
                }
            }
            return result;
        }

        /// <summary>
        /// 将 <see cref="Int32"/> 数组转换为目标字节序（内部调用 <see cref="ConvertEndian{T}"/>）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的数组。</returns>
        public static Int32[] ToTargetEndian(this Int32[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian)!;

        /// <summary>
        /// 将 <see cref="UInt32"/> 数组转换为目标字节序（内部调用 <see cref="ConvertEndian{T}"/>）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的数组。</returns>
        public static UInt32[] ToTargetEndian(this UInt32[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian)!;

        /// <summary>
        /// 将 <see cref="float"/> 数组转换为目标字节序（内部调用 <see cref="ConvertEndian{T}"/>）。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的数组。</returns>
        public static float[] ToTargetEndian(this float[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian)!;

        // FormTargetEndian 与 ToTargetEndian 逻辑相同，可直接复用
        /// <summary>
        /// <see cref="ToTargetEndian(int[], EnumEndian)"/> 的别名，行为完全相同，为兼容旧调用保留。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的数组。</returns>
        public static Int32[] FormTargetEndian(this Int32[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian)!;

        /// <summary>
        /// <see cref="ToTargetEndian(uint[], EnumEndian)"/> 的别名，行为完全相同，为兼容旧调用保留。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的数组。</returns>
        public static UInt32[] FormTargetEndian(this UInt32[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian)!;

        /// <summary>
        /// <see cref="ToTargetEndian(float[], EnumEndian)"/> 的别名，行为完全相同，为兼容旧调用保留。
        /// </summary>
        /// <param name="values">源数组。</param>
        /// <param name="targetEndian">目标字节序模式。</param>
        /// <returns>转换后的数组。</returns>
        public static float[] FormTargetEndian(this float[] values, EnumEndian targetEndian) => ConvertEndian(values, targetEndian)!;

        //从站用
        //写只读寄存器-输入寄存器
        //bool操作
        /// <summary>
        /// 从站侧：向线圈输入（离散输入，主站只读区）批量写入布尔值。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">要写入的布尔数组。</param>
        /// <returns>恒为 true；地址越界或数据区未初始化时抛出异常。</returns>
        public static bool WriteBoolR(this IModbusSlave slave, int address, bool[] values)
        {
            slave.DataStore.CoilInputs.WritePoints(CheckedAddress(address), NonEmpty(values, nameof(values)));
            return true;
        }

        /// <summary>
        /// 从站侧：向线圈输入（离散输入，主站只读区）写入单个布尔值。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">目标地址（0-65535）。</param>
        /// <param name="value">要写入的值。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteBoolR(this IModbusSlave slave, int address, bool value)
        {
            return WriteBoolR(slave, address, new bool[] { value });
        }

        //16位操作
        /// <summary>
        /// 从站侧：按从站字节序（<see cref="GetEndian(IModbusSlave)"/>）转换后，批量写入输入寄存器（主站只读区）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt16R(this IModbusSlave slave, int address, UInt16[] values)
        {
            slave.DataStore.InputRegisters.WritePoints(CheckedAddress(address), NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave)));
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个输入寄存器值（主站只读区）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">目标地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt16R(this IModbusSlave slave, int address, UInt16 value)
        {
            return WriteUInt16R(slave, address, new UInt16[] { value });
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入输入寄存器（主站只读区），值按有符号 16 位处理。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt16R(this IModbusSlave slave, int address, Int16[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave));
            slave.DataStore.InputRegisters.WritePoints(CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个输入寄存器值（主站只读区），值按有符号 16 位处理。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">目标地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt16R(this IModbusSlave slave, int address, Int16 value)
        {
            return WriteInt16R(slave, address, new Int16[] { value });
        }

        //32位操作符
        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入输入寄存器（主站只读区），每个 <see cref="UInt32"/> 占用两个连续寄存器。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt32R(this IModbusSlave slave, int address, UInt32[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave));
            slave.DataStore.InputRegisters.WritePoints(CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个 32 位无符号值到输入寄存器（占用两个连续寄存器）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt32R(this IModbusSlave slave, int address, UInt32 value)
        {
            return WriteUInt32R(slave, address, new UInt32[] { value });
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入输入寄存器（主站只读区），每个 <see cref="Int32"/> 占用两个连续寄存器。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt32R(this IModbusSlave slave, int address, Int32[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave));
            slave.DataStore.InputRegisters.WritePoints(CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个 32 位有符号值到输入寄存器（占用两个连续寄存器）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt32R(this IModbusSlave slave, int address, Int32 value)
        {
            return WriteInt32R(slave, address, new Int32[] { value });
        }

        //32位浮点数
        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入输入寄存器（主站只读区），每个 <see cref="float"/> 占用两个连续寄存器。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteFloatR(this IModbusSlave slave, int address, float[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave));
            slave.DataStore.InputRegisters.WritePoints(CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个 <see cref="float"/> 到输入寄存器（占用两个连续寄存器）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteFloatR(this IModbusSlave slave, int address, float value)
        {
            return WriteFloatR(slave, address, new float[] { value });
        }

        /// <summary>
        /// 从站侧：把字符串按 UTF8 编码写入输入寄存器（主站只读区）。
        /// 字符串线上布局固定为每寄存器内交换高低字节（与主流 PLC/HslCommunication 一致），不受从站字节序设置影响。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="stationNo">保留参数，从站自身无需站号，可任意传值（为兼容旧签名保留）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="str">要写入的字符串。</param>
        /// <returns>写入成功返回 true；<paramref name="str"/> 为 null 或空串时不写入并返回 false。</returns>
        public static bool WriteStringR(this IModbusSlave slave, int stationNo, int address, string str)
        {
            var writeData = EncodeString(str);
            if (writeData.Length == 0) return false;//空串不发送，避免零长度请求
            slave.DataStore.InputRegisters.WritePoints(CheckedAddress(address), writeData);
            return true;
        }

        //从站用
        //写读写寄存器-保持寄存器
        //bool操作
        /// <summary>
        /// 从站侧：向线圈（保持线圈，主站可读写区）批量写入布尔值。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">要写入的布尔数组。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteBoolRW(this IModbusSlave slave, int address, bool[] values)
        {
            slave.DataStore.CoilDiscretes.WritePoints(CheckedAddress(address), NonEmpty(values, nameof(values)));
            return true;
        }

        /// <summary>
        /// 从站侧：向线圈（保持线圈，主站可读写区）写入单个布尔值。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">目标地址（0-65535）。</param>
        /// <param name="value">要写入的值。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteBoolRW(this IModbusSlave slave, int address, bool value)
        {
            return WriteBoolRW(slave, address, new bool[] { value });
        }

        //16位操作
        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入保持寄存器（主站可读写区）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt16RW(this IModbusSlave slave, int address, UInt16[] values)
        { 
            slave.DataStore.HoldingRegisters.WritePoints(CheckedAddress(address), NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave)));
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个保持寄存器值（主站可读写区）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">目标地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt16RW(this IModbusSlave slave, int address, UInt16 value)
        {
            return WriteUInt16RW(slave, address, new UInt16[] { value });
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入保持寄存器（主站可读写区），值按有符号 16 位处理。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt16RW(this IModbusSlave slave, int address, Int16[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave));
            slave.DataStore.HoldingRegisters.WritePoints(CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个有符号 16 位值到保持寄存器（主站可读写区）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">目标地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt16RW(this IModbusSlave slave, int address, Int16 value)
        {
            return WriteInt16RW(slave, address, new Int16[] { value });
        }

        //32位操作符
        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入保持寄存器（主站可读写区），每个 <see cref="UInt32"/> 占用两个连续寄存器。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt32RW(this IModbusSlave slave, int address, UInt32[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave));
            slave.DataStore.HoldingRegisters.WritePoints(CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个 32 位无符号值到保持寄存器（占用两个连续寄存器）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt32RW(this IModbusSlave slave, int address, UInt32 value)
        {
            return WriteUInt32RW(slave, address, new UInt32[] { value });
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入保持寄存器（主站可读写区），每个 <see cref="Int32"/> 占用两个连续寄存器。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt32RW(this IModbusSlave slave, int address, Int32[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave));
            slave.DataStore.HoldingRegisters.WritePoints(CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个 32 位有符号值到保持寄存器（占用两个连续寄存器）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt32RW(this IModbusSlave slave, int address, Int32 value)
        {
            return WriteInt32RW(slave, address, new Int32[] { value });
        }

        //32位浮点数
        /// <summary>
        /// 从站侧：按从站字节序转换后，批量写入保持寄存器（主站可读写区），每个 <see cref="float"/> 占用两个连续寄存器。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteFloatRW(this IModbusSlave slave, int address, float[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(slave));
            slave.DataStore.HoldingRegisters.WritePoints(CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 从站侧：按从站字节序转换后，写入单个 <see cref="float"/> 到保持寄存器（占用两个连续寄存器）。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteFloatRW(this IModbusSlave slave, int address, float value)
        {
            return WriteFloatRW(slave, address, new float[] { value });
        }

        /// <summary>
        /// 从站侧：把字符串按 UTF8 编码写入保持寄存器（主站可读写区）。
        /// 字符串线上布局固定为每寄存器内交换高低字节，不受从站字节序设置影响。
        /// </summary>
        /// <param name="slave">从站对象。</param>
        /// <param name="stationNo">保留参数，从站自身无需站号，可任意传值（为兼容旧签名保留）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="str">要写入的字符串。</param>
        /// <returns>写入成功返回 true；<paramref name="str"/> 为 null 或空串时不写入并返回 false。</returns>
        public static bool WriteStringRW(this IModbusSlave slave, int stationNo, int address, string str)
        {
            var writeData = EncodeString(str);
            if (writeData.Length == 0) return false;//空串不发送，避免零长度请求
            slave.DataStore.HoldingRegisters.WritePoints(CheckedAddress(address), writeData);
            return true;
        }
        //主站用
        //写读写寄存器-保持寄存器 / 线圈
        //bool操作
        /// <summary>
        /// 主站侧：批量写目标从站的线圈（功能码 15，可读写区）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">要写入的布尔数组。</param>
        /// <returns>恒为 true（写入失败时底层抛出异常）。</returns>
        public static bool WriteBoolRW(this IModbusMaster mb, int stationNo, int address, bool[] values)
        {
            mb.WriteMultipleCoils(CheckedStation(stationNo), CheckedAddress(address), NonEmpty(values, nameof(values)));
            return true;
        }

        /// <summary>
        /// 主站侧：写目标从站的单个线圈。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">线圈地址（0-65535）。</param>
        /// <param name="value">要写入的值。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteBoolRW(this IModbusMaster mb, int stationNo, int address, bool value)
        {
            return WriteBoolRW(mb, stationNo, address, new bool[] { value });
        }

        //16位操作
        /// <summary>
        /// 主站侧：按主站字节序（<see cref="GetEndian(IModbusMaster)"/>）转换后，批量写目标从站的保持寄存器（功能码 16）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true（写入失败时底层抛出异常）。</returns>
        public static bool WriteUInt16RW(this IModbusMaster mb, int stationNo, int address, UInt16[] values)
        {
            mb.WriteMultipleRegisters(CheckedStation(stationNo), CheckedAddress(address), NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(mb)));
            return true;
        }

        /// <summary>
        /// 主站侧：按主站字节序转换后，写目标从站的单个保持寄存器（功能码 6）。与数组重载遵循同一字节序规则。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">寄存器地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt16RW(this IModbusMaster mb, int stationNo, int address, UInt16 value)
        { 
            mb.WriteSingleRegister(CheckedStation(stationNo), CheckedAddress(address), value.ToTargetEndian(GetEndian(mb)));
            return true;
        }

        /// <summary>
        /// 主站侧：按主站字节序转换后，批量写目标从站保持寄存器，值按有符号 16 位处理。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt16RW(this IModbusMaster mb, int stationNo, int address, Int16[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(mb));
            mb.WriteMultipleRegisters(CheckedStation(stationNo), CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 主站侧：按主站字节序转换后，写目标从站的单个有符号 16 位保持寄存器。与数组重载遵循同一字节序规则。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">寄存器地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt16RW(this IModbusMaster mb, int stationNo, int address, Int16 value)
        { 
            mb.WriteSingleRegister(CheckedStation(stationNo), CheckedAddress(address),(UInt16) value.ToTargetEndian(GetEndian(mb)));
            return true;
        }

        //32位操作符
        /// <summary>
        /// 主站侧：按主站字节序转换后，批量写目标从站保持寄存器，每个 <see cref="UInt32"/> 自动占用两个连续寄存器。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt32RW(this IModbusMaster mb, int stationNo, int address, UInt32[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(mb));
            mb.WriteMultipleRegisters(CheckedStation(stationNo), CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 主站侧：按主站字节序转换后，写目标从站单个 32 位无符号值（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteUInt32RW(this IModbusMaster mb, int stationNo, int address, UInt32 value)
        {
            return WriteUInt32RW(mb, stationNo, address, new UInt32[] { value });
        }

        /// <summary>
        /// 主站侧：按主站字节序转换后，批量写目标从站保持寄存器，每个 <see cref="Int32"/> 自动占用两个连续寄存器。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt32RW(this IModbusMaster mb, int stationNo, int address, Int32[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(mb));
            mb.WriteMultipleRegisters(CheckedStation(stationNo), CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 主站侧：按主站字节序转换后，写目标从站单个 32 位有符号值（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteInt32RW(this IModbusMaster mb, int stationNo, int address, Int32 value)
        {
            return WriteInt32RW(mb, stationNo, address, new Int32[] { value });
        }

        //32位浮点数
        /// <summary>
        /// 主站侧：按主站字节序转换后，批量写目标从站保持寄存器，每个 <see cref="float"/> 自动占用两个连续寄存器。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="values">原始业务值数组（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteFloatRW(this IModbusMaster mb, int stationNo, int address, float[] values)
        {
            var v1 = NonEmpty(values, nameof(values)).ToTargetEndian(GetEndian(mb));
            mb.WriteMultipleRegisters(CheckedStation(stationNo), CheckedAddress(address), v1.ToUInt16()!);
            return true;
        }

        /// <summary>
        /// 主站侧：按主站字节序转换后，写目标从站单个 <see cref="float"/>（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="value">原始业务值（未转换）。</param>
        /// <returns>恒为 true。</returns>
        public static bool WriteFloatRW(this IModbusMaster mb, int stationNo, int address, float value)
        {
            WriteFloatRW(mb, stationNo, address, new float[] { value });
            return true;
        }

        /// <summary>
        /// 主站侧：把字符串按 UTF8 编码写目标从站保持寄存器。
        /// 字符串线上布局固定为每寄存器内交换高低字节（与主流 PLC/HslCommunication 一致），不受主站字节序设置影响。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="str">要写入的字符串。</param>
        /// <returns>写入成功返回 true；<paramref name="str"/> 为 null 或空串时不发送并返回 false。</returns>
        public static bool WriteStringRW(this IModbusMaster mb, int stationNo, int address, string str)
        {
            var writeData = EncodeString(str);
            if (writeData.Length == 0) return false;//空串不发送，避免零长度请求
            mb.WriteMultipleRegisters(CheckedStation(stationNo), CheckedAddress(address), writeData);
            return true;
        }

        //读取
        //bool
        /// <summary>
        /// 主站侧：批量读取目标从站的离散输入（线圈输入，只读区，功能码 2）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">读取点数（1-2000，受单次响应上限约束）。</param>
        /// <returns>布尔值数组。</returns>
        public static bool[] ReadBoolR(this IModbusMaster mb, int stationNo, int address, int count)
        {
            return mb.ReadInputs(CheckedStation(stationNo), CheckedAddress(address), CheckedCount(count));
        }

        /// <summary>
        /// 主站侧：读取目标从站的单个离散输入。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">离散输入地址（0-65535）。</param>
        /// <returns>读取到的布尔值。</returns>
        public static bool ReadBoolR(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadBoolR(mb, stationNo, address, 1).FirstOrDefault();
        }

        //16位操作
        /// <summary>
        /// 主站侧：批量读取目标从站的输入寄存器（只读区，功能码 4），并按主站字节序还原为业务值。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">寄存器个数（1-125）。</param>
        /// <returns>还原后的业务值数组。</returns>
        public static UInt16[] ReadUInt16R(this IModbusMaster mb, int stationNo, int address, int count)
        {
            return mb.ReadInputRegisters(CheckedStation(stationNo), CheckedAddress(address), CheckedCount(count)).ToTargetEndian(GetEndian(mb));
        }

        /// <summary>
        /// 主站侧：读取目标从站的单个输入寄存器并还原为业务值。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">寄存器地址（0-65535）。</param>
        /// <returns>还原后的业务值。</returns>
        public static UInt16 ReadUInt16R(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadUInt16R(mb, stationNo, address, 1).FirstOrDefault();
        }

        /// <summary>
        /// 主站侧：批量读取目标从站输入寄存器，按主站字节序还原后作为有符号 16 位返回。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">寄存器个数（1-125）。</param>
        /// <returns>还原后的有符号值数组。</returns>
        public static Int16[] ReadInt16R(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters(CheckedStation(stationNo), CheckedAddress(address), CheckedCount(count));
            return res.ToTargetEndian(GetEndian(mb)).ToInt16();
        }

        /// <summary>
        /// 主站侧：读取目标从站的单个输入寄存器并还原为有符号 16 位值。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">寄存器地址（0-65535）。</param>
        /// <returns>还原后的有符号值。</returns>
        public static Int16 ReadInt16R(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadInt16R(mb, stationNo, address, 1).FirstOrDefault();
        }

        //32位操作
        /// <summary>
        /// 主站侧：批量读取目标从站输入寄存器，每个 <see cref="UInt32"/> 读取两个连续寄存器并按主站字节序还原。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">32 位值个数（实际读取 count×2 个寄存器，单次上限 125 个寄存器）。</param>
        /// <returns>还原后的业务值数组。</returns>
        public static UInt32[] ReadUInt32R(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters(CheckedStation(stationNo), CheckedAddress(address), WordRegCount(count)); 
            return res.ToUInt32().ToTargetEndian(GetEndian(mb));
        }

        /// <summary>
        /// 主站侧：读取目标从站单个 32 位无符号值（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <returns>还原后的业务值。</returns>
        public static UInt32 ReadUInt32R(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadUInt32R(mb, stationNo, address, 1).FirstOrDefault();
        }

        /// <summary>
        /// 主站侧：批量读取目标从站输入寄存器，每个 <see cref="Int32"/> 读取两个连续寄存器并按主站字节序还原。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">32 位值个数（实际读取 count×2 个寄存器，单次上限 125 个寄存器）。</param>
        /// <returns>还原后的有符号值数组。</returns>
        public static Int32[] ReadInt32R(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters(CheckedStation(stationNo), CheckedAddress(address), WordRegCount(count)); 
            return res.ToInt32().ToTargetEndian(GetEndian(mb));
        }

        /// <summary>
        /// 主站侧：读取目标从站单个 32 位有符号值（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <returns>还原后的有符号值。</returns>
        public static Int32 ReadInt32R(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadInt32R(mb, stationNo, address, 1).FirstOrDefault();
        }

        //float
        /// <summary>
        /// 主站侧：批量读取目标从站输入寄存器，每个 <see cref="float"/> 读取两个连续寄存器并按主站字节序还原。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">float 个数（实际读取 count×2 个寄存器，单次上限 125 个寄存器）。</param>
        /// <returns>还原后的 float 数组。</returns>
        public static float[] ReadFloatR(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters(CheckedStation(stationNo), CheckedAddress(address), WordRegCount(count)); 
            return res.ToFloat().ToTargetEndian(GetEndian(mb));
        }

        /// <summary>
        /// 主站侧：读取目标从站单个 <see cref="float"/>（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <returns>还原后的 float 值。</returns>
        public static float ReadFloatR(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadFloatR(mb, stationNo, address, 1).FirstOrDefault();
        }

        /// <summary>
        /// 主站侧：读取目标从站输入寄存器并按 UTF8 解码为字符串（奇数字节自动补齐后解码，末尾 0x00 被过滤）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">要读取的 UTF8 字节数（不是字符数）。</param>
        /// <returns>解码后的字符串。</returns>
        public static string ReadStringR(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadInputRegisters(CheckedStation(stationNo), CheckedAddress(address), StringRegCount(count));
            return DecodeString(res);
        }

        /// <summary>
        /// 主站侧：读取目标从站输入寄存器并解码为字符串，默认读取 2 字节（一个寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <returns>解码后的字符串。</returns>
        public static string ReadStringR(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadStringR(mb, stationNo, address, 2);
        }

        //保持寄存器
        //读取
        //bool
        /// <summary>
        /// 主站侧：批量读取目标从站的线圈（保持线圈，可读写区，功能码 1）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">读取点数（1-2000，受单次响应上限约束）。</param>
        /// <returns>布尔值数组。</returns>
        public static bool[] ReadBoolRW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            return mb.ReadCoils(CheckedStation(stationNo), CheckedAddress(address), CheckedCount(count));
        }

        /// <summary>
        /// 主站侧：读取目标从站的单个线圈。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">线圈地址（0-65535）。</param>
        /// <returns>读取到的布尔值。</returns>
        public static bool ReadBoolRW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadBoolRW(mb, stationNo, address, 1).FirstOrDefault();
        }

        //16位操作
        /// <summary>
        /// 主站侧：批量读取目标从站的保持寄存器（可读写区，功能码 3），并按主站字节序还原为业务值。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">寄存器个数（1-125）。</param>
        /// <returns>还原后的业务值数组。</returns>
        public static UInt16[] ReadUInt16RW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            return mb.ReadHoldingRegisters(CheckedStation(stationNo), CheckedAddress(address), CheckedCount(count)).ToTargetEndian(GetEndian(mb));
        }

        /// <summary>
        /// 主站侧：读取目标从站的单个保持寄存器并还原为业务值。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">寄存器地址（0-65535）。</param>
        /// <returns>还原后的业务值。</returns>
        public static UInt16 ReadUInt16RW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadUInt16RW(mb, stationNo, address, 1).FirstOrDefault();
        }

        /// <summary>
        /// 主站侧：批量读取目标从站保持寄存器，按主站字节序还原后作为有符号 16 位返回。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">寄存器个数（1-125）。</param>
        /// <returns>还原后的有符号值数组。</returns>
        public static Int16[] ReadInt16RW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters(CheckedStation(stationNo), CheckedAddress(address), CheckedCount(count));
            var ret = new Int16[count];
            var res2 = res.ToTargetEndian(GetEndian(mb));
            Buffer.BlockCopy(res2, 0, ret, 0, count * 2);
            return ret;
        }

        /// <summary>
        /// 主站侧：读取目标从站的单个保持寄存器并还原为有符号 16 位值。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">寄存器地址（0-65535）。</param>
        /// <returns>还原后的有符号值。</returns>
        public static Int16 ReadInt16RW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadInt16RW(mb, stationNo, address, 1).FirstOrDefault();
        }

        //32位操作
        /// <summary>
        /// 主站侧：批量读取目标从站保持寄存器，每个 <see cref="UInt32"/> 读取两个连续寄存器并按主站字节序还原。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">32 位值个数（实际读取 count×2 个寄存器，单次上限 125 个寄存器）。</param>
        /// <returns>还原后的业务值数组。</returns>
        public static UInt32[] ReadUInt32RW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters(CheckedStation(stationNo), CheckedAddress(address), WordRegCount(count)); 
            return res.ToUInt32().ToTargetEndian(GetEndian(mb)); 
        }

        /// <summary>
        /// 主站侧：读取目标从站单个 32 位无符号值（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <returns>还原后的业务值。</returns>
        public static UInt32 ReadUInt32RW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadUInt32RW(mb, stationNo, address, 1).FirstOrDefault();
        }

        /// <summary>
        /// 主站侧：批量读取目标从站保持寄存器，每个 <see cref="Int32"/> 读取两个连续寄存器并按主站字节序还原。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">32 位值个数（实际读取 count×2 个寄存器，单次上限 125 个寄存器）。</param>
        /// <returns>还原后的有符号值数组。</returns>
        public static Int32[] ReadInt32RW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters(CheckedStation(stationNo), CheckedAddress(address), WordRegCount(count)); 
            return res.ToInt32().ToTargetEndian(GetEndian(mb));
        }

        /// <summary>
        /// 主站侧：读取目标从站单个 32 位有符号值（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <returns>还原后的有符号值。</returns>
        public static Int32 ReadInt32RW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadInt32RW(mb, stationNo, address, 1).FirstOrDefault();
        }

        //float
        /// <summary>
        /// 主站侧：批量读取目标从站保持寄存器，每个 <see cref="float"/> 读取两个连续寄存器并按主站字节序还原。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">float 个数（实际读取 count×2 个寄存器，单次上限 125 个寄存器）。</param>
        /// <returns>还原后的 float 数组。</returns>
        public static float[] ReadFloatRW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters(CheckedStation(stationNo), CheckedAddress(address), WordRegCount(count)); 
            return res.ToFloat().ToTargetEndian(GetEndian(mb));
        }

        /// <summary>
        /// 主站侧：读取目标从站单个 <see cref="float"/>（占用两个连续寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <returns>还原后的 float 值。</returns>
        public static float ReadFloatRW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadFloatRW(mb, stationNo, address, 1).FirstOrDefault();
        }

        /// <summary>
        /// 主站侧：读取目标从站保持寄存器并按 UTF8 解码为字符串（末尾 0x00 被过滤）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <param name="count">要读取的 UTF8 字节数（不是字符数）。</param>
        /// <returns>解码后的字符串。</returns>
        public static string ReadStringRW(this IModbusMaster mb, int stationNo, int address, int count)
        {
            var res = mb.ReadHoldingRegisters(CheckedStation(stationNo), CheckedAddress(address), StringRegCount(count));
            return DecodeString(res);
        }

        /// <summary>
        /// 主站侧：读取目标从站保持寄存器并解码为字符串，默认读取 2 字节（一个寄存器）。
        /// </summary>
        /// <param name="mb">Modbus 主站对象。</param>
        /// <param name="stationNo">目标从站站号（1-247）。</param>
        /// <param name="address">起始地址（0-65535）。</param>
        /// <returns>解码后的字符串。</returns>
        public static string ReadStringRW(this IModbusMaster mb, int stationNo, int address)
        {
            return ReadStringRW(mb, stationNo, address, 2);
        }
    }

}
