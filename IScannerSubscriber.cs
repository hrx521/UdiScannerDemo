namespace UdiScannerDemo
{
    /// <summary>
    /// 扫码设备状态。
    /// Idle  = 空闲，可接收扫码
    /// Busy  = 刚产出结果后的短暂防重入窗口
    /// Error = 串口打开失败或通信异常
    /// </summary>
    public enum ScannerStatus
    {
        Idle,
        Busy,
        Error
    }

    /// <summary>
    /// 扫码结果订阅者。
    /// 注意：回调在串口接收线程触发，实现方必须自行 Invoke 回 UI 线程。
    /// </summary>
    public interface IScannerSubscriber
    {
        /// <param name="gs1Formatted">统一格式化文本，如 (01)...(11)...(17)...(10)...(21)...</param>
        /// <param name="rawHex">原始报文十六进制，用于审计与问题复盘</param>
        void OnScanReceived(string gs1Formatted, string rawHex);

        /// <param name="msg">错误描述</param>
        void OnScannerError(string msg);
    }
}

