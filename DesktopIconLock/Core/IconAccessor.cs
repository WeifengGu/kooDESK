using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DesktopIconLock.Common;
using DesktopIconLock.Native;

namespace DesktopIconLock.Core
{
    public class IconPositionItem
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public int X { get; set; }
        public int Y { get; set; }

        public override string ToString()
        {
            return string.Format("{0} -> ({1}, {2})", DisplayName ?? Key, X, Y);
        }
    }

    public sealed class IconPositionComparison
    {
        public int TargetIconCount { get; set; }
        public int MatchedIconCount { get; set; }
        public int MismatchCount { get; set; }
        public int MissingTargetIconCount { get; set; }

        public bool HasDifferences
        {
            get
            {
                // 桌面上暂时不存在的目标图标（被用户删除、移动到其他桌面或
                // 尚未加载）不能视为需要反复纠正的坐标偏差；否则会在每轮恢复后
                // 永远失败并错误触发熔断。只对当前已找到图标的坐标差异执行写入。
                return MismatchCount > 0;
            }
        }

        public string Summary
        {
            get
            {
                return string.Format(
                    "目标图标={0}, 当前找到={1}, 坐标偏差={2}, 当前桌面缺失={3}",
                    TargetIconCount,
                    MatchedIconCount,
                    MismatchCount,
                    MissingTargetIconCount);
            }
        }
    }

    public static class IconAccessor
    {
        private static readonly object _lock = new object();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;

        private const uint LVM_GETITEMTEXTW = User32.LVM_FIRST + 115;

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEMW
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        public static string ExtractBaseDisplayName(string key, string displayName)
        {
            if (!string.IsNullOrEmpty(displayName))
            {
                return displayName;
            }
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }
            int hashIdx = key.LastIndexOf('#');
            if (hashIdx > 0 && hashIdx < key.Length - 1)
            {
                int occ;
                if (int.TryParse(key.Substring(hashIdx + 1), out occ))
                {
                    return key.Substring(0, hashIdx);
                }
            }
            return key;
        }

        public static Dictionary<int, IconPositionItem> MatchDesktopItemsToTargets(
            List<KeyValuePair<int, IconPositionItem>> desktopItems,
            Dictionary<string, IconPositionItem> targetPositions)
        {
            Dictionary<int, IconPositionItem> result = new Dictionary<int, IconPositionItem>();
            if (desktopItems == null || desktopItems.Count == 0 || targetPositions == null || targetPositions.Count == 0)
            {
                return result;
            }

            Dictionary<string, List<IconPositionItem>> targetGroups =
                new Dictionary<string, List<IconPositionItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IconPositionItem> pair in targetPositions)
            {
                if (pair.Value == null) continue;
                string baseName = ExtractBaseDisplayName(pair.Key, pair.Value.DisplayName);
                List<IconPositionItem> list;
                if (!targetGroups.TryGetValue(baseName, out list))
                {
                    list = new List<IconPositionItem>();
                    targetGroups[baseName] = list;
                }
                list.Add(pair.Value);
            }

            Dictionary<string, List<KeyValuePair<int, IconPositionItem>>> desktopGroups =
                new Dictionary<string, List<KeyValuePair<int, IconPositionItem>>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < desktopItems.Count; i++)
            {
                KeyValuePair<int, IconPositionItem> kvp = desktopItems[i];
                string name = kvp.Value != null ? ExtractBaseDisplayName(kvp.Value.Key, kvp.Value.DisplayName) : string.Empty;
                if (string.IsNullOrEmpty(name)) continue;

                List<KeyValuePair<int, IconPositionItem>> list;
                if (!desktopGroups.TryGetValue(name, out list))
                {
                    list = new List<KeyValuePair<int, IconPositionItem>>();
                    desktopGroups[name] = list;
                }
                list.Add(kvp);
            }

            foreach (KeyValuePair<string, List<KeyValuePair<int, IconPositionItem>>> groupPair in desktopGroups)
            {
                string groupName = groupPair.Key;
                List<KeyValuePair<int, IconPositionItem>> dList = groupPair.Value;
                List<IconPositionItem> tList;
                if (!targetGroups.TryGetValue(groupName, out tList) || tList.Count == 0)
                {
                    continue;
                }

                if (dList.Count == 1 && tList.Count == 1)
                {
                    result[dList[0].Key] = tList[0];
                }
                else
                {
                    List<IconPositionItem> pool = new List<IconPositionItem>(tList);
                    for (int d = 0; d < dList.Count; d++)
                    {
                        if (pool.Count == 0) break;
                        int curX = dList[d].Value != null ? dList[d].Value.X : 0;
                        int curY = dList[d].Value != null ? dList[d].Value.Y : 0;

                        int bestIdx = 0;
                        long bestDistSq = long.MaxValue;
                        for (int t = 0; t < pool.Count; t++)
                        {
                            long dx = curX - pool[t].X;
                            long dy = curY - pool[t].Y;
                            long distSq = dx * dx + dy * dy;
                            if (distSq < bestDistSq)
                            {
                                bestDistSq = distSq;
                                bestIdx = t;
                            }
                        }

                        result[dList[d].Key] = pool[bestIdx];
                        pool.RemoveAt(bestIdx);
                    }
                }
            }

            return result;
        }

        public static Dictionary<string, IconPositionItem> ReadCurrentIconPositions()
        {
            string traceId = AuditLogger.CurrentTraceId;
            AuditLogger.LogBusinessEntry("读取当前桌面图标坐标", "Win32 SysListView32", "遍历桌面所有项目并解析稳定Key与(X, Y)真实像素坐标", traceId);

            Dictionary<string, IconPositionItem> result = new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);

            lock (_lock)
            {
                IntPtr hListView = User32.GetDesktopListViewHandle();
                if (hListView == IntPtr.Zero)
                {
                    AuditLogger.LogRejected("读取桌面图标", "未定位到桌面SysListView32控件", 404, "无法读取图标", "请确认explorer.exe正在运行", traceId);
                    return result;
                }

                uint pid;
                User32.GetWindowThreadProcessId(hListView, out pid);
                IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
                if (hProcess == IntPtr.Zero)
                {
                    AuditLogger.LogRejected("读取桌面图标", "OpenProcess失败", 500, "无法跨进程访问桌面ListView内存", "检查权限", traceId);
                    return result;
                }

                try
                {
                    int count = User32.SendMessage(hListView, User32.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    AuditLogger.LogCalculationStep("统计桌面图标数量", 1, "LVM_GETITEMCOUNT", "0", string.Format("获取到桌面图标总项数: {0}", count), count.ToString(), "循环提取坐标与文本", traceId);
                    List<IconPositionItem> rawItems = new List<IconPositionItem>();

                    IntPtr memPoint = VirtualAllocEx(hProcess, IntPtr.Zero, 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    IntPtr memItem = VirtualAllocEx(hProcess, IntPtr.Zero, 1024, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    IntPtr memText = VirtualAllocEx(hProcess, IntPtr.Zero, 512, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            User32.SendMessage(hListView, User32.LVM_GETITEMPOSITION, (IntPtr)i, memPoint);
                            byte[] ptBuf = new byte[8];
                            IntPtr bytesRead;
                            ReadProcessMemory(hProcess, memPoint, ptBuf, 8, out bytesRead);
                            int x = BitConverter.ToInt32(ptBuf, 0);
                            int y = BitConverter.ToInt32(ptBuf, 4);

                            LVITEMW lvItem = new LVITEMW();
                            lvItem.mask = 0x0001; // LVIF_TEXT
                            lvItem.iItem = i;
                            lvItem.iSubItem = 0;
                            lvItem.pszText = memText;
                            lvItem.cchTextMax = 256;

                            byte[] itemBytes = new byte[Marshal.SizeOf(typeof(LVITEMW))];
                            IntPtr ptr = Marshal.AllocHGlobal(itemBytes.Length);
                            Marshal.StructureToPtr(lvItem, ptr, false);
                            Marshal.Copy(ptr, itemBytes, 0, itemBytes.Length);
                            Marshal.FreeHGlobal(ptr);

                            IntPtr bytesWritten;
                            WriteProcessMemory(hProcess, memItem, itemBytes, (uint)itemBytes.Length, out bytesWritten);
                            User32.SendMessage(hListView, LVM_GETITEMTEXTW, (IntPtr)i, memItem);

                            byte[] textBuf = new byte[512];
                            ReadProcessMemory(hProcess, memText, textBuf, 512, out bytesRead);
                            string rawName = Encoding.Unicode.GetString(textBuf);
                            int nullIdx = rawName.IndexOf('\0');
                            string name = nullIdx >= 0 ? rawName.Substring(0, nullIdx) : rawName;

                            if (!string.IsNullOrEmpty(name))
                            {
                                rawItems.Add(new IconPositionItem
                                {
                                    DisplayName = name,
                                    X = x,
                                    Y = y
                                });
                            }
                        }

                        Dictionary<string, int> nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < rawItems.Count; i++)
                        {
                            string name = rawItems[i].DisplayName;
                            int cur;
                            nameCounts.TryGetValue(name, out cur);
                            nameCounts[name] = cur + 1;
                        }

                        Dictionary<string, int> nameOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < rawItems.Count; i++)
                        {
                            IconPositionItem item = rawItems[i];
                            int curOcc;
                            nameOccurrences.TryGetValue(item.DisplayName, out curOcc);
                            curOcc++;
                            nameOccurrences[item.DisplayName] = curOcc;
                            item.Key = nameCounts[item.DisplayName] > 1 ? string.Format("{0}#{1}", item.DisplayName, curOcc) : item.DisplayName;
                            result[item.Key] = item;
                            AuditLogger.Debug("图标坐标捕获", string.Format("[{0}/{1}] 捕获图标: {2}", i + 1, count, item), traceId);
                        }

                        AuditLogger.LogResponseReturn("读取当前桌面图标坐标", 200, string.Format("成功捕获 {0} 个桌面图标坐标", result.Count), 0, "成功", "所有图标坐标已捕获完毕", traceId);
                    }
                    finally
                    {
                        if (memPoint != IntPtr.Zero) VirtualFreeEx(hProcess, memPoint, 0, MEM_RELEASE);
                        if (memItem != IntPtr.Zero) VirtualFreeEx(hProcess, memItem, 0, MEM_RELEASE);
                        if (memText != IntPtr.Zero) VirtualFreeEx(hProcess, memText, 0, MEM_RELEASE);
                    }
                }
                catch (Exception ex)
                {
                    AuditLogger.LogException("读取当前桌面图标坐标", "ReadDesktopIconsException", ex.Message, "部分或全部图标坐标未读出", true, ex, traceId);
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            return result;
        }

        public static IconPositionComparison ComparePositions(
            Dictionary<string, IconPositionItem> targetPositions,
            Dictionary<string, IconPositionItem> currentPositions)
        {
            IconPositionComparison result = new IconPositionComparison();
            result.TargetIconCount = targetPositions != null ? targetPositions.Count : 0;

            if (targetPositions == null || targetPositions.Count == 0)
            {
                return result;
            }

            if (currentPositions == null || currentPositions.Count == 0)
            {
                result.MissingTargetIconCount = result.TargetIconCount;
                return result;
            }

            Dictionary<string, List<IconPositionItem>> targetGroups =
                new Dictionary<string, List<IconPositionItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IconPositionItem> pair in targetPositions)
            {
                if (pair.Value == null) continue;
                string baseName = ExtractBaseDisplayName(pair.Key, pair.Value.DisplayName);
                List<IconPositionItem> list;
                if (!targetGroups.TryGetValue(baseName, out list))
                {
                    list = new List<IconPositionItem>();
                    targetGroups[baseName] = list;
                }
                list.Add(pair.Value);
            }

            Dictionary<string, List<IconPositionItem>> currentGroups =
                new Dictionary<string, List<IconPositionItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IconPositionItem> pair in currentPositions)
            {
                if (pair.Value == null) continue;
                string baseName = ExtractBaseDisplayName(pair.Key, pair.Value.DisplayName);
                List<IconPositionItem> list;
                if (!currentGroups.TryGetValue(baseName, out list))
                {
                    list = new List<IconPositionItem>();
                    currentGroups[baseName] = list;
                }
                list.Add(pair.Value);
            }

            foreach (KeyValuePair<string, List<IconPositionItem>> tg in targetGroups)
            {
                string name = tg.Key;
                List<IconPositionItem> tList = tg.Value;
                List<IconPositionItem> cList;
                if (!currentGroups.TryGetValue(name, out cList) || cList.Count == 0)
                {
                    result.MissingTargetIconCount += tList.Count;
                    continue;
                }

                if (tList.Count == 1 && cList.Count == 1)
                {
                    result.MatchedIconCount++;
                    if (tList[0].X != cList[0].X || tList[0].Y != cList[0].Y)
                    {
                        result.MismatchCount++;
                    }
                }
                else
                {
                    List<IconPositionItem> cPool = new List<IconPositionItem>(cList);
                    for (int i = 0; i < tList.Count; i++)
                    {
                        IconPositionItem target = tList[i];
                        if (cPool.Count == 0)
                        {
                            result.MissingTargetIconCount++;
                            continue;
                        }

                        int bestIdx = 0;
                        long bestDistSq = long.MaxValue;
                        for (int c = 0; c < cPool.Count; c++)
                        {
                            long dx = target.X - cPool[c].X;
                            long dy = target.Y - cPool[c].Y;
                            long distSq = dx * dx + dy * dy;
                            if (distSq < bestDistSq)
                            {
                                bestDistSq = distSq;
                                bestIdx = c;
                            }
                        }

                        result.MatchedIconCount++;
                        if (target.X != cPool[bestIdx].X || target.Y != cPool[bestIdx].Y)
                        {
                            result.MismatchCount++;
                        }
                        cPool.RemoveAt(bestIdx);
                    }
                }
            }

            return result;
        }

        public static int ApplyPositions(Dictionary<string, IconPositionItem> targetPositions)
        {
            string traceId = AuditLogger.CurrentTraceId;
            AuditLogger.LogBusinessEntry("应用目标图标坐标", string.Format("目标图标数量={0}", targetPositions != null ? targetPositions.Count : 0), "将目标坐标写回真实桌面ListView，不使用任何模拟层", traceId);

            if (targetPositions == null || targetPositions.Count == 0)
            {
                AuditLogger.LogRejected("应用目标图标坐标", "目标坐标字典为空", 400, "没有可写入的坐标", "忽略操作", traceId);
                return 0;
            }

            int appliedCount = 0;

            lock (_lock)
            {
                IntPtr hListView = User32.GetDesktopListViewHandle();
                if (hListView == IntPtr.Zero)
                {
                    AuditLogger.LogRejected("应用目标图标坐标", "未获取到桌面ListView句柄", 500, "无法写入坐标", "稍后重试", traceId);
                    return 0;
                }

                int originalWindowStyle = User32.GetWindowLong(hListView, User32.GWL_STYLE);
                uint originalExtendedStyle = (uint)User32.SendMessage(
                    hListView,
                    User32.LVM_GETEXTENDEDLISTVIEWSTYLE,
                    IntPtr.Zero,
                    IntPtr.Zero).ToInt32();
                bool autoArrangeWasEnabled = (originalWindowStyle & (int)User32.LVS_AUTOARRANGE) != 0;
                bool snapToGridWasEnabled = (originalExtendedStyle & User32.LVS_EX_SNAPTOGRID) != 0;

                uint pid;
                User32.GetWindowThreadProcessId(hListView, out pid);
                IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
                if (hProcess == IntPtr.Zero)
                {
                    AuditLogger.LogRejected("应用目标图标坐标", "OpenProcess失败", 500, "无法跨进程访问桌面ListView内存", "检查权限", traceId);
                    return 0;
                }

                bool bulkPositioningStarted = false;
                bool bulkPositioningEnded = false;
                try
                {
                    int count = User32.SendMessage(hListView, User32.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    IntPtr memPoint = VirtualAllocEx(hProcess, IntPtr.Zero, 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    IntPtr memItem = VirtualAllocEx(hProcess, IntPtr.Zero, 1024, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    IntPtr memText = VirtualAllocEx(hProcess, IntPtr.Zero, 512, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

                    try
                    {
                        List<KeyValuePair<int, IconPositionItem>> desktopItems = new List<KeyValuePair<int, IconPositionItem>>();
                        for (int index = 0; index < count; index++)
                        {
                            string name = ReadItemName(
                                hListView,
                                hProcess,
                                index,
                                memItem,
                                memText);
                            if (!string.IsNullOrEmpty(name))
                            {
                                int curX;
                                int curY;
                                ReadItemPosition(
                                    hListView,
                                    hProcess,
                                    index,
                                    memPoint,
                                    out curX,
                                    out curY);
                                desktopItems.Add(new KeyValuePair<int, IconPositionItem>(
                                    index,
                                    new IconPositionItem { Key = name, DisplayName = name, X = curX, Y = curY }));
                            }
                        }

                        Dictionary<int, IconPositionItem> matchedMap = MatchDesktopItemsToTargets(desktopItems, targetPositions);
                        int initialMismatch = 0;
                        foreach (KeyValuePair<int, IconPositionItem> matchPair in matchedMap)
                        {
                            int index = matchPair.Key;
                            IconPositionItem target = matchPair.Value;
                            int currentX;
                            int currentY;
                            ReadItemPosition(
                                hListView,
                                hProcess,
                                index,
                                memPoint,
                                out currentX,
                                out currentY);
                            if (currentX != target.X || currentY != target.Y)
                            {
                                initialMismatch++;
                            }
                        }

                        if (initialMismatch == 0)
                        {
                            appliedCount = 0;
                            AuditLogger.LogResponseReturn(
                                "应用目标图标坐标",
                                200,
                                string.Format("目标已与当前桌面一致；匹配{0}个图标，未修改ListView样式或坐标", matchedMap.Count),
                                0,
                                "跳过",
                                "避免无差异时触发Explorer重绘或位置事件",
                                traceId);
                            return appliedCount;
                        }

                        AuditLogger.LogRuleDecision(
                            "批量定位前置差异检查",
                            string.Format("匹配图标={0}, 坐标偏差={1}", matchedMap.Count, initialMismatch),
                            "执行实际坐标写入",
                            "仅在确认存在位置差异后才临时关闭网格对齐",
                            traceId);

                        BeginBulkPositioning(
                            hListView,
                            originalWindowStyle,
                            originalExtendedStyle,
                            autoArrangeWasEnabled,
                            snapToGridWasEnabled,
                            traceId);
                        bulkPositioningStarted = true;

                        int remainingMismatch = initialMismatch;

                        // 关闭网格吸附后执行多轮收敛。即使Explorer在移动过程中交换了占位，
                        // 后续轮次也会把被挤走的早期图标重新纠正到目标位置。
                        for (int pass = 1; pass <= 4 && remainingMismatch > 0; pass++)
                        {
                            remainingMismatch = 0;
                            foreach (KeyValuePair<int, IconPositionItem> matchPair in matchedMap)
                            {
                                int index = matchPair.Key;
                                IconPositionItem target = matchPair.Value;
                                int currentX;
                                int currentY;
                                ReadItemPosition(
                                    hListView,
                                    hProcess,
                                    index,
                                    memPoint,
                                    out currentX,
                                    out currentY);

                                if (currentX != target.X || currentY != target.Y)
                                {
                                    SetItemPosition(hListView, index, target.X, target.Y);
                                    remainingMismatch++;
                                    AuditLogger.Debug(
                                        "批量坐标写入",
                                        string.Format(
                                            "轮次={0}, 图标={1}, 原坐标=({2},{3}), 目标=({4},{5})",
                                            pass,
                                            target.DisplayName ?? target.Key,
                                            currentX,
                                            currentY,
                                            target.X,
                                            target.Y),
                                        traceId);
                                }
                            }

                            if (remainingMismatch > 0)
                            {
                                Thread.Sleep(35);
                            }
                        }

                        appliedCount = matchedMap.Count;
                        Thread.Sleep(60);

                        EndBulkPositioning(
                            hListView,
                            originalExtendedStyle,
                            snapToGridWasEnabled,
                            traceId);
                        bulkPositioningEnded = true;

                        int verificationMismatch = 0;
                        foreach (KeyValuePair<int, IconPositionItem> matchPair in matchedMap)
                        {
                            int index = matchPair.Key;
                            IconPositionItem target = matchPair.Value;
                            int currentX;
                            int currentY;
                            ReadItemPosition(
                                hListView,
                                hProcess,
                                index,
                                memPoint,
                                out currentX,
                                out currentY);
                            if (currentX != target.X || currentY != target.Y)
                            {
                                verificationMismatch++;
                                AuditLogger.Warn(
                                    "坐标回读校验",
                                    string.Format(
                                        "图标={0}, 期望=({1},{2}), 实际=({3},{4})",
                                        target.DisplayName ?? target.Key,
                                        target.X,
                                        target.Y,
                                        currentX,
                                        currentY),
                                    traceId);
                            }
                        }

                        if (verificationMismatch == 0)
                        {
                            AuditLogger.LogResponseReturn(
                                "应用目标图标坐标",
                                200,
                                string.Format("成功匹配{0}个图标，回读校验0处偏差", appliedCount),
                                0,
                                "成功",
                                "桌面图标已按目标结构精确落位",
                                traceId);
                        }
                        else
                        {
                            AuditLogger.LogResponseReturn(
                                "应用目标图标坐标",
                                206,
                                string.Format("匹配{0}个图标，回读仍有{1}处偏差", appliedCount, verificationMismatch),
                                0,
                                "部分完成",
                                "控制器将在稳定校验阶段再次纠正",
                                traceId);
                        }
                    }
                    finally
                    {
                        if (memPoint != IntPtr.Zero) VirtualFreeEx(hProcess, memPoint, 0, MEM_RELEASE);
                        if (memItem != IntPtr.Zero) VirtualFreeEx(hProcess, memItem, 0, MEM_RELEASE);
                        if (memText != IntPtr.Zero) VirtualFreeEx(hProcess, memText, 0, MEM_RELEASE);
                    }
                }
                catch (Exception ex)
                {
                    AuditLogger.LogException("应用目标图标坐标", "SetPositionException", ex.Message, "部分坐标未能写回", true, ex, traceId);
                }
                finally
                {
                    // 异常路径也要恢复“与网格对齐”偏好；正常路径已经恢复过一次，
                    // 不能重复切换样式，否则会额外触发Explorer重绘和位置事件。
                    if (bulkPositioningStarted && !bulkPositioningEnded)
                    {
                        EndBulkPositioning(
                            hListView,
                            originalExtendedStyle,
                            snapToGridWasEnabled,
                            traceId);
                    }
                    CloseHandle(hProcess);
                }
            }

            return appliedCount;
        }

        private static void BeginBulkPositioning(
            IntPtr hListView,
            int originalWindowStyle,
            uint originalExtendedStyle,
            bool autoArrangeWasEnabled,
            bool snapToGridWasEnabled,
            string traceId)
        {
            if (autoArrangeWasEnabled)
            {
                int newStyle = originalWindowStyle & ~(int)User32.LVS_AUTOARRANGE;
                User32.SetWindowLong(hListView, User32.GWL_STYLE, newStyle);
                AuditLogger.Warn(
                    "自动排列检测",
                    "检测到桌面启用了自动排列图标，锁定模式下已关闭，否则系统会覆盖精确坐标",
                    traceId);
            }

            if (snapToGridWasEnabled)
            {
                User32.SendMessage(
                    hListView,
                    User32.LVM_SETEXTENDEDLISTVIEWSTYLE,
                    (IntPtr)User32.LVS_EX_SNAPTOGRID,
                    IntPtr.Zero);
                AuditLogger.Debug(
                    "批量定位前置处理",
                    string.Format("临时关闭与网格对齐，原扩展样式=0x{0:X8}", originalExtendedStyle),
                    traceId);
            }
        }

        private static void EndBulkPositioning(
            IntPtr hListView,
            uint originalExtendedStyle,
            bool snapToGridWasEnabled,
            string traceId)
        {
            if (hListView == IntPtr.Zero || !snapToGridWasEnabled) return;

            User32.SendMessage(
                hListView,
                User32.LVM_SETEXTENDEDLISTVIEWSTYLE,
                (IntPtr)User32.LVS_EX_SNAPTOGRID,
                (IntPtr)(originalExtendedStyle & User32.LVS_EX_SNAPTOGRID));
            AuditLogger.Debug(
                "批量定位收尾处理",
                "坐标写入完成，已恢复用户原有的“将图标与网格对齐”偏好",
                traceId);
        }

        private static string ReadItemName(
            IntPtr hListView,
            IntPtr hProcess,
            int index,
            IntPtr memItem,
            IntPtr memText)
        {
            LVITEMW lvItem = new LVITEMW();
            lvItem.mask = 0x0001;
            lvItem.iItem = index;
            lvItem.iSubItem = 0;
            lvItem.pszText = memText;
            lvItem.cchTextMax = 256;

            byte[] itemBytes = new byte[Marshal.SizeOf(typeof(LVITEMW))];
            IntPtr ptr = Marshal.AllocHGlobal(itemBytes.Length);
            try
            {
                Marshal.StructureToPtr(lvItem, ptr, false);
                Marshal.Copy(ptr, itemBytes, 0, itemBytes.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            IntPtr bytesWritten;
            WriteProcessMemory(hProcess, memItem, itemBytes, (uint)itemBytes.Length, out bytesWritten);
            User32.SendMessage(hListView, LVM_GETITEMTEXTW, (IntPtr)index, memItem);

            byte[] textBuffer = new byte[512];
            IntPtr bytesRead;
            ReadProcessMemory(hProcess, memText, textBuffer, 512, out bytesRead);
            string rawName = Encoding.Unicode.GetString(textBuffer);
            int nullIndex = rawName.IndexOf('\0');
            return nullIndex >= 0 ? rawName.Substring(0, nullIndex) : rawName;
        }

        private static void ReadItemPosition(
            IntPtr hListView,
            IntPtr hProcess,
            int index,
            IntPtr memPoint,
            out int x,
            out int y)
        {
            User32.SendMessage(hListView, User32.LVM_GETITEMPOSITION, (IntPtr)index, memPoint);
            byte[] pointBuffer = new byte[8];
            IntPtr bytesRead;
            ReadProcessMemory(hProcess, memPoint, pointBuffer, 8, out bytesRead);
            x = BitConverter.ToInt32(pointBuffer, 0);
            y = BitConverter.ToInt32(pointBuffer, 4);
        }

        private static void SetItemPosition(IntPtr hListView, int index, int x, int y)
        {
            IntPtr packedPosition = (IntPtr)((y << 16) | (x & 0xFFFF));
            User32.SendMessage(
                hListView,
                User32.LVM_SETITEMPOSITION,
                (IntPtr)index,
                packedPosition);
        }
    }
}
