using System;
using KooDesk.Native;

namespace KooDesk.Core
{
    public sealed class DesktopLocationEventDecision
    {
        public bool ShouldQueuePositionCheck { get; set; }
        public bool MarksMouseDrag { get; set; }
        public string Reason { get; set; }
    }

    public static class DesktopLocationEventClassifier
    {
        public static DesktopLocationEventDecision Evaluate(
            IntPtr expectedDesktopListView,
            IntPtr hwnd,
            int idObject,
            int idChild,
            bool leftMouseButtonDown,
            bool dragWasAlreadyObserved)
        {
            DesktopLocationEventDecision decision =
                new DesktopLocationEventDecision();

            if (expectedDesktopListView != IntPtr.Zero &&
                hwnd == expectedDesktopListView)
            {

                decision.ShouldQueuePositionCheck = true;
                decision.Reason = "收到桌面ListView的位置变化事件";
                return decision;
            }

            if (hwnd == IntPtr.Zero &&
                idObject == User32.OBJID_CURSOR &&
                idChild == 0)
            {
                if (leftMouseButtonDown)
                {
                    decision.ShouldQueuePositionCheck = true;
                    decision.MarksMouseDrag = true;
                    decision.Reason = "检测到按住鼠标左键时的光标位置变化，可能正在拖动桌面图标";
                    return decision;
                }

                if (dragWasAlreadyObserved)
                {
                    decision.ShouldQueuePositionCheck = true;
                    decision.Reason = "鼠标拖动已发生，等待按钮释放后检查桌面图标是否偏移";
                    return decision;
                }

                decision.Reason = "普通光标移动且未观察到左键拖动，不检查桌面布局";
                return decision;
            }

            decision.Reason = "事件既不是桌面具体图标事件，也不是需要验收的鼠标拖动事件";
            return decision;
        }
    }
}
