using System;
using System.Threading;
using System.Windows.Forms;
using DesktopIconLock.Common;
using DesktopIconLock.Core;
using DesktopIconLock.Native;
using DesktopIconLock.Tray;

namespace DesktopIconLock
{
static class Program
{
private static Mutex _singleInstanceMutex;

[STAThread]
static void Main(string[] args)
{
// 必须在任何WinForms对象、Screen枚举或消息窗口创建前启用。
// 解决125%缩放下1920x1080被误识别成1536x864等DPI虚拟化问题。
DisplayInfo.EnablePerMonitorDpiAwareness();

if (args != null && args.Length > 0)
{
string startupError;
if (string.Equals(args[0], "--startup-enable", StringComparison.OrdinalIgnoreCase))
{
Environment.ExitCode = StartupManager.SetEnabled(true, out startupError) ? 0 : 1;
return;
}
if (string.Equals(args[0], "--startup-disable", StringComparison.OrdinalIgnoreCase))
{
Environment.ExitCode = StartupManager.SetEnabled(false, out startupError) ? 0 : 1;
return;
}
if (string.Equals(args[0], "--startup-status", StringComparison.OrdinalIgnoreCase))
{
Environment.ExitCode = StartupManager.IsEnabled() ? 0 : 1;
return;
}
if (string.Equals(args[0], "--save-layout", StringComparison.OrdinalIgnoreCase) ||
string.Equals(args[0], "--save-layout-as-base", StringComparison.OrdinalIgnoreCase))
{
LayoutStore commandStore = new LayoutStore();
using (LockController commandController = new LockController(commandStore))
{
HistoryLayoutRecord record = commandController.SaveCurrentAsExact();
if (string.Equals(args[0], "--save-layout-as-base", StringComparison.OrdinalIgnoreCase))
{
commandController.SetHistoryRecordAsBase(record);
}
}
Environment.ExitCode = 0;
return;
}
if (string.Equals(args[0], "--delete-history-record", StringComparison.OrdinalIgnoreCase) &&
args.Length > 1)
{
LayoutStore commandStore = new LayoutStore();
HistoryStore commandHistory = new HistoryStore(commandStore.ConfigDirectory);
System.Collections.Generic.List<HistoryLayoutRecord> commandRecords = commandHistory.GetRecords();
HistoryLayoutRecord targetRecord = commandRecords.Find(
delegate(HistoryLayoutRecord item)
{
return string.Equals(item.Id, args[1], StringComparison.OrdinalIgnoreCase);
});
bool deletedBase;
bool deleted = commandHistory.DeleteRecord(targetRecord, out deletedBase);
if (deleted && deletedBase)
{
commandStore.ClearBaseProfile();
}
Environment.ExitCode = deleted ? 0 : 1;
return;
}
}

bool isNewInstance;
_singleInstanceMutex = new Mutex(true, "DesktopIconLock_SingleInstance_Mutex_999", out isNewInstance);
if (!isNewInstance)
{
MessageBox.Show("桌面图标位置锁定工具已在后台运行中，请查看任务栏右下角托盘图标。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
return;
}

string traceId = AuditLogger.GenerateTraceId();
AuditLogger.LogBusinessEntry("应用程序主入口", "Program.Main", "启动桌面图标位置锁定服务进程", traceId);

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
{
Exception ex = e.ExceptionObject as Exception;
AuditLogger.LogException("AppDomain.UnhandledException", ex != null ? ex.GetType().Name : "Unknown", 
ex != null ? ex.Message : "未知未捕获异常", "程序崩溃", false, ex, AuditLogger.CurrentTraceId);
};

Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
{
AuditLogger.LogException("Application.ThreadException", e.Exception.GetType().Name, 
e.Exception.Message, "UI线程异常已拦截", true, e.Exception, AuditLogger.CurrentTraceId);
};

try
{
LayoutStore store = new LayoutStore();
LockController controller = new LockController(store);
controller.Start();

AuditLogger.Info("托盘服务初始化", "进入Application.Run消息循环，无任何前台窗口", traceId);
Application.Run(new TrayContext(controller));
}
catch (Exception ex)
{
AuditLogger.LogException("MainLoop", "FatalStartupException", ex.Message, "进程退出", false, ex, traceId);
MessageBox.Show(string.Format("启动失败: {0}", ex.Message), "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
finally
{
if (_singleInstanceMutex != null)
{
_singleInstanceMutex.ReleaseMutex();
}
}
}
}
}
