using System;
using System.Drawing;
using System.Windows.Forms;

namespace UdiScannerDemo
{
    /// <summary>
    /// UDI 录入子窗体。
    /// 订阅策略：Activated 订阅、Deactivate 退订，实现"扫码跟随当前激活窗口"。
    /// 不调用任何串口启停方法。
    /// </summary>
    public class ChildUdiForm : Form, IScannerSubscriber
    {
        private readonly AutoComScannerManager _scanner;
        private bool _isSubscribed;

        private TextBox _txtGs1;
        private TextBox _txtRawHex;
        private TextBox _txtActive;
        private Label _lblFocus;

        public ChildUdiForm(AutoComScannerManager scanner)
        {
            _scanner = scanner;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "UDI 录入";
            Size = new Size(760, 420);

            var lblGs1 = new Label { Text = "GS1 格式化文本：", AutoSize = true, Location = new Point(12, 12) };

            _txtGs1 = new TextBox
            {
                Location = new Point(12, 34),
                Width = 700,
                Font = new Font("Consolas", 10f)
            };

            var lblRaw = new Label { Text = "原始报文 Hex：", AutoSize = true, Location = new Point(12, 76) };

            _txtRawHex = new TextBox
            {
                Location = new Point(12, 98),
                Width = 700,
                Height = 80,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                ReadOnly = true
            };

            _lblFocus = new Label
            {
                Text = "当前焦点框（可在此继续输入）：",
                AutoSize = true,
                Location = new Point(12, 196)
            };

            _txtActive = new TextBox
            {
                Location = new Point(12, 218),
                Width = 700,
                Text = "点这里，扫码结果仍写入 GS1 框"
            };

            Controls.AddRange(new Control[] { lblGs1, _txtGs1, lblRaw, _txtRawHex, _lblFocus, _txtActive });

            Activated += ChildUdiForm_Activated;
            Deactivate += ChildUdiForm_Deactivate;
            FormClosing += ChildUdiForm_FormClosing;
        }

        private void ChildUdiForm_Activated(object sender, EventArgs e)
        {
            if (_isSubscribed) return;
            _scanner.Subscribe(this);
            _isSubscribed = true;
        }

        private void ChildUdiForm_Deactivate(object sender, EventArgs e)
        {
            if (!_isSubscribed) return;
            _scanner.Unsubscribe(this);
            _isSubscribed = false;
        }

        private void ChildUdiForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 兜底：关闭瞬间若仍处于订阅状态则释放
            if (!_isSubscribed) return;
            _scanner.Unsubscribe(this);
            _isSubscribed = false;
        }

        #region IScannerSubscriber

        public void OnScanReceived(string gs1Formatted, string rawHex)
        {
            // 回调运行在串口接收线程，必须回到 UI 线程
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string>(OnScanReceived), gs1Formatted, rawHex);
                return;
            }

            _txtGs1.Text = gs1Formatted;
            _txtGs1.SelectionStart = _txtGs1.TextLength;
            _txtRawHex.Text = rawHex;
        }

        public void OnScannerError(string msg)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(OnScannerError), msg);
                return;
            }
            MessageBox.Show(this, msg, "扫码枪");
        }

        #endregion
    }
}
