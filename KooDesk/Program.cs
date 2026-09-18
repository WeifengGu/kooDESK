using System;
using System.Threading;
using System.Windows.Forms;
using KooDesk.Core;
using KooDesk.Native;
using KooDesk.Tray;
using KooDesk.Resources;

namespace KooDesk
{
    static class Program
    {
        private static Mutex _singleInstanceMutex;

        [STAThread]
        static void Main(string[] args)
        {

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

                    Environment.ExitCode = RunSaveLayoutCommand() ? 0 : 1;
                    return;
                }
            }

            bool isNewInstance;
            _singleInstanceMutex = new Mutex(true, "KooDesk_SingleInstance_Mutex_999", out isNewInstance);
            if (!isNewInstance)
            {

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                MessageBox.Show(UIText.App.AlreadyRunning, UIText.Dialog.TitleInfo, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
            };

            try
            {
                LayoutStore store = new LayoutStore();
                LockController controller = new LockController(store);
                controller.Start();

                Application.Run(new TrayContext(controller));
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(UIText.App.StartupFailedFormat, ex.Message), UIText.Dialog.TitleError, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                ReleaseSingleInstanceMutex();
            }
        }

        private static bool RunSaveLayoutCommand()
        {
            try
            {
                LayoutStore commandStore = new LayoutStore();
                using (LockController commandController = new LockController(commandStore))
                {
                    commandController.SaveCurrentLayout();
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(string.Format(UIText.Dialog.SaveFailedFormat, ex.Message));
                return false;
            }
        }

        private static void ReleaseSingleInstanceMutex()
        {
            if (_singleInstanceMutex == null) return;

            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {

            }
            finally
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
    }
}
