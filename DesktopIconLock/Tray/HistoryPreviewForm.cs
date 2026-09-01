using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DesktopIconLock.Core;

namespace DesktopIconLock.Tray
{
    /// <summary>
    /// 历史布局预览与操作窗口
    /// 1. 悬停状态（Hover Mode）：
    ///    - 纯预览卡片，采用 WS_EX_NOACTIVATE 与 SW_SHOWNOACTIVATE，绝对不抢输入焦点。
    ///    - 用户鼠标在托盘菜单各历史项之间自由滑动时，托盘菜单保持 100% 展开不关闭，预览图平滑跟随切换。
    /// 2. 点击状态（Pinned Action Mode）：
    ///    - 用户左键点击菜单项后，托盘菜单关闭，窗口立即转换为带有【删除】【取消】【设为基准】【应用】按钮的操作控制面板。
    /// </summary>
    public class HistoryPreviewForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_HIDE = 0;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private readonly PictureBox _pictureBox;
        private readonly Label _informationLabel;
        private readonly FlowLayoutPanel _buttonPanel;
        private readonly Button _deleteButton;
        private readonly Button _cancelButton;
        private readonly Button _baseButton;
        private readonly Button _applyButton;
        private HistoryLayoutRecord _currentRecord;
        private bool _isPinned;

        public event Action<HistoryLayoutRecord> DeleteRequested;
        public event Action<HistoryLayoutRecord> BaseRequested;
        public event Action<HistoryLayoutRecord> ApplyRequested;

        public HistoryLayoutRecord CurrentRecord { get { return _currentRecord; } }
        public bool IsPinned { get { return _isPinned; } }

        public HistoryPreviewForm()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 28, 28);
            ForeColor = Color.White;
            ClientSize = new Size(680, 480);
            MaximizeBox = false;
            MinimizeBox = false;
            Text = "历史布局预览";

            _pictureBox = new PictureBox();
            _pictureBox.Location = new Point(10, 10);
            _pictureBox.Size = new Size(660, 360);
            _pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            _pictureBox.BackColor = Color.FromArgb(16, 16, 16);
            _pictureBox.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_pictureBox);

            _informationLabel = new Label();
            _informationLabel.Location = new Point(12, 380);
            _informationLabel.Size = new Size(656, 48);
            _informationLabel.AutoEllipsis = true;
            _informationLabel.ForeColor = Color.FromArgb(230, 230, 230);
            _informationLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
            Controls.Add(_informationLabel);

            _buttonPanel = new FlowLayoutPanel();
            _buttonPanel.Location = new Point(10, 436);
            _buttonPanel.Size = new Size(660, 44);
            _buttonPanel.FlowDirection = FlowDirection.RightToLeft;
            _buttonPanel.WrapContents = false;
            Controls.Add(_buttonPanel);

            _applyButton = CreateButton("应用", Color.FromArgb(0, 122, 204), Color.White);
            _baseButton = CreateButton("设为基准", Color.FromArgb(45, 137, 239), Color.White);
            _cancelButton = CreateButton("关闭/取消", Color.FromArgb(70, 70, 70), Color.White);
            _deleteButton = CreateButton("删除此记录", Color.FromArgb(180, 40, 40), Color.White);

            _buttonPanel.Controls.Add(_applyButton);
            _buttonPanel.Controls.Add(_baseButton);
            _buttonPanel.Controls.Add(_cancelButton);
            _buttonPanel.Controls.Add(_deleteButton);

            _applyButton.Click += delegate
            {
                if (_currentRecord != null && ApplyRequested != null)
                {
                    ApplyRequested(_currentRecord);
                }
            };
            _baseButton.Click += delegate
            {
                if (_currentRecord != null && BaseRequested != null)
                {
                    BaseRequested(_currentRecord);
                }
            };
            _deleteButton.Click += delegate
            {
                if (_currentRecord != null && DeleteRequested != null)
                {
                    DeleteRequested(_currentRecord);
                }
            };
            _cancelButton.Click += delegate { HidePreview(true); };

            SetActionButtonsVisible(false);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                return parameters;
            }
        }

        /// <summary>
        /// 悬停预览：使用无激活显示，菜单完全不失焦，保持展开
        /// </summary>
        public void ShowHoverPreview(HistoryLayoutRecord record, Point menuScreenLocation, Size menuSize)
        {
            if (_isPinned || record == null) return;

            _currentRecord = record;
            SetActionButtonsVisible(false);
            LoadScreenshot(record.ScreenshotPath);

            string baseTag = record.IsBase ? "【★ 当前自适应基准】" : "";
            _informationLabel.Text = string.Format(
                "{0} {1}\r\n记录时间：{2}   物理分辨率：{3}   缩放比例：{4}%   桌面图标数：{5}",
                record.MenuText,
                baseTag,
                record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                record.Profile != null ? record.Profile.Resolution : "未知",
                record.ScalePercent,
                record.Profile != null && record.Profile.Icons != null ? record.Profile.Icons.Count : 0);

            Text = "历史布局预览（点击菜单项弹出操作面板）";
            UpdateResponsiveLayout(menuScreenLocation);
            PositionCenterOfScreen(menuScreenLocation);

            if (!Visible)
            {
                ShowWindow(Handle, SW_SHOWNOACTIVATE);
                Visible = true;
            }
            else
            {
                Invalidate();
            }
        }

        /// <summary>
        /// 点击固定模式：显示操作按钮，成为操作面板
        /// </summary>
        public void PinActionPanel(HistoryLayoutRecord record, Point anchorPoint)
        {
            if (record == null) return;

            _currentRecord = record;
            _isPinned = true;
            SetActionButtonsVisible(true);
            LoadScreenshot(record.ScreenshotPath);

            string baseTag = record.IsBase ? "【★ 当前自适应基准】" : "";
            _informationLabel.Text = string.Format(
                "{0} {1}\r\n记录时间：{2}   物理分辨率：{3}   缩放比例：{4}%   桌面图标数：{5}",
                record.MenuText,
                baseTag,
                record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                record.Profile != null ? record.Profile.Resolution : "未知",
                record.ScalePercent,
                record.Profile != null && record.Profile.Icons != null ? record.Profile.Icons.Count : 0);

            _baseButton.Enabled = !record.IsBase;
            _baseButton.Text = record.IsBase ? "已是基准" : "设为基准";
            Text = "历史布局管理 - 操作控制面板";

            UpdateResponsiveLayout(anchorPoint);
            PositionCenterOfScreen(anchorPoint);

            if (!Visible)
            {
                Show();
            }
            BringToFront();
            Activate();
        }

        public void HidePreview(bool force)
        {
            if (!force && _isPinned) return;
            _isPinned = false;
            SetActionButtonsVisible(false);
            ShowWindow(Handle, SW_HIDE);
            Visible = false;
        }

        private void SetActionButtonsVisible(bool visible)
        {
            _buttonPanel.Visible = visible;
            _buttonPanel.Enabled = visible;
        }

        /// <summary>
        /// 响应式尺寸重算：
        /// 1. 屏幕分辨率在 1920*1080 以下（不含）时：窗口宽度为屏幕工作区宽度的 70%；
        /// 2. 1920*1080 含及以上时：窗口宽度为屏幕工作区宽度的 50%；
        /// 3. 图片展示区按 16:9 比例自适应缩放，按钮面板和说明标签高度跟随动态重排。
        /// </summary>
        private void UpdateResponsiveLayout(Point referencePoint)
        {
            Screen screen = referencePoint.X != 0 || referencePoint.Y != 0
                ? Screen.FromPoint(referencePoint)
                : Screen.PrimaryScreen;
            if (screen == null) screen = Screen.PrimaryScreen;

            Rectangle workArea = screen.WorkingArea;
            bool isUnder1080p = screen.Bounds.Width < 1920 || screen.Bounds.Height < 1080;
            double ratio = isUnder1080p ? 0.70 : 0.50;

            int targetWidth = (int)Math.Round(workArea.Width * ratio);
            targetWidth = Math.Max(560, Math.Min(targetWidth, workArea.Width - 40));

            int margin = 12;
            int picWidth = targetWidth - margin * 2;
            int picHeight = (int)Math.Round(picWidth * 9.0 / 16.0);

            // 垂直方向防溢出约束
            int maxPicHeight = Math.Max(220, workArea.Height - 240);
            if (picHeight > maxPicHeight)
            {
                picHeight = maxPicHeight;
                picWidth = (int)Math.Round(picHeight * 16.0 / 9.0);
                targetWidth = picWidth + margin * 2;
            }

            int infoHeight = 48;
            int buttonHeight = 44;
            int targetHeight = margin + picHeight + 10 + infoHeight + (_isPinned ? (8 + buttonHeight) : 0) + margin;

            ClientSize = new Size(targetWidth, targetHeight);

            _pictureBox.Location = new Point(margin, margin);
            _pictureBox.Size = new Size(picWidth, picHeight);

            int currentY = margin + picHeight + 10;
            _informationLabel.Location = new Point(margin + 2, currentY);
            _informationLabel.Size = new Size(picWidth - 4, infoHeight);

            currentY += infoHeight + 8;
            _buttonPanel.Location = new Point(margin, currentY);
            _buttonPanel.Size = new Size(picWidth, buttonHeight);
        }

        private Button CreateButton(string text, Color backColor, Color foreColor)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Size = new Size(106, 36);
            btn.Margin = new Padding(6, 2, 0, 2);
            btn.BackColor = backColor;
            btn.ForeColor = foreColor;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private void LoadScreenshot(string path)
        {
            Image oldImage = _pictureBox.Image;
            _pictureBox.Image = null;
            if (oldImage != null) oldImage.Dispose();

            if (!File.Exists(path)) return;

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image src = Image.FromStream(fs))
                {
                    _pictureBox.Image = new Bitmap(src);
                }
            }
            catch { }
        }

        private void PositionCenterOfScreen(Point referencePoint)
        {
            Screen screen = referencePoint.X != 0 || referencePoint.Y != 0
                ? Screen.FromPoint(referencePoint)
                : Screen.PrimaryScreen;
            if (screen == null) screen = Screen.PrimaryScreen;

            Rectangle workArea = screen.WorkingArea;
            int x = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
            int y = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);

            Location = new Point(x, y);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_pictureBox.Image != null)
                {
                    _pictureBox.Image.Dispose();
                    _pictureBox.Image = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
