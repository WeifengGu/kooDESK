using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DesktopIconLock.Common;

namespace DesktopIconLock.Core
{
    public static class ScreenCaptureService
    {
        public static void CaptureFullDesktop(string outputPath, string traceId)
        {
            AuditLogger.LogBusinessEntry(
                "保存历史布局截图",
                string.Format("输出路径={0}", outputPath),
                "捕获当前用户完整虚拟桌面的物理像素截图",
                traceId);

            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Rectangle bounds = SystemInformation.VirtualScreen;
            AuditLogger.LogParamValidation(
                "保存历史布局截图",
                "VirtualScreen",
                "SystemInformation.VirtualScreen",
                string.Format("{0},{1},{2}x{3}", bounds.Left, bounds.Top, bounds.Width, bounds.Height),
                "宽高必须大于0",
                bounds.Width > 0 && bounds.Height > 0,
                "虚拟桌面尺寸无效",
                "无法生成历史布局截图",
                traceId);

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new InvalidOperationException("虚拟桌面尺寸无效，无法截图。");
            }

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
        }
    }
}
