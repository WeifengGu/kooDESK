namespace KooDesk.Resources
{

    internal static class UIText
    {

        internal static class App
        {
            public const string Name = "苦桌";

            public const string EnglishName = "kooDESK";
            public const string TooltipLocked = Name + " (已开启锁定)";
            public const string TooltipUnlocked = Name + " (已暂停锁定)";
            public const string AlreadyRunning = Name + "已在后台运行中，请查看任务栏右下角托盘图标。";

            public const string StartupFailedFormat = "启动失败: {0}";
        }

        internal static class Menu
        {
            public const string SaveCurrent = "保存当前布局";
            public const string ApplyBase = "恢复保存布局";

            public const string ApplyBaseMissing = "恢复基准布局（尚未保存）";
            public const string About = "关于";
            public const string Startup = "开机自启动";
            public const string Exit = "退出";
        }

        internal static class Balloon
        {
            public const string SaveDoneTitle = "布局保存完成";

            public const string SaveDoneBodyFormat = "当前布局已保存为唯一基准布局。\n分辨率：{0}\n缩放：{1}%\n图标数：{2}";

            public const string StartupTitle = "开机自启动设置";
            public const string StartupEnabled = "已注册为当前用户开机自启动项。";
            public const string StartupDisabled = "已关闭当前用户开机自启动。";

            public const string BaseAppliedTitle = "基准布局已应用";
            public const string BaseAppliedBody = "桌面图标已按保存的基准布局归位。";

            public const string LockStateTitle = "锁定状态变更";
            public const string UnlockedBody = "已临时解锁，可自由移动图标。再次锁定时将检查布局是否发生变化。";
            public const string NoChangeBody = "桌面布局未发生变动，已开启桌面图标锁定保护。";

            public const string SavedAndLockedTitle = "布局保存并锁定";

            public const string SavedAndLockedBodyFormat = "已保存并锁定当前布局。\n分辨率：{0}\n缩放：{1}%";

            public const string FirstRunTitle = App.Name + "已启动";
            public const string FirstRunBody = "已自动保存当前桌面为基准布局。\n提示：请右键桌面确认【查看->自动排列图标】已关闭，以确保位置精确锁定！";
        }

        internal static class Dialog
        {
            public const string TitleInfo = "提示";
            public const string TitleError = "错误";

            public const string TitleSaveCurrent = "保存当前布局";

            public const string SaveFailedFormat = "保存布局失败：{0}";

            public const string TitleApplyBase = "应用基准布局";
            public const string ApplyBaseConflict = "未能应用基准布局（可能与其他写入任务冲突，或布局中的图标已不存在于桌面），请稍后重试。";

            public const string ApplyBaseFailedFormat = "应用基准布局失败：{0}";

            public const string TitleLockLayout = "锁定桌面布局";

            public const string LockFailedFormat = "锁定操作失败：{0}\n\n当前仍保持解锁状态，桌面图标位置未被主动恢复。";

            public const string TitleStartup = "开机自启动设置";

            public const string StartupFailedFormat = "修改开机自启动失败：{0}";
            public const string UnknownError = "未知错误";
        }

        internal static class About
        {
            public const string WindowTitle = "关于 " + App.EnglishName;
            public const string Heading = App.Name + " (" + App.EnglishName + ")";

            public const string Description = "一款支持多显示器的桌面图标布局保存与恢复软件。";
            public const string AuthorLine = "作者：Koo";
            public const string CodeByLine = "代码编写：AI";
            public const string EmailLine = "邮箱：404729936@qq.com";

            public const string BuildDateLineFormat = "编译日期：{0}";

            public const string BuildDateUnknown = "未知";
            public const string SourceLine = "源码来源：https://github.com/CangShui/DesktopIconLock";

            public const string InfoBodyFormat = Description + "\n\n" + AuthorLine + "\n" + EmailLine + "\n" + CodeByLine + "\n" + BuildDateLineFormat + "\n" + SourceLine;
            public const string ButtonOk = "确定";
        }
    }
}
