using System;
using System.Windows.Forms;
using Microsoft.Win32;
using DesktopIconLock.Common;

namespace DesktopIconLock.Core
{
    /// <summary>
    /// 当前用户开机启动管理。使用HKCU Run，不需要管理员权限。
    /// </summary>
    public static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DesktopIconLock";

        public static bool IsEnabled()
        {
            string traceId = AuditLogger.CurrentTraceId;
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

                    AuditLogger.LogRuleDecision(
                        "开机启动状态检查",
                        string.Format("注册表值={0}; 当前程序={1}", value ?? "<不存在>", expected),
                        enabled ? "已启用" : "未启用",
                        enabled ? "注册表启动路径与当前程序完全一致" : "未注册或路径已变化",
                        traceId);
                    return enabled;
                }
            }
            catch (Exception ex)
            {
                AuditLogger.LogException(
                    "开机启动状态检查",
                    ex.GetType().Name,
                    ex.Message,
                    "托盘菜单将显示为未启用",
                    true,
                    ex,
                    traceId);
                return false;
            }
        }

        public static bool SetEnabled(bool enabled, out string errorMessage)
        {
            string traceId = AuditLogger.GenerateTraceId();
            AuditLogger.LogRequestArrival(
                "StartupManager.SetEnabled",
                "托盘右键菜单",
                string.Format("目标状态={0}", enabled ? "启用" : "关闭"),
                "准备修改当前用户开机启动注册表项",
                traceId);

            errorMessage = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        errorMessage = "无法打开当前用户开机启动注册表路径。";
                        AuditLogger.LogRejected(
                            "注册表写入前置检查",
                            errorMessage,
                            500,
                            "开机启动状态未改变",
                            "检查当前用户注册表权限",
                            traceId);
                        return false;
                    }

                    if (enabled)
                    {
                        string commandLine = BuildCommandLine();
                        key.SetValue(ValueName, commandLine, RegistryValueKind.String);
                        AuditLogger.LogStorageOperation(
                            "新增/更新开机启动项",
                            "HKCU Run",
                            string.Format("名称={0}, 程序={1}", ValueName, commandLine),
                            1,
                            "注册成功",
                            traceId);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                        AuditLogger.LogStorageOperation(
                            "删除开机启动项",
                            "HKCU Run",
                            string.Format("名称={0}", ValueName),
                            1,
                            "关闭成功",
                            traceId);
                    }
                }

                bool actual = IsEnabled();
                bool success = actual == enabled;
                if (!success)
                {
                    errorMessage = "注册表写入后状态校验不一致。";
                }

                AuditLogger.LogResponseReturn(
                    "StartupManager.SetEnabled",
                    success ? 200 : 500,
                    string.Format("目标={0}, 实际={1}", enabled, actual),
                    0,
                    success ? "成功" : "失败",
                    success ? "开机启动状态已更新" : "开机启动状态未按预期更新",
                    traceId);
                return success;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                AuditLogger.LogException(
                    "修改开机启动项",
                    ex.GetType().Name,
                    ex.Message,
                    "开机启动状态未改变",
                    true,
                    ex,
                    traceId);
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
