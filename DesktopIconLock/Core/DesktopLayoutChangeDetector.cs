using System;
using System.Collections.Generic;

namespace DesktopIconLock.Core
{
    public sealed class DesktopLayoutChangeResult
    {
        public bool HasChanged { get; set; }
        public bool ComparisonAvailable { get; set; }
        public int PreviousIconCount { get; set; }
        public int CurrentIconCount { get; set; }
        public int MovedIconCount { get; set; }
        public int AddedIconCount { get; set; }
        public int RemovedIconCount { get; set; }

        public string Summary
        {
            get
            {
                return string.Format(
                    "解锁时图标数={0}, 当前图标数={1}, 移动={2}, 新增={3}, 删除={4}",
                    PreviousIconCount,
                    CurrentIconCount,
                    MovedIconCount,
                    AddedIconCount,
                    RemovedIconCount);
            }
        }
    }

    public static class DesktopLayoutChangeDetector
    {
        public static DesktopLayoutChangeResult Compare(
            Dictionary<string, IconPositionItem> previous,
            Dictionary<string, IconPositionItem> current)
        {
            DesktopLayoutChangeResult result = new DesktopLayoutChangeResult();
            result.ComparisonAvailable = previous != null && current != null;

            if (!result.ComparisonAvailable)
            {
                result.HasChanged = true;
                result.PreviousIconCount = previous != null ? previous.Count : 0;
                result.CurrentIconCount = current != null ? current.Count : 0;
                return result;
            }

            result.PreviousIconCount = previous.Count;
            result.CurrentIconCount = current.Count;

            Dictionary<string, List<IconPositionItem>> prevGroups =
                new Dictionary<string, List<IconPositionItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IconPositionItem> pair in previous)
            {
                if (pair.Value == null) continue;
                string name = IconAccessor.ExtractBaseDisplayName(pair.Key, pair.Value.DisplayName);
                List<IconPositionItem> list;
                if (!prevGroups.TryGetValue(name, out list))
                {
                    list = new List<IconPositionItem>();
                    prevGroups[name] = list;
                }
                list.Add(pair.Value);
            }

            Dictionary<string, List<IconPositionItem>> currGroups =
                new Dictionary<string, List<IconPositionItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IconPositionItem> pair in current)
            {
                if (pair.Value == null) continue;
                string name = IconAccessor.ExtractBaseDisplayName(pair.Key, pair.Value.DisplayName);
                List<IconPositionItem> list;
                if (!currGroups.TryGetValue(name, out list))
                {
                    list = new List<IconPositionItem>();
                    currGroups[name] = list;
                }
                list.Add(pair.Value);
            }

            foreach (KeyValuePair<string, List<IconPositionItem>> pg in prevGroups)
            {
                string name = pg.Key;
                List<IconPositionItem> pList = pg.Value;
                List<IconPositionItem> cList;
                if (!currGroups.TryGetValue(name, out cList) || cList.Count == 0)
                {
                    result.RemovedIconCount += pList.Count;
                    continue;
                }

                if (pList.Count == 1 && cList.Count == 1)
                {
                    if (pList[0].X != cList[0].X || pList[0].Y != cList[0].Y)
                    {
                        result.MovedIconCount++;
                    }
                }
                else
                {
                    List<IconPositionItem> cPool = new List<IconPositionItem>(cList);
                    for (int i = 0; i < pList.Count; i++)
                    {
                        if (cPool.Count == 0)
                        {
                            result.RemovedIconCount++;
                            continue;
                        }
                        IconPositionItem pItem = pList[i];
                        int bestIdx = 0;
                        long bestDistSq = long.MaxValue;
                        for (int c = 0; c < cPool.Count; c++)
                        {
                            long dx = pItem.X - cPool[c].X;
                            long dy = pItem.Y - cPool[c].Y;
                            long dist = dx * dx + dy * dy;
                            if (dist < bestDistSq)
                            {
                                bestDistSq = dist;
                                bestIdx = c;
                            }
                        }

                        if (pItem.X != cPool[bestIdx].X || pItem.Y != cPool[bestIdx].Y)
                        {
                            result.MovedIconCount++;
                        }
                        cPool.RemoveAt(bestIdx);
                    }
                }
            }

            foreach (KeyValuePair<string, List<IconPositionItem>> cg in currGroups)
            {
                string name = cg.Key;
                List<IconPositionItem> cList = cg.Value;
                List<IconPositionItem> pList;
                if (!prevGroups.TryGetValue(name, out pList))
                {
                    result.AddedIconCount += cList.Count;
                }
                else if (cList.Count > pList.Count)
                {
                    result.AddedIconCount += (cList.Count - pList.Count);
                }
            }

            result.HasChanged =
                result.MovedIconCount > 0 ||
                result.AddedIconCount > 0 ||
                result.RemovedIconCount > 0;
            return result;
        }

        public static Dictionary<string, IconPositionItem> Clone(
            Dictionary<string, IconPositionItem> source)
        {
            if (source == null) return null;

            Dictionary<string, IconPositionItem> clone =
                new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IconPositionItem> pair in source)
            {
                IconPositionItem item = pair.Value;
                clone[pair.Key] = item == null
                    ? null
                    : new IconPositionItem
                    {
                        Key = item.Key,
                        DisplayName = item.DisplayName,
                        X = item.X,
                        Y = item.Y
                    };
            }
            return clone;
        }
    }
}
