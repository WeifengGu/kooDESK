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

            foreach (KeyValuePair<string, IconPositionItem> previousPair in previous)
            {
                IconPositionItem currentItem;
                if (!current.TryGetValue(previousPair.Key, out currentItem))
                {
                    result.RemovedIconCount++;
                    continue;
                }

                IconPositionItem previousItem = previousPair.Value;
                if (previousItem == null ||
                    currentItem == null ||
                    previousItem.X != currentItem.X ||
                    previousItem.Y != currentItem.Y)
                {
                    result.MovedIconCount++;
                }
            }

            foreach (KeyValuePair<string, IconPositionItem> currentPair in current)
            {
                if (!previous.ContainsKey(currentPair.Key))
                {
                    result.AddedIconCount++;
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
