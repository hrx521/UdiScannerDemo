using System;
using System.Drawing;
using System.Windows.Forms;

namespace UdiScannerDemo
{
    /// <summary>
    /// MDI 主窗体。
    /// 唯一职责：创建并持有扫码管理器，启动时打开串口，退出时释放串口。
    /// 子窗体不得调用 StartScan / StopScan。
    /// </summary>
    public class MainMdiForm : Form
    {
        public readonly AutoComScannerManager ScannerManager = new AutoComScannerManager();

        private TextBox _txtPort;
        private Button _btnOpen;
        private Button _btnClose;
        private Label _lblStatus;
        private Button _btnNewChild;
        private Timer _statusTimer;
        private int _childIndex;

        public MainMdiForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "UDI Scanner Demo — UDI扫码枪 (VCP)";
            Size = new Size(1000, 680);
            StartPosition = FormStartPosition.CenterScreen;
            IsMdiContainer = true;

            var panel = new Panel { Dock = DockStyle.Top, Height = 44 };

            var lblPort = new Label
            {
                Text = "串口：",
                AutoSize = true,
                Location = new Point(12, 14)
            };

            _txtPort = new TextBox
            {
                Text = "COM3",
                Location = new Point(60, 10),
                Width = 90
            };

            _btnOpen = new Button
            {
                Text = "打开",
                Location = new Point(160, 9),
                Width = 70
            };
            _btnOpen.Click += BtnOpen_Click;

            _btnClose = new Button
            {
                Text = "关闭",
                Location = new Point(238, 9),
                Width = 70,
                Enabled = false
            };
            _btnClose.Click += BtnClose_Click;

            _lblStatus = new Label
            {
                Text = "未启动",
                AutoSize = true,
                Location = new Point(320, 14)
            };

            _btnNewChild = new Button
            {
                Text = "新建录入窗体",
                Location = new Point(440, 9),
                Width = 120
            };
            _btnNewChild.Click += BtnNewChild_Click;

            panel.Controls.AddRange(new Control[] { lblPort, _txtPort, _btnOpen, _btnClose, _lblStatus, _btnNewChild });
            Controls.Add(panel);

            _statusTimer = new Timer { Interval = 300 };
            _statusTimer.Tick += (s, e) => RefreshStatus();
            _statusTimer.Start();

            FormClosing += MainMdiForm_FormClosing;
        }

        private void BtnOpen_Click(object sender, EventArgs e)
        {
            ScannerManager.PortName = _txtPort.Text.Trim();
            bool ok = ScannerManager.StartScan();
            _btnOpen.Enabled = !ok;
            _btnClose.Enabled = ok;
            if (!ok)
                MessageBox.Show(this, "打开串口失败，请检查端口号、驱动与是否被占用。", "扫码枪");
        }

        private void BtnClose_Click(object sender, EventArgs e)
        {
            ScannerManager.StopScan();
            _btnOpen.Enabled = true;
            _btnClose.Enabled = false;
        }

        private void BtnNewChild_Click(object sender, EventArgs e)
        {
            _childIndex++;
            var child = new ChildUdiForm(ScannerManager) { MdiParent = this };
            child.Text = "UDI 录入 — " + _childIndex;
            child.Show();
        }

        private void RefreshStatus()
        {
            ScannerStatus status = ScannerManager.Status;
            switch (status)
            {
                case ScannerStatus.Idle:
                    _lblStatus.Text = "就绪，可扫码";
                    break;
                case ScannerStatus.Busy:
                    _lblStatus.Text = "处理中";
                    break;
                case ScannerStatus.Error:
                    _lblStatus.Text = "异常";
                    break;
            }
        }

        private void MainMdiForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 唯一释放点
            ScannerManager.StopScan();
        }
    }
}
