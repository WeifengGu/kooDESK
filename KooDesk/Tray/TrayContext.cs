using System;
using System.Drawing;
using System.Windows.Forms;
using KooDesk.Core;
using KooDesk.Native;
using KooDesk.Resources;

namespace KooDesk.Tray
{
    public class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly LockController _controller;
        private readonly MessageWindow _messageWindow;
        private ToolStripMenuItem _menuStartup;
        private ToolStripMenuItem _menuHistory;

        private const string LockedIconResource = "red.ico";
        private const string UnlockedIconResource = "green.ico";

        private Icon _lockedIcon;
        private Icon _unlockedIcon;

        public TrayContext(LockController controller)
        {
            _controller = controller;
            _messageWindow = new MessageWindow(_controller);

            _controller.SetRehookMarshalWindow(_messageWindow.Handle);

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = GetAppIcon(_controller.IsLocked);
            _notifyIcon.Text = _controller.IsLocked ? UIText.App.TooltipLocked : UIText.App.TooltipUnlocked;
            _notifyIcon.Visible = true;

            BuildContextMenu();

            _notifyIcon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ToggleLock();
                }
            };

            CheckFirstRunGuide();
        }

        private void BuildContextMenu()
        {
            ContextMenuStrip contextMenu = new ContextMenuStrip();

            ToolStripMenuItem menuSaveExact = new ToolStripMenuItem(UIText.Menu.SaveCurrent);
            menuSaveExact.Click += delegate(object s, EventArgs e)
            {
                try
                {
                    contextMenu.Close();
                    Application.DoEvents();
                    DesktopProfile profile = _controller.SaveCurrentLayout();
                    RefreshBaseLayoutMenu();
                    ShowNotification(
                        UIText.Balloon.SaveDoneTitle,
                        string.Format(
                        UIText.Balloon.SaveDoneBodyFormat,
                        profile.Resolution,
                        profile.ScalePercent,
                        profile.Icons.Count));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format(UIText.Dialog.SaveFailedFormat, ex.Message),
                        UIText.Dialog.TitleSaveCurrent,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            };
            contextMenu.Items.Add(menuSaveExact);

            _menuHistory = new ToolStripMenuItem(UIText.Menu.ApplyBase);
            _menuHistory.Click += delegate(object s, EventArgs e) { ApplyBaseLayout(); };
            contextMenu.Items.Add(_menuHistory);

            contextMenu.Items.Add(new ToolStripSeparator());

            _menuStartup = new ToolStripMenuItem(UIText.Menu.Startup);
            _menuStartup.CheckOnClick = false;
            _menuStartup.Checked = StartupManager.IsEnabled();
            _menuStartup.Click += delegate(object s, EventArgs e)
            {
                bool targetState = !_menuStartup.Checked;
                string errorMessage;
                if (StartupManager.SetEnabled(targetState, out errorMessage))
                {
                    _menuStartup.Checked = targetState;
                    ShowNotification(
                        UIText.Balloon.StartupTitle,
                        targetState
                        ? UIText.Balloon.StartupEnabled
                        : UIText.Balloon.StartupDisabled);
                }
                else
                {
                    MessageBox.Show(
                    string.Format(UIText.Dialog.StartupFailedFormat, errorMessage ?? UIText.Dialog.UnknownError),
                    UIText.Dialog.TitleStartup,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                }
            };
            contextMenu.Items.Add(_menuStartup);

            contextMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem menuAbout = new ToolStripMenuItem(UIText.Menu.About);
            menuAbout.Click += delegate(object s, EventArgs e) { ShowAboutDialog(); };
            contextMenu.Items.Add(menuAbout);

            ToolStripMenuItem menuExit = new ToolStripMenuItem(UIText.Menu.Exit);
            menuExit.Click += delegate(object s, EventArgs e) { ExitApplication(); };
            contextMenu.Items.Add(menuExit);

            _notifyIcon.ContextMenuStrip = contextMenu;
            RefreshBaseLayoutMenu();
        }

        private void RefreshBaseLayoutMenu()
        {
            bool hasBaseLayout = _controller.HasSavedBaseLayout;
            _menuHistory.Enabled = hasBaseLayout;
            _menuHistory.Text = hasBaseLayout
            ? UIText.Menu.ApplyBase
            : UIText.Menu.ApplyBaseMissing;
        }

        private void ApplyBaseLayout()
        {
            if (_notifyIcon.ContextMenuStrip != null)
            {
                _notifyIcon.ContextMenuStrip.Close();
            }
            Application.DoEvents();

            try
            {
                if (_controller.ApplyBaseLayout())
                {
                    ShowNotification(UIText.Balloon.BaseAppliedTitle, UIText.Balloon.BaseAppliedBody);
                }
                else
                {
                    MessageBox.Show(
                    UIText.Dialog.ApplyBaseConflict,
                    UIText.Dialog.TitleApplyBase,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(UIText.Dialog.ApplyBaseFailedFormat, ex.Message),
                    UIText.Dialog.TitleApplyBase,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ToggleLock()
        {

            if (_controller.IsLocked)
            {
                _controller.Unlock();
                RefreshLockPresentation();
                ShowNotification(UIText.Balloon.LockStateTitle, UIText.Balloon.UnlockedBody);
                return;
            }

            try
            {
                DesktopLayoutChangeResult change = _controller.InspectUnlockedLayoutChange();
                if (!change.HasChanged)
                {
                    _controller.RestoreSavedLayoutAndLock("锁定前检测到布局未发生变化，沿用原布局恢复决策链");
                    RefreshLockPresentation();
                    ShowNotification(UIText.Balloon.LockStateTitle, UIText.Balloon.NoChangeBody);
                    return;
                }

                DesktopProfile profile = _controller.SaveCurrentLayoutAndLock();
                RefreshBaseLayoutMenu();
                RefreshLockPresentation();
                ShowNotification(
                    UIText.Balloon.SavedAndLockedTitle,
                    string.Format(
                    UIText.Balloon.SavedAndLockedBodyFormat,
                    profile.Resolution,
                    profile.ScalePercent));
            }
            catch (Exception ex)
            {
                RefreshLockPresentation();
                MessageBox.Show(
                        string.Format(UIText.Dialog.LockFailedFormat, ex.Message),
                        UIText.Dialog.TitleLockLayout,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
            }
        }

        private void RefreshLockPresentation()
        {
            _notifyIcon.Icon = GetAppIcon(_controller.IsLocked);
            _notifyIcon.Text = _controller.IsLocked ? UIText.App.TooltipLocked : UIText.App.TooltipUnlocked;
        }

        private void ShowNotification(string title, string message)
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }

        private void CheckFirstRunGuide()
        {
            if (_controller.Store.CurrentConfig.BaseProfile == null)
            {
                try
                {
                    _controller.SaveCurrentLayout();
                    _notifyIcon.ShowBalloonTip(6000, UIText.Balloon.FirstRunTitle,
                        UIText.Balloon.FirstRunBody, ToolTipIcon.Info);
                }
                catch
                {
                }

                RefreshBaseLayoutMenu();
            }
        }

        private void ShowAboutDialog()
        {

            int formWidth = 420;

            using (Form aboutForm = new Form())
            {
                aboutForm.Text = UIText.About.WindowTitle;
                aboutForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                aboutForm.MaximizeBox = false;
                aboutForm.MinimizeBox = false;
                aboutForm.ShowInTaskbar = false;
                aboutForm.StartPosition = FormStartPosition.CenterScreen;
                aboutForm.ClientSize = new Size(formWidth, 150);
                aboutForm.BackColor = Color.White;
                aboutForm.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

                Label titleLabel = new Label
                {
                    Text = UIText.About.Heading,
                    Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(33, 33, 33),
                    Location = new Point(20, 16),
                    AutoSize = true
                };
                aboutForm.Controls.Add(titleLabel);

                Label infoLabel = new Label
                {
                    Text = string.Format(UIText.About.InfoBodyFormat, GetBuildDateText()),
                    ForeColor = Color.FromArgb(66, 66, 66),
                    Location = new Point(20, 48),
                    AutoSize = true
                };
                aboutForm.Controls.Add(infoLabel);

                int buttonY = infoLabel.Bottom + 18;
                aboutForm.ClientSize = new Size(formWidth, buttonY + 44);

                Button okButton = new Button
                {
                    Text = UIText.About.ButtonOk,
                    DialogResult = DialogResult.OK,
                    Size = new Size(88, 30),
                    Location = new Point(formWidth - 20 - 88, buttonY),
                    BackColor = Color.FromArgb(0, 122, 204),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                okButton.FlatAppearance.BorderSize = 0;
                aboutForm.AcceptButton = okButton;
                aboutForm.Controls.Add(okButton);

                aboutForm.ShowDialog();
            }
        }

        private static string GetBuildDateText()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (System.IO.File.Exists(exePath))
                {
                    return System.IO.File.GetLastWriteTime(exePath)
                        .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch
            {

            }

            return UIText.About.BuildDateUnknown;
        }

        private void ExitApplication()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Icon = null;
            _controller.Dispose();
            _notifyIcon.Dispose();
            DisposeIcon(ref _lockedIcon);
            DisposeIcon(ref _unlockedIcon);
            _messageWindow.DestroyHandle();
            Application.Exit();
        }

        private Icon GetAppIcon(bool isLocked)
        {
            if (isLocked)
            {
                if (_lockedIcon == null) _lockedIcon = LoadAppIcon(LockedIconResource, true);
                return _lockedIcon;
            }

            if (_unlockedIcon == null) _unlockedIcon = LoadAppIcon(UnlockedIconResource, false);
            return _unlockedIcon;
        }

        private static Icon LoadAppIcon(string resourceName, bool isLocked)
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (System.IO.Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        return new Icon(stream);
                    }
                }
            }
            catch
            {

            }

            return CreateAppIcon(isLocked);
        }

        private static void DisposeIcon(ref Icon icon)
        {
            Icon local = icon;
            icon = null;
            if (local != null) local.Dispose();
        }

        private static Icon CreateAppIcon(bool isLocked)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color baseColor = isLocked ? Color.FromArgb(211, 47, 47) : Color.FromArgb(0, 150, 80);
                using (SolidBrush brush = new SolidBrush(baseColor))
                {
                    g.FillEllipse(brush, 2, 2, 28, 28);
                }

                using (Pen pen = new Pen(Color.White, 2.5f))
                {
                    if (isLocked)
                    {
                        g.DrawArc(pen, 10, 7, 12, 12, 180, 180);
                        g.FillRectangle(Brushes.White, 9, 13, 14, 11);
                        using (SolidBrush dotBrush = new SolidBrush(baseColor))
                        {
                            g.FillEllipse(dotBrush, 14, 16, 4, 4);
                        }
                    }
                    else
                    {
                        g.DrawArc(pen, 6, 6, 12, 12, 210, 180);
                        g.FillRectangle(Brushes.White, 9, 13, 14, 11);
                        using (SolidBrush dotBrush = new SolidBrush(baseColor))
                        {
                            g.FillEllipse(dotBrush, 14, 16, 4, 4);
                        }
                    }
                }

                return CreateIconFromBitmap(bmp);
            }
        }

        private static Icon CreateIconFromBitmap(Bitmap bmp)
        {
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using (Icon temp = Icon.FromHandle(hIcon))
                {
                    return (Icon)temp.Clone();
                }
            }
            finally
            {
                User32.DestroyIcon(hIcon);
            }
        }

        private class MessageWindow : NativeWindow
        {
            private readonly LockController _controller;

            public MessageWindow(LockController controller)
            {
                _controller = controller;
                CreateParams cp = new CreateParams();
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == User32.WM_APP_REHOOK_LOCATION)
                {

                    _controller.RegisterDesktopLocationHook();
                }
                else if (m.Msg == User32.WM_DISPLAYCHANGE)
                {
                    _controller.OnDisplayChanged("WM_DISPLAYCHANGE");
                }
                else if (m.Msg == User32.WM_DPICHANGED)
                {
                    _controller.OnDisplayChanged("WM_DPICHANGED");
                }
                else if (m.Msg == User32.WM_SETTINGCHANGE)
                {
                    if ((uint)m.WParam.ToInt64() == User32.SPI_SETWORKAREA)
                    {
                        _controller.OnDisplayChanged("WM_SETTINGCHANGE/SPI_SETWORKAREA");
                    }
                }

                base.WndProc(ref m);
            }
        }
    }
}
