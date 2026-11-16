using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Windows.Forms;

namespace UdiScannerDemo
{
    /// <summary>
    /// USB 虚拟串口（CDC / VCP）扫码管理器。
    ///
    /// 职责边界：
    ///   1) 串口生命周期管理（全局只开一次，由主窗体负责启停）
    ///   2) 流式数据分包（以 0x0D 作为帧结束符）
    ///   3) GS1 解析与格式化（委托给 Gs1UdiParser）
    ///   4) 线程安全的订阅分发
    ///
    /// 线程模型：
    ///   DataReceived 回调运行在线程池线程；Subscribe / Unsubscribe / StartScan / StopScan
    ///   预期在 UI 线程调用。共享状态由两把互不嵌套的锁保护。
    /// </summary>
    public class AutoComScannerManager
    {
        private const byte CR = 0x0D;
        private const byte LF = 0x0A;
        private const byte GS = 0x1D;
        private const byte OPEN_PAREN = 0x28;
        private const byte CLOSE_PAREN = 0x29;

        private SerialPort _serialPort;
        private readonly List<byte> _buffer = new List<byte>();
        private Timer _idleTimer;
        private ScannerStatus _status = ScannerStatus.Idle;

        private readonly object _bufferLock = new object();
        private readonly object _subscriberLock = new object();
        private readonly HashSet<IScannerSubscriber> _subscribers = new HashSet<IScannerSubscriber>();

        public string PortName { get; set; }
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>Busy 状态保持时长，用于拦截重复触发。</summary>
        public int BusyHoldMs { get; set; } = 300;

        public ScannerStatus Status
        {
            get { lock (_bufferLock) return _status; }
        }

        #region 订阅管理

        public void Subscribe(IScannerSubscriber subscriber)
        {
            if (subscriber == null) return;
            lock (_subscriberLock)
            {
                _subscribers.Add(subscriber);
            }
        }

        public void Unsubscribe(IScannerSubscriber subscriber)
        {
            if (subscriber == null) return;
            lock (_subscriberLock)
            {
                _subscribers.Remove(subscriber);
            }
        }

        private void NotifyScanReceived(string gs1Formatted, string rawHex)
        {
            List<IScannerSubscriber> snapshot;
            lock (_subscriberLock)
            {
                snapshot = _subscribers.ToList();
            }
            foreach (var s in snapshot)
            {
                try
                {
                    s.OnScanReceived(gs1Formatted, rawHex);
                }
                catch
                {
                    // 单个订阅者异常不得中断其它订阅者
                }
            }
        }

        private void NotifyError(string message)
        {
            List<IScannerSubscriber> snapshot;
            lock (_subscriberLock)
            {
                snapshot = _subscribers.ToList();
            }
            foreach (var s in snapshot)
            {
                try
                {
                    s.OnScannerError(message);
                }
                catch
                {
                }
            }
        }

        #endregion

        #region 串口启停（仅主窗体调用）

        public bool StartScan()
        {
            string error = null;

            lock (_bufferLock)
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    return true; // 已打开，直接视为可用，不重复打开
                }
                else
                {
                    try
                    {
                        _serialPort = new SerialPort(PortName, BaudRate, Parity, DataBits, StopBits);
                        _serialPort.ReadTimeout = 200;
                        _serialPort.WriteTimeout = 200;
                        _serialPort.DataReceived += SerialPort_DataReceived;
                        _serialPort.Open();

                        _idleTimer = new Timer { Interval = BusyHoldMs };
                        _idleTimer.Tick += IdleTimer_Tick;

                        _status = ScannerStatus.Idle;
                    }
                    catch (Exception ex)
                    {
                        SafeClosePort();
                        _status = ScannerStatus.Error;
                        error = "打开串口失败：" + ex.Message;
                    }
                }
            }

            // 订阅者回调在锁外执行，避免回调内再次访问管理器时产生锁竞争
            if (error != null)
            {
                NotifyError(error);
                return false;
            }

            // alreadyOpen 与本次成功打开均视为可用
            return true;
        }

        public void StopScan()
        {
            lock (_bufferLock)
            {
                SafeClosePort();
                if (_idleTimer != null)
                {
                    _idleTimer.Stop();
                    _idleTimer.Dispose();
                    _idleTimer = null;
                }
                _buffer.Clear();
                _status = ScannerStatus.Idle;
            }
        }

        /// <summary>
        /// 关闭顺序固定为 解绑 → Close → Dispose → 置空。
        /// 不在 DataReceived 回调内调用，避免 SerialPort 关闭死锁。
        /// </summary>
        private void SafeClosePort()
        {
            if (_serialPort == null) return;
            try
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.Close();
                }
            }
            catch
            {
            }
            try { _serialPort.Dispose(); } catch { }
            _serialPort = null;
        }

        private void IdleTimer_Tick(object sender, EventArgs e)
        {
            lock (_bufferLock)
            {
                if (_status == ScannerStatus.Busy)
                    _status = ScannerStatus.Idle;
            }
            if (_idleTimer != null) _idleTimer.Stop();
        }

        #endregion

        #region 接收 / 分包 / 解析

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort sp = sender as SerialPort;
            if (sp == null || !sp.IsOpen) return;

            int count;
            try
            {
                count = sp.BytesToRead;
                if (count <= 0) return;
                byte[] temp = new byte[count];
                sp.Read(temp, 0, count);

                lock (_bufferLock)
                {
                    _buffer.AddRange(temp);
                }
            }
            catch (Exception ex)
            {
                lock (_bufferLock)
                {
                    _status = ScannerStatus.Error;
                }
                NotifyError("读取串口失败：" + ex.Message);
                return;
            }

            TryParsePacket();
        }

        /// <summary>
        /// 分包器：按 0x0D 切出单帧。
        /// 解析器只接收单帧完整数据，不接触流式缓冲区。
        /// </summary>
        private void TryParsePacket()
        {
            byte[] frame = null;

            lock (_bufferLock)
            {
                int crIndex = _buffer.IndexOf(CR);
                if (crIndex < 0) return;

                frame = _buffer.GetRange(0, crIndex).ToArray();
                _buffer.RemoveRange(0, crIndex + 1);

                _status = ScannerStatus.Busy;
            }

            if (frame == null || frame.Length == 0) return;

            string rawHex = BitConverter.ToString(frame).Replace("-", " ");
            string formatted;

            if (IsBracketFormattedFrame(frame))
            {
                // 固件已输出带括号格式：清理控制字符后直接使用，不再进入裸流解析。
                formatted = CleanControlBytes(frame);
            }
            else
            {
                formatted = Gs1UdiParser.Parse(frame);
                if (string.IsNullOrEmpty(formatted))
                {
                    formatted = "[PARSE_FAIL] " + SafeAscii(frame);
                }
            }

            NotifyScanReceived(formatted, rawHex);

            lock (_bufferLock)
            {
                if (_idleTimer != null) _idleTimer.Start();
            }
        }

        /// <summary>
        /// 字节层面判断：帧内是否同时存在 '(' 与 ')'。
        /// 不使用字符串 StartsWith，避免前置控制字符与 Trim 行为差异导致误判。
        /// </summary>
        private static bool IsBracketFormattedFrame(byte[] frame)
        {
            bool hasOpen = false, hasClose = false;
            foreach (byte b in frame)
            {
                if (b == OPEN_PAREN) hasOpen = true;
                else if (b == CLOSE_PAREN) hasClose = true;
                if (hasOpen && hasClose) return true;
            }
            return false;
        }

        private static string CleanControlBytes(byte[] frame)
        {
            var sb = new System.Text.StringBuilder(frame.Length);
            foreach (byte b in frame)
            {
                if (b == GS || b == CR || b == LF || b == 0x00) continue;
                if (b >= 0x20 && b <= 0x7E) sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }

        private static string SafeAscii(byte[] frame)
        {
            var sb = new System.Text.StringBuilder(frame.Length);
            foreach (byte b in frame)
            {
                if (b >= 0x20 && b <= 0x7E) sb.Append((char)b);
            }
            return sb.ToString();
        }

        #endregion
    }
}
