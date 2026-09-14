using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DesktopIconLock.Common;
using DesktopIconLock.Core;
using DesktopIconLock.Native;

namespace DesktopIconLock.Tray
{
public class TrayContext : ApplicationContext
{
private readonly NotifyIcon _notifyIcon;
private readonly LockController _controller;
private readonly MessageWindow _messageWindow;
private readonly HistoryPreviewForm _historyPreview;
private ToolStripMenuItem _menuLockStatus;
private ToolStripMenuItem _menuStartup;
private ToolStripMenuItem _menuHistory;

public TrayContext(LockController controller)
{
_controller = controller;
_messageWindow = new MessageWindow(_controller);
_historyPreview = new HistoryPreviewForm();
_historyPreview.DeleteRequested += OnDeleteHistoryRequested;
_historyPreview.BaseRequested += OnSetHistoryBaseRequested;
_historyPreview.ApplyRequested += OnApplyHistoryRequested;

_notifyIcon = new NotifyIcon();
_notifyIcon.Icon = CreateAppIcon(_controller.IsLocked);
_notifyIcon.Text = "桌面图标位置锁定工具 (已开启锁定)";
_notifyIcon.Visible = true;

BuildContextMenu();

_notifyIcon.DoubleClick += delegate(object s, EventArgs e) { ToggleLock(); };

CheckFirstRunGuide();
}

private void BuildContextMenu()
{
ContextMenuStrip contextMenu = new ContextMenuStrip();

_menuLockStatus = new ToolStripMenuItem(_controller.IsLocked ? "图标位置已锁定（点击解锁）" : "图标位置已解锁（点击锁定）");
_menuLockStatus.Font = new Font(_menuLockStatus.Font, FontStyle.Bold);
_menuLockStatus.Click += delegate(object s, EventArgs e) { ToggleLock(); };
contextMenu.Items.Add(_menuLockStatus);

contextMenu.Items.Add(new ToolStripSeparator());

ToolStripMenuItem menuSaveExact = new ToolStripMenuItem("保存当前布局");
menuSaveExact.Click += delegate(object s, EventArgs e)
{
try
{
contextMenu.Close();
Application.DoEvents();
HistoryLayoutRecord record = _controller.SaveCurrentAsExact();
RefreshHistoryMenu();
ShowNotification(
"布局保存完成",
string.Format(
"已保存当前布局和全桌面截图。\n分辨率：{0}\n缩放：{1}%",
record.Profile.Resolution,
record.ScalePercent));
}
catch (Exception ex)
{
AuditLogger.LogException(
"托盘保存当前布局",
ex.GetType().Name,
ex.Message,
"当前布局或历史截图未完整保存",
true,
ex,
AuditLogger.CurrentTraceId);
MessageBox.Show(
"保存布局失败：" + ex.Message,
"保存当前布局",
MessageBoxButtons.OK,
MessageBoxIcon.Error);
}
};
contextMenu.Items.Add(menuSaveExact);

_menuHistory = new ToolStripMenuItem("历史布局");
_menuHistory.Click += delegate(object s, EventArgs e)
{
    OpenHistoryOverviewWindow();
};
contextMenu.Items.Add(_menuHistory);

contextMenu.Items.Add(new ToolStripSeparator());

ToolStripMenuItem menuAbout = new ToolStripMenuItem("关于");
menuAbout.Click += delegate(object s, EventArgs e) { ShowAboutDialog(); };
contextMenu.Items.Add(menuAbout);

ToolStripMenuItem menuOpenLogs = new ToolStripMenuItem("日志目录");
menuOpenLogs.Click += delegate(object s, EventArgs e)
{
string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
System.Diagnostics.Process.Start("explorer.exe", logDir);
};
contextMenu.Items.Add(menuOpenLogs);

contextMenu.Items.Add(new ToolStripSeparator());

_menuStartup = new ToolStripMenuItem("开机启动");
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
"开机启动设置",
targetState
? "已注册为当前用户开机启动项。"
: "已关闭当前用户开机启动。");
}
else
{
MessageBox.Show(
"修改开机启动失败：" + (errorMessage ?? "未知错误"),
"开机启动设置",
MessageBoxButtons.OK,
MessageBoxIcon.Error);
}
};
contextMenu.Items.Add(_menuStartup);

contextMenu.Items.Add(new ToolStripSeparator());

ToolStripMenuItem menuExit = new ToolStripMenuItem("退出程序");
menuExit.Click += delegate(object s, EventArgs e) { ExitApplication(); };
contextMenu.Items.Add(menuExit);

_notifyIcon.ContextMenuStrip = contextMenu;
RefreshHistoryMenu();
}

private void OpenHistoryOverviewWindow()
{
    System.Collections.Generic.List<HistoryLayoutRecord> records = _controller.GetHistoryRecords();
    _historyPreview.ShowOverview(records);
}

private void RefreshHistoryMenu()
{
    if (_historyPreview != null && _historyPreview.Visible)
    {
        System.Collections.Generic.List<HistoryLayoutRecord> records = _controller.GetHistoryRecords();
        _historyPreview.RefreshList(records);
    }
}

private void OnDeleteHistoryRequested(HistoryLayoutRecord record)
{
    DialogResult result = MessageBox.Show(
        "确定删除这条历史布局及其全桌面截图吗？\n\n" + record.MenuText,
        "删除历史布局",
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning,
        MessageBoxDefaultButton.Button2);
    if (result != DialogResult.Yes) return;

    if (_controller.DeleteHistoryRecord(record))
    {
        RefreshHistoryMenu();
        ShowNotification("历史布局已删除", record.MenuText);
    }
    else
    {
        MessageBox.Show(
            "历史布局删除失败，请打开日志目录查看原因。",
            "删除历史布局",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}

private void OnSetHistoryBaseRequested(HistoryLayoutRecord record)
{
    _controller.SetHistoryRecordAsBase(record);
    RefreshHistoryMenu();
    ShowNotification("基准布局已更新", record.MenuText);
}

private void OnApplyHistoryRequested(HistoryLayoutRecord record)
{
    _controller.ApplyHistoryRecord(record);
    ShowNotification("历史布局已应用", record.MenuText);
}

private void ToggleLock()
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"TrayContext.ToggleLock",
"托盘菜单或托盘图标双击",
string.Format("当前锁定状态={0}", _controller.IsLocked),
"处理用户的锁定状态切换请求",
traceId);

if (_controller.IsLocked)
{
_controller.Unlock();
RefreshLockPresentation();
ShowNotification("锁定状态变更", "已临时解锁，可自由移动图标。再次锁定时将检查布局是否发生变化。");
AuditLogger.LogResponseReturn(
"TrayContext.ToggleLock",
200,
"已从锁定切换为解锁",
0,
"成功",
"桌面位置未被移动，已记录解锁比较快照",
traceId);
return;
}

try
{
DesktopLayoutChangeResult change = _controller.InspectUnlockedLayoutChange();
if (!change.HasChanged)
{
_controller.RestoreSavedLayoutAndLock("锁定前检测到布局未发生变化，沿用原布局恢复决策链");
RefreshLockPresentation();
ShowNotification("锁定状态变更", "桌面布局未发生变动，已开启桌面图标锁定保护。");
AuditLogger.LogResponseReturn(
"TrayContext.ToggleLock",
200,
change.Summary,
0,
"成功",
"布局未变化，已直接恢复并锁定",
traceId);
return;
}

if (_notifyIcon.ContextMenuStrip != null)
{
_notifyIcon.ContextMenuStrip.Close();
}
Application.DoEvents();

LayoutChangePromptChoice choice;
using (LayoutChangePromptForm prompt = new LayoutChangePromptForm())
{
prompt.ShowDialog();
choice = prompt.Choice;
}

AuditLogger.LogRuleDecision(
"桌面布局变动提示框选择",
change.Summary,
choice.ToString(),
"用户明确选择锁定前如何处理当前布局",
traceId);

if (choice == LayoutChangePromptChoice.Cancel)
{
RefreshLockPresentation();
AuditLogger.LogRejected(
"锁定请求用户确认",
"用户取消锁定",
499,
"保持解锁状态，不保存、不恢复、不移动任何图标",
"用户可以继续调整图标并稍后再次锁定",
traceId);
AuditLogger.LogResponseReturn(
"TrayContext.ToggleLock",
499,
"用户取消锁定",
0,
"取消",
"锁定状态仍为false，桌面布局保持不变",
traceId);
return;
}

if (choice == LayoutChangePromptChoice.RestoreSaved)
{
_controller.RestoreSavedLayoutAndLock("用户选择恢复已保存布局并锁定");
RefreshLockPresentation();
ShowNotification("锁定状态变更", "已按现有匹配规则恢复保存的布局并开启锁定保护。");
AuditLogger.LogResponseReturn(
"TrayContext.ToggleLock",
200,
change.Summary,
0,
"成功",
"已放弃当前调整，恢复保存布局并锁定",
traceId);
return;
}

HistoryLayoutRecord record = _controller.SaveCurrentLayoutAndLock();
RefreshHistoryMenu();
RefreshLockPresentation();
ShowNotification(
"布局保存并锁定",
string.Format(
"已保存并锁定当前布局。\n分辨率：{0}\n缩放：{1}%",
record.Profile.Resolution,
record.ScalePercent));
AuditLogger.LogResponseReturn(
"TrayContext.ToggleLock",
200,
string.Format("已保存历史记录={0}", record.Id),
0,
"成功",
"当前图标位置已成为新的锁定目标",
traceId);
}
catch (Exception ex)
{
RefreshLockPresentation();
AuditLogger.LogException(
"TrayContext.ToggleLock",
ex.GetType().Name,
ex.Message,
"锁定请求未完成，继续保持解锁状态和当前桌面位置",
true,
ex,
traceId);
MessageBox.Show(
"锁定操作失败：" + ex.Message + "\n\n当前仍保持解锁状态，桌面图标位置未被主动恢复。",
"锁定桌面布局",
MessageBoxButtons.OK,
MessageBoxIcon.Error);
AuditLogger.LogResponseReturn(
"TrayContext.ToggleLock",
500,
"锁定操作失败",
0,
"失败",
"继续保持解锁状态，用户可检查日志后重试",
traceId);
}
}

private void RefreshLockPresentation()
{
_menuLockStatus.Text = _controller.IsLocked ? "图标位置已锁定（点击解锁）" : "图标位置已解锁（点击锁定）";
_notifyIcon.Icon = CreateAppIcon(_controller.IsLocked);
_notifyIcon.Text = _controller.IsLocked ? "桌面图标位置锁定工具 (已开启锁定)" : "桌面图标位置锁定工具 (已暂停锁定)";
}

private void ShowNotification(string title, string message)
{
_notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
}

private void CheckFirstRunGuide()
{
if (_controller.Store.CurrentConfig.BaseProfile == null)
{
_controller.SaveCurrentAsBase();
_notifyIcon.ShowBalloonTip(6000, "桌面图标位置锁定工具已启动", 
"已自动保存当前桌面为基准布局。\n提示：请右键桌面确认【查看->自动排列图标】已关闭，以确保位置精确锁定！", ToolTipIcon.Info);
}
}

private void ShowAboutDialog()
{
using (Form aboutForm = new Form())
{
aboutForm.Text = "关于 DesktopIconLock";
aboutForm.FormBorderStyle = FormBorderStyle.FixedDialog;
aboutForm.MaximizeBox = false;
aboutForm.MinimizeBox = false;
aboutForm.ShowInTaskbar = false;
aboutForm.StartPosition = FormStartPosition.CenterScreen;
aboutForm.ClientSize = new Size(720, 390);
aboutForm.BackColor = Color.White;
aboutForm.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

Label titleLabel = new Label
{
Text = "桌面图标位置锁定工具 (DesktopIconLock)",
Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
ForeColor = Color.FromArgb(33, 33, 33),
Location = new Point(20, 16),
AutoSize = true
};
aboutForm.Controls.Add(titleLabel);

Label descLabel = new Label
{
Text = "轻量级 Windows 桌面图标锁定与多分辨率自适应工具。\n" +
       "直接读写桌面原生坐标，无前台窗口、无模拟层、零额外遮罩。\n\n" +
       "主要功能：\n" +
       "• 锁定防拖动：锁定状态下移动图标会自动纠正回保存位置\n" +
       "• 多模式适配：支持 1K/2K/4K/RDP 远程桌面精确布局与拓扑自适应\n" +
       "• 历史管理：带桌面缩略图预览，支持一键切换历史布局",
ForeColor = Color.FromArgb(66, 66, 66),
Location = new Point(20, 48),
AutoSize = true,
MaximumSize = new Size(680, 0)
};
aboutForm.Controls.Add(descLabel);

int linkStartY = descLabel.Bottom + 16;

LinkLabel githubLink = new LinkLabel
{
Text = "开源项目：https://github.com/CangShui/DesktopIconLock",
Location = new Point(20, linkStartY),
AutoSize = true,
MaximumSize = new Size(680, 0),
LinkColor = Color.FromArgb(0, 102, 204),
ActiveLinkColor = Color.FromArgb(0, 70, 150)
};
githubLink.LinkClicked += delegate
{
try { System.Diagnostics.Process.Start("https://github.com/CangShui/DesktopIconLock"); } catch { }
};
aboutForm.Controls.Add(githubLink);

LinkLabel blogLink = new LinkLabel
{
Text = "作者博客：https://cangshui.net",
Location = new Point(20, linkStartY + 28),
AutoSize = true,
LinkColor = Color.FromArgb(0, 102, 204),
ActiveLinkColor = Color.FromArgb(0, 70, 150)
};
blogLink.LinkClicked += delegate
{
try { System.Diagnostics.Process.Start("https://cangshui.net"); } catch { }
};
aboutForm.Controls.Add(blogLink);

int buttonY = blogLink.Bottom + 18;
aboutForm.ClientSize = new Size(720, buttonY + 44);

Button okButton = new Button
{
Text = "确定",
DialogResult = DialogResult.OK,
Size = new Size(88, 30),
Location = new Point(720 - 20 - 88, buttonY),
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

private void ExitApplication()
{
AuditLogger.Info("程序退出", "用户触发托盘退出", AuditLogger.CurrentTraceId);
_notifyIcon.Visible = false;
_controller.Dispose();
_historyPreview.Dispose();
_messageWindow.DestroyHandle();
Application.Exit();
}

private static Icon CreateAppIcon(bool isLocked)
{
using (Bitmap bmp = new Bitmap(32, 32))
using (Graphics g = Graphics.FromImage(bmp))
{
g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
g.Clear(Color.Transparent);

Color baseColor = isLocked ? Color.FromArgb(0, 150, 136) : Color.FromArgb(239, 108, 0);
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

return Icon.FromHandle(bmp.GetHicon());
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
if (m.Msg == User32.WM_DISPLAYCHANGE)
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
else
{
AuditLogger.Debug(
"显示消息已忽略",
string.Format(
"收到WM_SETTINGCHANGE，但参数={0}不是SPI_SETWORKAREA；不启动图标恢复，避免无关设置导致桌面重绘",
m.WParam.ToInt64()),
AuditLogger.CurrentTraceId);
}
}
else if (m.Msg == User32.WM_DEVICECHANGE)
{
AuditLogger.Debug(
"显示消息已忽略",
"收到WM_DEVICECHANGE。设备枚举变化不代表桌面分辨率已稳定，等待WM_DISPLAYCHANGE或WM_DPICHANGED再恢复图标。",
AuditLogger.CurrentTraceId);
}

base.WndProc(ref m);
}
}
}
}
