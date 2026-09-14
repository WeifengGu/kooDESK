using System;
using System.Drawing;
using System.Windows.Forms;
using DesktopIconLock.Core;
using DesktopIconLock.Native;
using DesktopIconLock.Tray;

class H
{
    [STAThread]
    static void Main()
    {
        DisplayInfo.EnablePerMonitorDpiAwareness();
        Application.EnableVisualStyles();
        var store = new LayoutStore();
        var history = new HistoryStore(store.ConfigDirectory);
        var records = history.GetRecords();
        var form = new HistoryPreviewForm();
        form.ShowOverview(records);
        var timer = new Timer();
        timer.Interval = 4500;
        timer.Tick += delegate
        {
            timer.Stop();
            form.Close();
        };
        timer.Start();
        Application.Run(form);
    }
}
