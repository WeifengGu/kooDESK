using System;
using System.Runtime.InteropServices;

namespace DesktopIconLock.Native
{
public static class User32
{
public const int WM_DISPLAYCHANGE = 0x007E;
public const int WM_DEVICECHANGE = 0x0219;
public const int WM_DPICHANGED = 0x02E0;
public const int WM_SETTINGCHANGE = 0x001A;

public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
public const uint WINEVENT_OUTOFCONTEXT = 0;
public const uint WINEVENT_SKIPOWNPROCESS = 2;
public const int OBJID_WINDOW = 0;
public const int OBJID_CLIENT = -4;
public const int OBJID_CURSOR = -9;
public const int VK_LBUTTON = 0x01;

public const uint SPI_GETWORKAREA = 0x0030;
public const uint SPI_SETWORKAREA = 0x002F;

public const uint LVM_FIRST = 0x1000;
public const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
public const uint LVM_GETITEMPOSITION = LVM_FIRST + 16;
public const uint LVM_SETITEMPOSITION = LVM_FIRST + 15;
public const uint LVM_GETITEMSPACING = LVM_FIRST + 51;
public const uint LVM_GETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 55;
public const uint LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;

public const uint LVS_AUTOARRANGE = 0x0100;
public const uint LVS_EX_SNAPTOGRID = 0x00080000;
public const uint LVS_EX_AUTOARRANGE = 0x00000100;
public const int GWL_STYLE = -16;

public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

[DllImport("user32.dll")]
public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

[DllImport("user32.dll")]
public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

[DllImport("user32.dll", CharSet = CharSet.Auto)]
public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

[DllImport("user32.dll", CharSet = CharSet.Auto)]
public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

[DllImport("user32.dll", CharSet = CharSet.Auto)]
public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

[DllImport("user32.dll", SetLastError = true)]
public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

[DllImport("user32.dll")]
public static extern IntPtr GetDesktopWindow();

[DllImport("user32.dll")]
public static extern IntPtr GetShellWindow();

[DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

[DllImport("user32.dll", EntryPoint = "GetWindowLong")]
public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

[DllImport("user32.dll", EntryPoint = "SetWindowLong")]
public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

[DllImport("user32.dll")]
private static extern short GetAsyncKeyState(int vKey);

public static bool IsLeftMouseButtonDown()
{
return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
public int Left;
public int Top;
public int Right;
public int Bottom;

public int Width { get { return Right - Left; } }
public int Height { get { return Bottom - Top; } }

public override string ToString()
{
return string.Format("[{0}, {1}, {2}, {3}] ({4}x{5})", Left, Top, Right, Bottom, Width, Height);
}
}

public static IntPtr GetDesktopListViewHandle()
{
IntPtr hProgman = FindWindow("Progman", "Program Manager");
IntPtr hDesktopList = IntPtr.Zero;

if (hProgman != IntPtr.Zero)
{
IntPtr hShelldll = FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);
if (hShelldll != IntPtr.Zero)
{
hDesktopList = FindWindowEx(hShelldll, IntPtr.Zero, "SysListView32", "FolderView");
if (hDesktopList != IntPtr.Zero)
{
return hDesktopList;
}
}
}

EnumWindows(delegate(IntPtr topHandle, IntPtr lParam)
{
IntPtr shellDll = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
if (shellDll != IntPtr.Zero)
{
IntPtr folderView = FindWindowEx(shellDll, IntPtr.Zero, "SysListView32", "FolderView");
if (folderView != IntPtr.Zero)
{
hDesktopList = folderView;
return false;
}
}
return true;
}, IntPtr.Zero);

return hDesktopList;
}
}
}
