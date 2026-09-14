using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DesktopIconLock.Core;

namespace DesktopIconLock.Tray
{
    /// <summary>
    /// 历史布局浏览汇总与管理窗口
    /// 点击托盘菜单中的【历史布局】后直接以弹窗形式展示所有历史布局卡片视图（网格矩阵），
    /// 彻底避免鼠标在多级子菜单移动时菜单因失焦意外关闭的问题。
    /// </summary>
    public class HistoryPreviewForm : Form
    {
        private readonly FlowLayoutPanel _cardContainer;
        private readonly Label _headerTitle;
        private readonly Label _headerSubtitle;
        private readonly Panel _headerPanel;
        private readonly List<HistoryLayoutRecord> _loadedRecords = new List<HistoryLayoutRecord>();

        public event Action<HistoryLayoutRecord> DeleteRequested;
        public event Action<HistoryLayoutRecord> BaseRequested;
        public event Action<HistoryLayoutRecord> ApplyRequested;

        public HistoryPreviewForm()
        {
            Text = "历史布局浏览汇总";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 28);
            ForeColor = Color.White;
            MinimumSize = new Size(680, 500);
            ClientSize = new Size(1020, 680);
            ShowInTaskbar = true;
            MaximizeBox = true;
            MinimizeBox = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            _headerPanel = new Panel();
            _headerPanel.Dock = DockStyle.Top;
            _headerPanel.Height = 65;
            _headerPanel.BackColor = Color.FromArgb(32, 32, 38);
            _headerPanel.Padding = new Padding(24, 12, 24, 10);
            Controls.Add(_headerPanel);

            _headerTitle = new Label();
            _headerTitle.Text = "历史布局浏览汇总";
            _headerTitle.Font = new Font("Microsoft YaHei UI", 12.5F, FontStyle.Bold);
            _headerTitle.ForeColor = Color.White;
            _headerTitle.AutoSize = true;
            _headerTitle.Location = new Point(20, 10);
            _headerPanel.Controls.Add(_headerTitle);

            _headerSubtitle = new Label();
            _headerSubtitle.Text = "点击任意布局的【应用】即可立即恢复图标位置，或将其【设为基准】与【删除】。";
            _headerSubtitle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            _headerSubtitle.ForeColor = Color.FromArgb(180, 180, 190);
            _headerSubtitle.AutoSize = true;
            _headerSubtitle.Location = new Point(22, 36);
            _headerPanel.Controls.Add(_headerSubtitle);

            _cardContainer = new FlowLayoutPanel();
            _cardContainer.Dock = DockStyle.Fill;
            _cardContainer.AutoScroll = true;
            _cardContainer.Padding = new Padding(20, 16, 20, 20);
            _cardContainer.WrapContents = true;
            _cardContainer.FlowDirection = FlowDirection.LeftToRight;
            _cardContainer.BackColor = Color.FromArgb(20, 20, 24);
            Controls.Add(_cardContainer);
            _cardContainer.BringToFront();
        }

        public void ShowOverview(List<HistoryLayoutRecord> records)
        {
            _loadedRecords.Clear();
            if (records != null)
            {
                _loadedRecords.AddRange(records);
            }

            RenderCards();

            if (!Visible)
            {
                Show();
            }
            BringToFront();
            Activate();
        }

        public void RefreshList(List<HistoryLayoutRecord> records)
        {
            _loadedRecords.Clear();
            if (records != null)
            {
                _loadedRecords.AddRange(records);
            }
            RenderCards();
        }

        private void RenderCards()
        {
            _cardContainer.SuspendLayout();

            for (int i = _cardContainer.Controls.Count - 1; i >= 0; i--)
            {
                Control ctrl = _cardContainer.Controls[i];
                _cardContainer.Controls.RemoveAt(i);
                ctrl.Dispose();
            }

            if (_loadedRecords.Count == 0)
            {
                Label emptyLabel = new Label();
                emptyLabel.Text = "暂无历史布局记录\n\n您可以在托盘右键菜单中点击【保存当前布局】创建记录。";
                emptyLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular);
                emptyLabel.ForeColor = Color.FromArgb(150, 150, 160);
                emptyLabel.AutoSize = true;
                emptyLabel.Padding = new Padding(30);
                _cardContainer.Controls.Add(emptyLabel);
                _cardContainer.ResumeLayout();
                return;
            }

            int cardWidth = 300;
            int cardHeight = 290;

            for (int i = 0; i < _loadedRecords.Count; i++)
            {
                HistoryLayoutRecord record = _loadedRecords[i];
                Panel card = CreateRecordCard(record, i + 1, cardWidth, cardHeight);
                _cardContainer.Controls.Add(card);
            }

            _cardContainer.ResumeLayout();
        }

        private Panel CreateRecordCard(HistoryLayoutRecord record, int index, int width, int height)
        {
            Panel card = new Panel();
            card.Size = new Size(width, height);
            card.Margin = new Padding(12, 12, 12, 12);
            card.BackColor = Color.FromArgb(32, 32, 38);
            card.BorderStyle = BorderStyle.FixedSingle;

            Panel topBar = new Panel();
            topBar.Location = new Point(0, 0);
            topBar.Size = new Size(width, 32);
            topBar.BackColor = record.IsBase ? Color.FromArgb(30, 80, 50) : Color.FromArgb(42, 42, 50);
            card.Controls.Add(topBar);

            Label titleLabel = new Label();
            string baseBadge = record.IsBase ? " [★ 当前基准]" : "";
            titleLabel.Text = string.Format("布局 {0}{1}", index, baseBadge);
            titleLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            titleLabel.ForeColor = record.IsBase ? Color.FromArgb(120, 255, 170) : Color.FromArgb(0, 180, 255);
            titleLabel.Location = new Point(8, 6);
            titleLabel.Size = new Size(width - 16, 20);
            titleLabel.AutoEllipsis = true;
            topBar.Controls.Add(titleLabel);

            PictureBox thumbBox = new PictureBox();
            thumbBox.Location = new Point(10, 38);
            thumbBox.Size = new Size(width - 20, 140);
            thumbBox.SizeMode = PictureBoxSizeMode.Zoom;
            thumbBox.BackColor = Color.FromArgb(14, 14, 18);
            thumbBox.BorderStyle = BorderStyle.FixedSingle;
            thumbBox.Cursor = Cursors.Hand;

            if (File.Exists(record.ScreenshotPath))
            {
                try
                {
                    using (FileStream fs = new FileStream(record.ScreenshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (Image src = Image.FromStream(fs))
                    {
                        thumbBox.Image = new Bitmap(src);
                    }
                }
                catch { }
            }
            card.Controls.Add(thumbBox);

            Label infoLabel = new Label();
            infoLabel.Location = new Point(10, 182);
            infoLabel.Size = new Size(width - 20, 52);
            infoLabel.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular);
            infoLabel.ForeColor = Color.FromArgb(200, 200, 210);
            infoLabel.Text = string.Format(
                "分辨率: {0} @ {1}%\n图标数: {2} 项\n时间: {3}",
                record.Profile != null ? record.Profile.Resolution : "未知",
                record.ScalePercent,
                record.Profile != null && record.Profile.Icons != null ? record.Profile.Icons.Count : 0,
                record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            card.Controls.Add(infoLabel);

            Panel actionsPanel = new Panel();
            actionsPanel.Location = new Point(8, 240);
            actionsPanel.Size = new Size(width - 16, 40);
            card.Controls.Add(actionsPanel);

            Button applyBtn = CreateActionButton("应用", Color.FromArgb(0, 122, 204), Color.White, 65, 30);
            applyBtn.Location = new Point(0, 4);
            applyBtn.Click += delegate
            {
                if (ApplyRequested != null)
                {
                    ApplyRequested(record);
                }
            };
            actionsPanel.Controls.Add(applyBtn);

            Button baseBtn = CreateActionButton(record.IsBase ? "已是基准" : "设为基准", Color.FromArgb(45, 137, 239), Color.White, 80, 30);
            baseBtn.Location = new Point(70, 4);
            baseBtn.Enabled = !record.IsBase;
            if (record.IsBase)
            {
                baseBtn.BackColor = Color.FromArgb(60, 70, 80);
                baseBtn.ForeColor = Color.FromArgb(160, 160, 170);
            }
            baseBtn.Click += delegate
            {
                if (BaseRequested != null)
                {
                    BaseRequested(record);
                }
            };
            actionsPanel.Controls.Add(baseBtn);

            Button delBtn = CreateActionButton("删除", Color.FromArgb(180, 40, 40), Color.White, 60, 30);
            delBtn.Location = new Point(actionsPanel.Width - 60, 4);
            delBtn.Click += delegate
            {
                if (DeleteRequested != null)
                {
                    DeleteRequested(record);
                }
            };
            actionsPanel.Controls.Add(delBtn);

            return card;
        }

        private Button CreateActionButton(string text, Color backColor, Color foreColor, int width, int height)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Size = new Size(width, height);
            btn.BackColor = backColor;
            btn.ForeColor = foreColor;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
