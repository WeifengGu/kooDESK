using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DesktopIconLock.Common;

namespace DesktopIconLock.Core
{
    public static class ScreenCaptureService
    {
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;
        private const int WS_MINIMIZE = 0x20000000;
        private const int GWL_STYLE = -16;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static void CaptureFullDesktop(string outputPath, string traceId)
        {
            AuditLogger.LogBusinessEntry(
                "保存历史布局截图",
                string.Format("输出路径={0}", outputPath),
                "捕获纯净无遮挡的桌面物理像素截图",
                traceId);

            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Rectangle bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new InvalidOperationException("虚拟桌面尺寸无效，无法截图。");
            }

            // 临时最小化遮挡桌面的顶层应用程序窗口，实现“穿透上层软件窗口截取真实桌面”
            List<IntPtr> minimizedWindows = new List<IntPtr>();
            IntPtr shellWnd = GetShellWindow();
            IntPtr progman = FindWindow("Progman", "Program Manager");

            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (hWnd == shellWnd || hWnd == progman) return true;
                    if (!IsWindowVisible(hWnd)) return true;

                    StringBuilder sb = new StringBuilder(256);
                    GetClassName(hWnd, sb, 256);
                    string className = sb.ToString();

                    // 跳过桌面、壁纸层和系统托盘/任务栏
                    if (className == "Progman" ||
                        className == "WorkerW" ||
                        className == "Shell_TrayWnd" ||
                        className == "Shell_SecondaryTrayWnd")
                    {
                        return true;
                    }

                    int style = GetWindowLong(hWnd, GWL_STYLE);
                    if ((style & WS_MINIMIZE) != 0) return true;

                    ShowWindow(hWnd, SW_MINIMIZE);
                    minimizedWindows.Add(hWnd);
                    return true;
                }, IntPtr.Zero);

                // 稍微等待窗口收起动画完成
                if (minimizedWindows.Count > 0)
                {
                    Thread.Sleep(250);
                }
            }
            catch { }

            string temporaryPath = outputPath + ".tmp";
            try
            {
                using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        new Point(bounds.Left, bounds.Top),
                        Point.Empty,
                        bounds.Size,
                        CopyPixelOperation.SourceCopy);
                    bitmap.Save(temporaryPath, ImageFormat.Png);
                }

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
                File.Move(temporaryPath, outputPath);

                AuditLogger.LogStorageOperation(
                    "写入历史布局截图",
                    "PNG截图文件",
                    string.Format("路径={0}, 尺寸={1}x{2}", outputPath, bounds.Width, bounds.Height),
                    1,
                    "截图已保存",
                    traceId);
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch { }

                AuditLogger.LogException(
                    "保存历史布局截图",
                    ex.GetType().Name,
                    ex.Message,
                    "布局坐标仍可保存，但该条历史记录没有可用截图",
                    true,
                    ex,
                    traceId);
                throw;
            }
            finally
            {
                // 截图完成立即恢复刚才最小化的所有上层窗口
                if (minimizedWindows.Count > 0)
                {
                    for (int i = minimizedWindows.Count - 1; i >= 0; i--)
                    {
                        ShowWindow(minimizedWindows[i], SW_RESTORE);
                    }
                }
            }
        }
    }
}
