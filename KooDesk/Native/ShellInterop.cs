using System;
using System.Runtime.InteropServices;

namespace KooDesk.Native
{
    public static class ShellInterop
    {
        public static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        public static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        public static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
        public static readonly Guid IID_IFolderView2 = new Guid("1af3a467-214f-4214-af5a-75fb96bdf709");

        public const int CSIDL_DESKTOP = 0x0000;
        public const int SWC_DESKTOP = 0x00000008;
        public const int SWFO_NEEDDISPATCH = 0x00000001;

        public const uint SIGDN_NORMALDISPLAY = 0x00000000;
        public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
        public const uint SIGDN_DESKTOPABSOLUTEEDITING = 0x8004c000;

        [Flags]
        public enum SVGIO : uint
        {
            SVGIO_BACKGROUND = 0x00000000,
            SVGIO_SELECTION = 0x00000001,
            SVGIO_ALLVIEW = 0x00000002,
            SVGIO_CHECKED = 0x00000003,
            SVGIO_TYPE_MASK = 0x0000000F,
            SVGIO_FLAG_VIEWORDER = 0x80000000
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;

            public POINT(int x, int y)
            {
                this.x = x;
                this.y = y;
            }

            public override string ToString()
            {
                return string.Format("({0}, {1})", x, y);
            }
        }

        [ComImport]
        [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IShellWindows
        {
            [DispId(1610743808)]
            int Count { get; }

            [DispId(0)]
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object Item([In, MarshalAs(UnmanagedType.Struct)] object index);

            [DispId(-4)]
            [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(System.Runtime.InteropServices.CustomMarshalers.EnumeratorToEnumVariantMarshaler))]
            System.Collections.IEnumerator GetEnumerator();

            [DispId(1610743811)]
            void Register([In, MarshalAs(UnmanagedType.IDispatch)] object pid, [In] int HWND, [In] int swClass, out int plCookie);

            [DispId(1610743812)]
            void RegisterPending([In] int lThreadId, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarloc, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot, [In] int swClass, out int plCookie);

            [DispId(1610743813)]
            void Revoke([In] int lCookie);

            [DispId(1610743814)]
            void OnNavigate([In] int lCookie, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarloc);

            [DispId(1610743815)]
            void OnActivated([In] int lCookie, [In] bool fActive);

            [DispId(1610743816)]
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object pvarLoc, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot, [In] int swClass, out int phwnd, [In] int swflags);

            [DispId(1610743817)]
            void OnCreated([In] int lCookie, [In, MarshalAs(UnmanagedType.IUnknown)] object punk);

            [DispId(1610743818)]
            void ProcessAttachDetach([In] bool fAttach);
        }

        [ComImport]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IServiceProvider
        {
            [PreserveSig]
            int QueryService([In] ref Guid guidService, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
        }

        [ComImport]
        [Guid("000214E2-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellBrowser
        {
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp([In] bool fEnterMode);
            void InsertMenusSB([In] IntPtr hmenuShared, [In, Out] IntPtr lpMenuWidths);
            void SetMenuSB([In] IntPtr hmenuShared, [In] IntPtr holemenuRes, [In] IntPtr hwndActiveObject);
            void RemoveMenusSB([In] IntPtr hmenuShared);
            void SetStatusTextSB([In, MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
            void EnableModelessSB([In] bool fEnable);
            void TranslateAcceleratorSB([In] IntPtr pmsg, [In] ushort wID);
            void BrowseObject([In] IntPtr pidl, [In] uint wFlags);
            void GetViewStateStream([In] uint grfMode, [MarshalAs(UnmanagedType.Interface)] out object ppStrm);
            void GetControlWindow([In] uint id, out IntPtr phwnd);
            void SendControlMsg([In] uint id, [In] uint uMsg, [In] IntPtr wParam, [In] IntPtr lParam, out IntPtr pret);
            [PreserveSig]
            int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView ppshv);
            void OnViewWindowActive([In, MarshalAs(UnmanagedType.Interface)] IShellView ppshv);
            void SetToolbarItems([In] IntPtr lpButtons, [In] uint nButtons, [In] uint uFlags);
        }

        [ComImport]
        [Guid("000214E3-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellView
        {
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp([In] bool fEnterMode);
            void TranslateAcceleratorA([In] IntPtr pmsg);
            void EnableModeless([In] bool fEnable);
            void UIActivate([In] uint uState);
            void Refresh();
            void CreateViewWindow([In, MarshalAs(UnmanagedType.Interface)] IShellView psvPrevious, [In] IntPtr lpfs, [In, MarshalAs(UnmanagedType.Interface)] IShellBrowser psb, [In] IntPtr prcView, out IntPtr phWnd);
            void DestroyViewWindow();
            void GetCurrentInfo(out IntPtr lpfs);
            void AddPropertySheetPages([In] uint dwReserved, [In] IntPtr pfn, [In] IntPtr lparam);
            void SaveViewState();
            void SelectItem([In] IntPtr pidlItem, [In] uint uFlags);
            void GetItemObject([In] uint uItem, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        }

        [ComImport]
        [Guid("1af3a467-214f-4214-af5a-75fb96bdf709")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFolderView2
        {
            void GetWindow(out IntPtr phwnd);
            void ContextSensitiveHelp([In] bool fEnterMode);
            void TranslateAcceleratorA([In] IntPtr pmsg);
            void EnableModeless([In] bool fEnable);
            void UIActivate([In] uint uState);
            void Refresh();
            void CreateViewWindow([In, MarshalAs(UnmanagedType.Interface)] IShellView psvPrevious, [In] IntPtr lpfs, [In, MarshalAs(UnmanagedType.Interface)] IShellBrowser psb, [In] IntPtr prcView, out IntPtr phWnd);
            void DestroyViewWindow();
            void GetCurrentInfo(out IntPtr lpfs);
            void AddPropertySheetPages([In] uint dwReserved, [In] IntPtr pfn, [In] IntPtr lparam);
            void SaveViewState();
            void SelectItem([In] IntPtr pidlItem, [In] uint uFlags);
            void GetItemObject([In] uint uItem, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

            [PreserveSig]
            int GetCurrentFolder([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig]
            int GetFolder([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig]
            int ItemCount([In] SVGIO uFlags, out int pcItems);
            [PreserveSig]
            int Items([In] SVGIO uFlags, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig]
            int GetSelectionMarked([Out] out int piItem);
            [PreserveSig]
            int GetFocusedItem([Out] out int piItem);
            [PreserveSig]
            int GetItemPosition([In] IntPtr pidl, [Out] out POINT ppt);
            [PreserveSig]
            int GetSpacing([In, Out] ref POINT ppt);
            [PreserveSig]
            int GetDefaultSpacing([In, Out] ref POINT ppt);
            [PreserveSig]
            int GetAutoArrange();
            [PreserveSig]
            int SelectAndPositionItems([In] uint citems, [In] IntPtr apidl, [In] IntPtr apt, [In] uint dwFlags);

            [PreserveSig]
            int SetGroupBy([In] IntPtr key, [In] bool fAscending);
            [PreserveSig]
            int GetGroupBy([Out] IntPtr pkey, [Out] out bool pfAscending);
            [PreserveSig]
            int SetViewProperty([In] IntPtr pidl, [In] IntPtr propkey, [In] IntPtr propvar);
            [PreserveSig]
            int GetViewProperty([In] IntPtr pidl, [In] IntPtr propkey, [Out] IntPtr ppropvar);
            [PreserveSig]
            int SetTileViewProperties([In] IntPtr pidl, [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
            [PreserveSig]
            int SetExtendedTileViewProperties([In] IntPtr pidl, [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
            [PreserveSig]
            int SetTextGroupSubset([In] uint iGroupId, [In, MarshalAs(UnmanagedType.LPWStr)] string pszQuery);
            [PreserveSig]
            int GetGroupSubset([In] uint iGroupId, [Out] IntPtr ppsa);
            [PreserveSig]
            int GetVisibleItem([In] int iStart, [In] bool fPrevious, out IntPtr ppidl);
            [PreserveSig]
            int GetSelectedItem([In] int iStart, out IntPtr ppidl);
            [PreserveSig]
            int GetItem([In] int iItem, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig]
            int SetCurrentFolderCategorizer([In] IntPtr pcat);
            [PreserveSig]
            int GetCurrentFolderCategorizer([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig]
            int GetFilteredTokenCount([Out] out uint pcount);
            [PreserveSig]
            int GetFilteredToken([In] uint iToken, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig]
            int Item([In] int iItemIndex, out IntPtr ppidl);
            [PreserveSig]
            int SetItemPosition([In] IntPtr pidl, [In] ref POINT ppt);
            [PreserveSig]
            int SetItemPositions([In] uint citems, [In] IntPtr apidl, [In] IntPtr apt);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);
    }
}
