using System;
using System.Collections.Generic;
using System.Linq;

namespace UdiScannerDemo
{
    /// <summary>
    /// GS1 UDI 裸字节流解析器（VCP串口原始流，含0x1D GS分隔符）
    /// 只支持ASCII半角AI，输出标准 (01)xxxx(11)xxxx 格式字符串
    /// 适配UDI扫码枪虚拟串口模式，前置0x0A换行等控制字符自动跳过
    /// </summary>
    public static class Gs1UdiParser
    {
        /// <summary>
        /// GS分组分隔符 0x1D
        /// </summary>
        private const byte GS = 0x1D;

        private static bool IsAsciiDigit(byte b)
        {
            return b >= 0x30 && b <= 0x39;
        }

        /// <summary>
        /// 解析裸GS1字节数组，返回带括号格式化文本
        /// </summary>
        /// <param name="rawPacket">串口读到的原始字节包（不含末尾CR 0x0D）</param>
        /// <returns>如 (01)06921041411591(11)230419(17)280418(10)B23100613(21)00226</returns>
        public static string Parse(byte[] rawPacket)
        {
            if (rawPacket == null || rawPacket.Length == 0)
                return string.Empty;

            int pos = 0;
            List<string> segments = new List<string>();

            // 跳过前置控制字符：0x0A LF、0x00空字节等
            while (pos < rawPacket.Length)
            {
                byte b = rawPacket[pos];
                if (b == 0x0A || b == 0x00)
                {
                    pos++;
                }
                else
                {
                    break;
                }
            }

            while (pos + 1 < rawPacket.Length)
            {
                byte b1 = rawPacket[pos];
                byte b2 = rawPacket[pos + 1];

                // 判断2位AI是否都是ASCII数字
                if (!IsAsciiDigit(b1) || !IsAsciiDigit(b2))
                    break;

                string ai = $"{(char)b1}{(char)b2}";
                pos += 2;

                string value;
                // 按GS1常用AI规则处理
                switch (ai)
                {
                    case "01": // GTIN 固定14位
                        value = ReadFixedAscii(rawPacket, ref pos, 14);
                        break;
                    case "11": // 生产日期 固定6位 YYMMDD
                        value = ReadFixedAscii(rawPacket, ref pos, 6);
                        break;
                    case "17": // 有效期 固定6位 YYMMDD
                        value = ReadFixedAscii(rawPacket, ref pos, 6);
                        break;
                    case "10": // 批号，变长，遇GS或包尾终止
                    case "21": // 序列号，变长，遇GS或包尾终止
                        value = ReadVariableAscii(rawPacket, ref pos);
                        break;
                    default:
                        // 遇到未实现AI，停止解析，防止乱吞字节
                        goto EndParse;
                }

                if (string.IsNullOrEmpty(value))
                    break;

                segments.Add($"({ai}){value}");
            }

        EndParse:
            return string.Join("", segments);
        }

        /// <summary>
        /// 读取固定长度ASCII字符串
        /// </summary>
        private static string ReadFixedAscii(byte[] data, ref int pos, int len)
        {
            if (pos + len > data.Length)
                return string.Empty;

            var slice = new byte[len];
            Array.Copy(data, pos, slice, 0, len);
            pos += len;
            return System.Text.Encoding.ASCII.GetString(slice);
        }

        /// <summary>
        /// 读取变长字段，遇到GS(0x1D)或者包结尾停止，GS本身吃掉
        /// </summary>
        private static string ReadVariableAscii(byte[] data, ref int pos)
        {
            int start = pos;
            while (pos < data.Length)
            {
                if (data[pos] == GS)
                {
                    pos++; // 跳过GS分隔符
                    break;
                }
                pos++;
            }
            int readLen = pos - start;
            if (readLen <= 0)
                return string.Empty;

            var slice = new byte[readLen];
            Array.Copy(data, start, slice, 0, readLen);
            return System.Text.Encoding.ASCII.GetString(slice);
        }
    }
}
