using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopIconLock.Tray
{
    public enum LayoutChangePromptChoice
    {
        SaveCurrent,
        Cancel,
        RestoreSaved
    }

    public sealed class LayoutChangePromptForm : Form
    {
        private LayoutChangePromptChoice _choice;

        public LayoutChangePromptChoice Choice
        {
            get { return _choice; }
        }

        public LayoutChangePromptForm()
        {
            _choice = LayoutChangePromptChoice.Cancel;

            Text = "桌面布局变动";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(28, 28, 28);
            ForeColor = Color.White;
            ClientSize = new Size(560, 220);

            Label titleLabel = new Label();
            titleLabel.Location = new Point(24, 22);
            titleLabel.Size = new Size(512, 32);
            titleLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            titleLabel.ForeColor = Color.White;
            titleLabel.Text = "桌面布局已发生变动，是否保存当前布局？";
            Controls.Add(titleLabel);

            Label explanationLabel = new Label();
            explanationLabel.Location = new Point(26, 64);
            explanationLabel.Size = new Size(508, 84);
            explanationLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
            explanationLabel.ForeColor = Color.FromArgb(220, 220, 220);
            explanationLabel.Text =
                "保存当前布局：保存并锁定现在的图标位置。\r\n" +
                "恢复已保存布局：放弃本次调整，按现有匹配规则恢复。\r\n" +
                "取消：继续保持解锁，不保存也不移动图标。";
            Controls.Add(explanationLabel);

            Button restoreButton = CreateButton(
                "恢复已保存布局",
                new Point(24, 166),
                new Size(160, 36),
                Color.FromArgb(180, 90, 20));
            restoreButton.Click += delegate
            {
                _choice = LayoutChangePromptChoice.RestoreSaved;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(restoreButton);

            Button cancelButton = CreateButton(
                "取消",
                new Point(300, 166),
                new Size(104, 36),
                Color.FromArgb(70, 70, 70));
            cancelButton.Click += delegate
            {
                _choice = LayoutChangePromptChoice.Cancel;
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(cancelButton);

            Button saveButton = CreateButton(
                "保存当前布局",
                new Point(416, 166),
                new Size(120, 36),
                Color.FromArgb(0, 122, 204));
            saveButton.Click += delegate
            {
                _choice = LayoutChangePromptChoice.SaveCurrent;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(saveButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private static Button CreateButton(
            string text,
            Point location,
            Size size,
            Color backColor)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = location;
            button.Size = size;
            button.BackColor = backColor;
            button.ForeColor = Color.White;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            return button;
        }
    }
}
