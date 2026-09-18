using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace KooDesk.Native
{
    public class MonitorProfileInfo
    {
        public string DeviceName { get; set; }
        public string MonitorFingerprint { get; set; }
        public bool IsRemoteSession { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Dpi { get; set; }
        public User32.RECT Bounds { get; set; }
        public User32.RECT WorkArea { get; set; }
        public bool IsPrimary { get; set; }

        public string ResolutionKey { get { return string.Format("{0}x{1}", Width, Height); } }
        public int ScalePercent { get { return (int)Math.Round(Dpi * 100.0 / 96.0); } }

        public override string ToString()
        {
            return string.Format(
                "{0} [物理分辨率={1}, DPI={2}({3}%), 工作区={4}x{5}, 主屏={6}, 会话={7}, 指纹={8}]",
                DeviceName,
                ResolutionKey,
                Dpi,
                ScalePercent,
                WorkArea.Width,
                WorkArea.Height,
                IsPrimary,
                IsRemoteSession ? "RDP" : "本地",
                MonitorFingerprint);
        }
    }

    public static class DisplayInfo
    {
        private const int MDT_EFFECTIVE_DPI = 0;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int ENUM_CURRENT_SETTINGS = -1;
        private static bool _dpiAwarenessAttempted;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        public static void EnablePerMonitorDpiAwareness()
        {
            if (_dpiAwarenessAttempted) return;
            _dpiAwarenessAttempted = true;

            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch
            {

            }
        }

        public static List<MonitorProfileInfo> GetAllMonitors()
        {
            List<MonitorProfileInfo> list = new List<MonitorProfileInfo>();
            bool isRemoteSession = IsRemoteDesktopSession();

            try
            {
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    Screen screen = screens[i];
                    int physicalWidth = screen.Bounds.Width;
                    int physicalHeight = screen.Bounds.Height;
                    TryGetPhysicalResolution(screen.DeviceName, ref physicalWidth, ref physicalHeight);

                    MonitorProfileInfo info = new MonitorProfileInfo();
                    info.DeviceName = screen.DeviceName;
                    info.IsRemoteSession = isRemoteSession;
                    info.MonitorFingerprint = isRemoteSession
                        ? GenerateHash("RDP_VIRTUAL_DISPLAY")
                        : GenerateMonitorFingerprint(screen.DeviceName);
                    info.Width = physicalWidth;
                    info.Height = physicalHeight;
                    info.Dpi = GetScreenDpi(screen);
                    info.IsPrimary = screen.Primary;

                    User32.RECT bounds = new User32.RECT();
                    bounds.Left = screen.Bounds.Left;
                    bounds.Top = screen.Bounds.Top;
                    bounds.Right = screen.Bounds.Left + physicalWidth;
                    bounds.Bottom = screen.Bounds.Top + physicalHeight;
                    info.Bounds = bounds;

                    User32.RECT workArea = new User32.RECT();
                    workArea.Left = screen.WorkingArea.Left;
                    workArea.Top = screen.WorkingArea.Top;
                    workArea.Right = screen.WorkingArea.Right;
                    workArea.Bottom = screen.WorkingArea.Bottom;
                    info.WorkArea = workArea;

                    list.Add(info);
                }
            }
            catch
            {

            }

            if (list.Count == 0)
            {
                MonitorProfileInfo fallback = new MonitorProfileInfo();
                fallback.DeviceName = @"\\.\DISPLAY1";
                fallback.IsRemoteSession = isRemoteSession;
                fallback.MonitorFingerprint = isRemoteSession
                    ? GenerateHash("RDP_VIRTUAL_DISPLAY")
                    : GenerateHash("DEFAULT_MONITOR");
                fallback.Width = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.Bounds.Width : 1920;
                fallback.Height = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.Bounds.Height : 1080;
                fallback.Dpi = SafeGetSystemDpi();
                fallback.IsPrimary = true;
                fallback.Bounds = new User32.RECT { Left = 0, Top = 0, Right = fallback.Width, Bottom = fallback.Height };
                fallback.WorkArea = fallback.Bounds;
                list.Add(fallback);
            }

            return list;
        }

        private static bool IsRemoteDesktopSession()
        {
            try
            {

                return SystemInformation.TerminalServerSession ||
                    GetSystemMetrics(0x1000) != 0;
            }
            catch
            {
                return false;
            }
        }

        public static MonitorProfileInfo GetPrimaryMonitor()
        {
            List<MonitorProfileInfo> monitors = GetAllMonitors();
            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].IsPrimary) return monitors[i];
            }
            return monitors[0];
        }

        private static void TryGetPhysicalResolution(string deviceName, ref int width, ref int height)
        {
            DEVMODE mode = new DEVMODE();
            mode.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode) &&
                mode.dmPelsWidth > 0 &&
                mode.dmPelsHeight > 0)
            {
                width = (int)mode.dmPelsWidth;
                height = (int)mode.dmPelsHeight;
            }
        }

        private static int GetScreenDpi(Screen screen)
        {
            try
            {
                POINT point = new POINT();
                point.X = screen.Bounds.Left + Math.Max(1, screen.Bounds.Width / 2);
                point.Y = screen.Bounds.Top + Math.Max(1, screen.Bounds.Height / 2);
                IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
                uint dpiX;
                uint dpiY;
                if (monitor != IntPtr.Zero &&
                    GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0 &&
                    dpiX >= 96)
                {
                    return (int)dpiX;
                }
            }
            catch { }

            return SafeGetSystemDpi();
        }

        private static int SafeGetSystemDpi()
        {
            try
            {
                uint dpi = GetDpiForSystem();
                return dpi >= 96 ? (int)dpi : 96;
            }
            catch
            {
                return 96;
            }
        }

        private static string GenerateMonitorFingerprint(string displayDeviceName)
        {
            try
            {
                DISPLAY_DEVICE monitor = new DISPLAY_DEVICE();
                monitor.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (EnumDisplayDevices(displayDeviceName, 0, ref monitor, 0))
                {
                    string stableIdentity = string.Format(
                        "{0}|{1}|{2}",
                        displayDeviceName ?? "",
                        monitor.DeviceID ?? "",
                        monitor.DeviceString ?? "");
                    return GenerateHash(stableIdentity);
                }
            }
            catch { }

            return GenerateHash(displayDeviceName ?? "UNKNOWN_DISPLAY");
        }

        private static string GenerateHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder sb = new StringBuilder("MON_");
                for (int i = 0; i < 8; i++)
                {
                    sb.Append(hash[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }
    }
}
