using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KooDesk.Core
{

    public static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "KooDesk";

        private const string LegacyValueName = "DesktopIconLock";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    string value = key != null ? key.GetValue(ValueName) as string : null;
                    string expected = BuildCommandLine();
                    bool enabled = string.Equals(
                        Normalize(value),
                        Normalize(expected),
                        StringComparison.OrdinalIgnoreCase);

                    return enabled;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetEnabled(bool enabled, out string errorMessage)
        {

            errorMessage = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        errorMessage = "无法打开当前用户开机自启动注册表路径。";
                        return false;
                    }

                    if (enabled)
                    {
                        string commandLine = BuildCommandLine();
                        key.SetValue(ValueName, commandLine, RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }

                    key.DeleteValue(LegacyValueName, false);
                }

                bool actual = IsEnabled();
                bool success = actual == enabled;
                if (!success)
                {
                    errorMessage = "注册表写入后状态校验不一致。";
                }

                return success;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static string BuildCommandLine()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim();
        }
    }
}
