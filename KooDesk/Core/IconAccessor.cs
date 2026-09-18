using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KooDesk.Native;

namespace KooDesk.Core
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
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SYNCHRONIZE = 0x00100000;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;

        private const uint LVM_GETITEMTEXTW = User32.LVM_FIRST + 115;
        private const uint LVIF_TEXT = 0x0001;

        private const uint TEXT_BUFFER_SIZE = 512;
        private const int TEXT_MAX_CHARS = 256;
        private static readonly int ITEM_STRUCT_SIZE = Marshal.SizeOf(typeof(LVITEMW));

        private const uint PROCESS_READ_ACCESS =
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_SYNCHRONIZE;
        private const uint PROCESS_WRITE_ACCESS = PROCESS_READ_ACCESS;

        private const int ConvergencePassCount = 4;
        private const int ConvergenceSleepMs = 35;
        private const int SettleSleepMs = 60;

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

        private sealed class RemoteListView : IDisposable
        {
            public readonly IntPtr ProcessHandle;
            public readonly IntPtr PointRemote;
            public readonly IntPtr ItemRemote;
            public readonly IntPtr TextRemote;
            public readonly byte[] LocalPoint;
            public readonly byte[] LocalText;
            public readonly byte[] LocalItem;
            public readonly IntPtr LocalItemPtr;
            public int ReadFailureCount;
            private bool _disposed;

            private RemoteListView(IntPtr hProcess, IntPtr point, IntPtr item, IntPtr text)
            {
                ProcessHandle = hProcess;
                PointRemote = point;
                ItemRemote = item;
                TextRemote = text;
                LocalPoint = new byte[8];
                LocalText = new byte[TEXT_BUFFER_SIZE];
                LocalItem = new byte[ITEM_STRUCT_SIZE];
                LocalItemPtr = Marshal.AllocHGlobal(ITEM_STRUCT_SIZE);
            }

            public static RemoteListView TryOpen(IntPtr hListView, uint desiredAccess)
            {
                uint pid;
                User32.GetWindowThreadProcessId(hListView, out pid);
                if (pid == 0)
                {
                    return null;
                }

                IntPtr hProcess = OpenProcess(desiredAccess, false, pid);
                if (hProcess == IntPtr.Zero)
                {

                    hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
                }
                if (hProcess == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr point = VirtualAllocEx(hProcess, IntPtr.Zero, 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                IntPtr item = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)ITEM_STRUCT_SIZE, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                IntPtr text = VirtualAllocEx(hProcess, IntPtr.Zero, TEXT_BUFFER_SIZE, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

                if (point == IntPtr.Zero || item == IntPtr.Zero || text == IntPtr.Zero)
                {
                    if (point != IntPtr.Zero) VirtualFreeEx(hProcess, point, 0, MEM_RELEASE);
                    if (item != IntPtr.Zero) VirtualFreeEx(hProcess, item, 0, MEM_RELEASE);
                    if (text != IntPtr.Zero) VirtualFreeEx(hProcess, text, 0, MEM_RELEASE);
                    CloseHandle(hProcess);
                    return null;
                }

                return new RemoteListView(hProcess, point, item, text);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (PointRemote != IntPtr.Zero) VirtualFreeEx(ProcessHandle, PointRemote, 0, MEM_RELEASE);
                if (ItemRemote != IntPtr.Zero) VirtualFreeEx(ProcessHandle, ItemRemote, 0, MEM_RELEASE);
                if (TextRemote != IntPtr.Zero) VirtualFreeEx(ProcessHandle, TextRemote, 0, MEM_RELEASE);
                if (LocalItemPtr != IntPtr.Zero) Marshal.FreeHGlobal(LocalItemPtr);
                CloseHandle(ProcessHandle);
            }
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

            Dictionary<string, IconPositionItem> result = new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);

            lock (_lock)
            {
                IntPtr hListView = User32.GetDesktopListViewHandle();
                if (hListView == IntPtr.Zero)
                {
                    return result;
                }

                RemoteListView memory = RemoteListView.TryOpen(hListView, PROCESS_READ_ACCESS);
                if (memory == null)
                {
                    return result;
                }

                try
                {
                    List<KeyValuePair<int, IconPositionItem>> desktopItems = EnumerateDesktopItems(hListView, memory);
                    List<IconPositionItem> rawItems = new List<IconPositionItem>(desktopItems.Count);
                    for (int i = 0; i < desktopItems.Count; i++)
                    {
                        rawItems.Add(desktopItems[i].Value);
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
                    }
                }
                catch
                {

                }
                finally
                {
                    memory.Dispose();
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

            if (targetPositions == null || targetPositions.Count == 0)
            {
                return 0;
            }

            int appliedCount = 0;

            lock (_lock)
            {
                IntPtr hListView = User32.GetDesktopListViewHandle();
                if (hListView == IntPtr.Zero)
                {
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

                RemoteListView memory = RemoteListView.TryOpen(hListView, PROCESS_WRITE_ACCESS);
                if (memory == null)
                {
                    return 0;
                }

                bool bulkPositioningStarted = false;
                bool bulkPositioningEnded = false;
                try
                {
                    List<KeyValuePair<int, IconPositionItem>> desktopItems = EnumerateDesktopItems(hListView, memory);
                    Dictionary<int, IconPositionItem> matchedMap = MatchDesktopItemsToTargets(desktopItems, targetPositions);

                    int initialMismatch = 0;
                    for (int m = 0; m < desktopItems.Count; m++)
                    {
                        IconPositionItem matchedTarget;
                        if (!matchedMap.TryGetValue(desktopItems[m].Key, out matchedTarget)) continue;
                        if (matchedTarget.X != desktopItems[m].Value.X || matchedTarget.Y != desktopItems[m].Value.Y)
                        {
                            initialMismatch++;
                        }
                    }

                    if (initialMismatch == 0)
                    {
                        appliedCount = 0;
                        return appliedCount;
                    }

                    BeginBulkPositioning(
                        hListView,
                        originalWindowStyle,
                        originalExtendedStyle,
                        autoArrangeWasEnabled,
                        snapToGridWasEnabled);
                    bulkPositioningStarted = true;

                    int remainingMismatch = initialMismatch;

                    for (int pass = 1; pass <= ConvergencePassCount && remainingMismatch > 0; pass++)
                    {
                        remainingMismatch = 0;
                        foreach (KeyValuePair<int, IconPositionItem> matchPair in matchedMap)
                        {
                            int index = matchPair.Key;
                            IconPositionItem target = matchPair.Value;
                            int currentX;
                            int currentY;
                            if (!ReadItemPosition(hListView, memory, index, out currentX, out currentY))
                            {

                                remainingMismatch++;
                                continue;
                            }

                            if (currentX != target.X || currentY != target.Y)
                            {
                                SetItemPosition(hListView, index, target.X, target.Y);
                                remainingMismatch++;
                            }
                        }

                        if (remainingMismatch > 0)
                        {
                            Thread.Sleep(ConvergenceSleepMs);
                        }
                    }

                    appliedCount = matchedMap.Count;
                    Thread.Sleep(SettleSleepMs);

                    EndBulkPositioning(
                        hListView,
                        originalExtendedStyle,
                        snapToGridWasEnabled);
                    bulkPositioningEnded = true;
                }
                catch
                {

                }
                finally
                {

                    if (bulkPositioningStarted && !bulkPositioningEnded)
                    {
                        EndBulkPositioning(
                            hListView,
                            originalExtendedStyle,
                            snapToGridWasEnabled);
                    }
                    memory.Dispose();
                }
            }

            return appliedCount;
        }

        private static void BeginBulkPositioning(
            IntPtr hListView,
            int originalWindowStyle,
            uint originalExtendedStyle,
            bool autoArrangeWasEnabled,
            bool snapToGridWasEnabled)
        {
            if (autoArrangeWasEnabled)
            {
                int newStyle = originalWindowStyle & ~(int)User32.LVS_AUTOARRANGE;
                User32.SetWindowLong(hListView, User32.GWL_STYLE, newStyle);
            }

            if (snapToGridWasEnabled)
            {
                User32.SendMessage(
                    hListView,
                    User32.LVM_SETEXTENDEDLISTVIEWSTYLE,
                    (IntPtr)User32.LVS_EX_SNAPTOGRID,
                    IntPtr.Zero);
            }
        }

        private static void EndBulkPositioning(
            IntPtr hListView,
            uint originalExtendedStyle,
            bool snapToGridWasEnabled)
        {
            if (hListView == IntPtr.Zero || !snapToGridWasEnabled) return;

            User32.SendMessage(
                hListView,
                User32.LVM_SETEXTENDEDLISTVIEWSTYLE,
                (IntPtr)User32.LVS_EX_SNAPTOGRID,
                (IntPtr)(originalExtendedStyle & User32.LVS_EX_SNAPTOGRID));
        }

        private static List<KeyValuePair<int, IconPositionItem>> EnumerateDesktopItems(
            IntPtr hListView,
            RemoteListView memory)
        {
            List<KeyValuePair<int, IconPositionItem>> items = new List<KeyValuePair<int, IconPositionItem>>();
            int count = User32.SendMessage(hListView, User32.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();

            for (int index = 0; index < count; index++)
            {
                int x;
                int y;
                if (!ReadItemPosition(hListView, memory, index, out x, out y))
                {
                    memory.ReadFailureCount++;
                    continue;
                }

                string name = ReadItemName(hListView, memory, index);
                if (string.IsNullOrEmpty(name)) continue;

                items.Add(new KeyValuePair<int, IconPositionItem>(
                    index,
                    new IconPositionItem { Key = name, DisplayName = name, X = x, Y = y }));
            }

            return items;
        }

        private static string ReadItemName(IntPtr hListView, RemoteListView memory, int index)
        {
            LVITEMW lvItem = new LVITEMW();
            lvItem.mask = LVIF_TEXT;
            lvItem.iItem = index;
            lvItem.iSubItem = 0;
            lvItem.pszText = memory.TextRemote;
            lvItem.cchTextMax = TEXT_MAX_CHARS;

            Marshal.StructureToPtr(lvItem, memory.LocalItemPtr, false);
            Marshal.Copy(memory.LocalItemPtr, memory.LocalItem, 0, memory.LocalItem.Length);

            IntPtr bytesWritten;
            if (!WriteProcessMemory(memory.ProcessHandle, memory.ItemRemote, memory.LocalItem, (uint)memory.LocalItem.Length, out bytesWritten)
                || bytesWritten.ToInt64() < memory.LocalItem.Length)
            {
                return null;
            }

            User32.SendMessage(hListView, LVM_GETITEMTEXTW, (IntPtr)index, memory.ItemRemote);

            Array.Clear(memory.LocalText, 0, memory.LocalText.Length);
            IntPtr bytesRead;
            if (!ReadProcessMemory(memory.ProcessHandle, memory.TextRemote, memory.LocalText, TEXT_BUFFER_SIZE, out bytesRead))
            {
                return null;
            }

            int readableBytes = (int)Math.Min((long)memory.LocalText.Length, bytesRead.ToInt64() & ~1L);
            string rawName = Encoding.Unicode.GetString(memory.LocalText, 0, readableBytes);
            int nullIndex = rawName.IndexOf('\0');
            return nullIndex >= 0 ? rawName.Substring(0, nullIndex) : rawName;
        }

        private static bool ReadItemPosition(
            IntPtr hListView,
            RemoteListView memory,
            int index,
            out int x,
            out int y)
        {
            x = 0;
            y = 0;

            User32.SendMessage(hListView, User32.LVM_GETITEMPOSITION, (IntPtr)index, memory.PointRemote);

            IntPtr bytesRead;
            if (!ReadProcessMemory(memory.ProcessHandle, memory.PointRemote, memory.LocalPoint, 8, out bytesRead))
            {
                return false;
            }
            if (bytesRead.ToInt64() < 8)
            {
                return false;
            }

            x = BitConverter.ToInt32(memory.LocalPoint, 0);
            y = BitConverter.ToInt32(memory.LocalPoint, 4);
            return true;
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
