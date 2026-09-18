using System;
using System.Collections.Generic;
using KooDesk.Native;

namespace KooDesk.Core
{
    public sealed class ProfileGeometryCompatibility
    {
        public bool IsCompatible { get; set; }
        public string Reason { get; set; }
    }

    public static class AdaptiveMapper
    {
        private class PlacementCandidate
        {
            public IconPositionItem Source;
            public double DesiredColumn;
            public double DesiredRow;
        }

        public static void PopulateProfileGeometry(
            DesktopProfile profile,
            MonitorProfileInfo monitor,
            Dictionary<string, IconPositionItem> icons)
        {
            if (profile == null) return;

            profile.WorkAreaWidth = monitor != null && monitor.WorkArea.Width > 0
                ? monitor.WorkArea.Width
                : ParseResolutionWidth(profile.Resolution, 1920);
            profile.WorkAreaHeight = monitor != null && monitor.WorkArea.Height > 0
                ? monitor.WorkArea.Height
                : ParseResolutionHeight(profile.Resolution, 1080);
            profile.Dpi = profile.Dpi >= 96
                ? profile.Dpi
                : (monitor != null && monitor.Dpi >= 96 ? monitor.Dpi : 96);

            Dictionary<string, IconPositionItem> sourceIcons = icons ?? profile.Icons;
            if (sourceIcons == null || sourceIcons.Count == 0)
            {
                profile.GridOriginX = 0;
                profile.GridOriginY = 0;
                profile.GridSpacingX = Math.Max(48, (int)Math.Round(75.0 * profile.Dpi / 96.0));
                profile.GridSpacingY = profile.GridSpacingX;
                return;
            }

            List<int> xs = new List<int>();
            List<int> ys = new List<int>();
            int minX = int.MaxValue;
            int minY = int.MaxValue;

            foreach (KeyValuePair<string, IconPositionItem> pair in sourceIcons)
            {
                IconPositionItem item = pair.Value;

                if (item == null) continue;
                xs.Add(item.X);
                ys.Add(item.Y);
                if (item.X < minX) minX = item.X;
                if (item.Y < minY) minY = item.Y;
            }

            if (xs.Count == 0)
            {
                profile.GridOriginX = 0;
                profile.GridOriginY = 0;
                profile.GridSpacingX = Math.Max(48, (int)Math.Round(75.0 * profile.Dpi / 96.0));
                profile.GridSpacingY = profile.GridSpacingX;
                return;
            }

            profile.GridOriginX = Math.Max(0, minX);
            profile.GridOriginY = Math.Max(0, minY);
            profile.GridSpacingX = InferGridSpacing(
                xs,
                Math.Max(48, (int)Math.Round(75.0 * profile.Dpi / 96.0)));
            profile.GridSpacingY = InferGridSpacing(
                ys,
                Math.Max(48, (int)Math.Round(96.0 * profile.Dpi / 96.0)));

        }

        public static ProfileGeometryCompatibility CheckRuntimeGeometryCompatibility(
            DesktopProfile savedProfile,
            DesktopProfile runtimeGeometry)
        {
            ProfileGeometryCompatibility result = new ProfileGeometryCompatibility();
            result.IsCompatible = false;

            if (savedProfile == null || runtimeGeometry == null)
            {
                result.Reason = "保存Profile或当前桌面网格为空，不能安全按绝对坐标恢复";
                return result;
            }

            if (savedProfile.WorkAreaWidth <= 0 ||
                savedProfile.WorkAreaHeight <= 0 ||
                savedProfile.GridSpacingX <= 0 ||
                savedProfile.GridSpacingY <= 0 ||
                runtimeGeometry.WorkAreaWidth <= 0 ||
                runtimeGeometry.WorkAreaHeight <= 0 ||
                runtimeGeometry.GridSpacingX <= 0 ||
                runtimeGeometry.GridSpacingY <= 0)
            {
                result.Reason = "保存Profile或当前桌面缺少工作区/网格信息，不能确认精确坐标仍然有效";
                return result;
            }

            int workAreaTolerance = 24;
            int gridXTolerance = Math.Max(4, savedProfile.GridSpacingX / 20);
            int gridYTolerance = Math.Max(4, savedProfile.GridSpacingY / 20);
            int originTolerance = 12;

            if (Math.Abs(savedProfile.WorkAreaWidth - runtimeGeometry.WorkAreaWidth) > workAreaTolerance ||
                Math.Abs(savedProfile.WorkAreaHeight - runtimeGeometry.WorkAreaHeight) > workAreaTolerance)
            {
                result.Reason = string.Format(
                    "工作区不一致：保存={0}x{1}，当前={2}x{3}",
                    savedProfile.WorkAreaWidth,
                    savedProfile.WorkAreaHeight,
                    runtimeGeometry.WorkAreaWidth,
                    runtimeGeometry.WorkAreaHeight);
                return result;
            }

            if (Math.Abs(savedProfile.GridSpacingX - runtimeGeometry.GridSpacingX) > gridXTolerance ||
                Math.Abs(savedProfile.GridSpacingY - runtimeGeometry.GridSpacingY) > gridYTolerance)
            {
                result.Reason = string.Format(
                    "桌面网格步进不一致：保存={0}x{1}，当前={2}x{3}",
                    savedProfile.GridSpacingX,
                    savedProfile.GridSpacingY,
                    runtimeGeometry.GridSpacingX,
                    runtimeGeometry.GridSpacingY);
                return result;
            }

            if (Math.Abs(savedProfile.GridOriginX - runtimeGeometry.GridOriginX) > originTolerance ||
                Math.Abs(savedProfile.GridOriginY - runtimeGeometry.GridOriginY) > originTolerance)
            {
                result.Reason = string.Format(
                    "桌面网格原点不一致：保存=({0},{1})，当前=({2},{3})",
                    savedProfile.GridOriginX,
                    savedProfile.GridOriginY,
                    runtimeGeometry.GridOriginX,
                    runtimeGeometry.GridOriginY);
                return result;
            }

            result.IsCompatible = true;
            result.Reason = string.Format(
                "工作区和网格兼容：工作区={0}x{1}，网格={2}x{3}",
                runtimeGeometry.WorkAreaWidth,
                runtimeGeometry.WorkAreaHeight,
                runtimeGeometry.GridSpacingX,
                runtimeGeometry.GridSpacingY);
            return result;
        }

        public static Dictionary<string, IconPositionItem> Map(
            DesktopProfile baseProfile,
            MonitorProfileInfo targetMonitor)
        {
            return Map(baseProfile, targetMonitor, null);
        }

        public static Dictionary<string, IconPositionItem> Map(
            DesktopProfile baseProfile,
            MonitorProfileInfo targetMonitor,
            DesktopProfile runtimeGeometry)
        {
            Dictionary<string, IconPositionItem> mapped =
                new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);

            if (baseProfile == null || baseProfile.Icons == null || baseProfile.Icons.Count == 0)
            {
                return mapped;
            }

            if (targetMonitor == null)
            {
                return mapped;
            }

            if (baseProfile.WorkAreaWidth <= 0 ||
                baseProfile.WorkAreaHeight <= 0 ||
                baseProfile.GridSpacingX <= 0 ||
                baseProfile.GridSpacingY <= 0)
            {

                DesktopProfile resolved = LayoutStore.CloneProfile(baseProfile);
                PopulateProfileGeometry(resolved, null, resolved.Icons);
                baseProfile = resolved;
            }

            int baseDpi = Math.Max(96, baseProfile.Dpi);
            int targetDpi = Math.Max(96, targetMonitor.Dpi);
            int targetWorkWidth = targetMonitor.WorkArea.Width > 0
                ? targetMonitor.WorkArea.Width
                : targetMonitor.Width;
            int targetWorkHeight = targetMonitor.WorkArea.Height > 0
                ? targetMonitor.WorkArea.Height
                : targetMonitor.Height;
            bool hasRuntimeGeometry = runtimeGeometry != null &&
                runtimeGeometry.WorkAreaWidth > 0 &&
                runtimeGeometry.WorkAreaHeight > 0 &&
                runtimeGeometry.GridSpacingX > 0 &&
                runtimeGeometry.GridSpacingY > 0;

            if (hasRuntimeGeometry)
            {
                targetWorkWidth = runtimeGeometry.WorkAreaWidth;
                targetWorkHeight = runtimeGeometry.WorkAreaHeight;
            }

            int targetGridX = hasRuntimeGeometry
                ? runtimeGeometry.GridSpacingX
                : Math.Max(
                    48,
                    (int)Math.Round(baseProfile.GridSpacingX * targetDpi / (double)baseDpi));
            int targetGridY = hasRuntimeGeometry
                ? runtimeGeometry.GridSpacingY
                : Math.Max(
                    48,
                    (int)Math.Round(baseProfile.GridSpacingY * targetDpi / (double)baseDpi));
            int targetOriginX = hasRuntimeGeometry
                ? Math.Max(0, runtimeGeometry.GridOriginX)
                : Math.Max(
                    0,
                    (int)Math.Round(baseProfile.GridOriginX * targetDpi / (double)baseDpi));
            int targetOriginY = hasRuntimeGeometry
                ? Math.Max(0, runtimeGeometry.GridOriginY)
                : Math.Max(
                    0,
                    (int)Math.Round(baseProfile.GridOriginY * targetDpi / (double)baseDpi));

            int baseMaxColumn = CalculateLastGridIndex(
                baseProfile.WorkAreaWidth,
                baseProfile.GridOriginX,
                baseProfile.GridSpacingX);
            int baseMaxRow = CalculateLastGridIndex(
                baseProfile.WorkAreaHeight,
                baseProfile.GridOriginY,
                baseProfile.GridSpacingY);
            int targetMaxColumn = CalculateLastGridIndex(
                targetWorkWidth,
                targetOriginX,
                targetGridX);
            int targetMaxRow = CalculateLastGridIndex(
                targetWorkHeight,
                targetOriginY,
                targetGridY);

            List<PlacementCandidate> candidates = new List<PlacementCandidate>();
            foreach (KeyValuePair<string, IconPositionItem> pair in baseProfile.Icons)
            {
                IconPositionItem source = pair.Value;
                if (source == null) continue;

                double baseColumn = (source.X - baseProfile.GridOriginX) /
                    (double)Math.Max(1, baseProfile.GridSpacingX);
                double baseRow = (source.Y - baseProfile.GridOriginY) /
                    (double)Math.Max(1, baseProfile.GridSpacingY);

                PlacementCandidate candidate = new PlacementCandidate();
                candidate.Source = source;
                candidate.DesiredColumn = baseMaxColumn > 0
                    ? baseColumn * targetMaxColumn / baseMaxColumn
                    : 0;
                candidate.DesiredRow = baseMaxRow > 0
                    ? baseRow * targetMaxRow / baseMaxRow
                    : 0;
                candidates.Add(candidate);
            }

            candidates.Sort(delegate(PlacementCandidate left, PlacementCandidate right)
            {
                int rowCompare = left.DesiredRow.CompareTo(right.DesiredRow);
                if (rowCompare != 0) return rowCompare;
                return left.DesiredColumn.CompareTo(right.DesiredColumn);
            });

            HashSet<int> occupied = new HashSet<int>();
            int collisionCount = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                PlacementCandidate candidate = candidates[i];
                int preferredColumn = Clamp(
                    (int)Math.Round(candidate.DesiredColumn),
                    0,
                    targetMaxColumn);
                int preferredRow = Clamp(
                    (int)Math.Round(candidate.DesiredRow),
                    0,
                    targetMaxRow);

                int placedColumn;
                int placedRow;
                FindNearestFreeCell(
                    preferredColumn,
                    preferredRow,
                    candidate.DesiredColumn,
                    candidate.DesiredRow,
                    targetMaxColumn,
                    targetMaxRow,
                    occupied,
                    out placedColumn,
                    out placedRow);

                if (placedColumn != preferredColumn || placedRow != preferredRow)
                {
                    collisionCount++;
                }

                occupied.Add(CellKey(placedColumn, placedRow));

                int x = targetOriginX + placedColumn * targetGridX;
                int y = targetOriginY + placedRow * targetGridY;
                x = Clamp(x, 0, Math.Max(0, targetWorkWidth - targetGridX));
                y = Clamp(y, 0, Math.Max(0, targetWorkHeight - targetGridY));

                IconPositionItem mappedItem = new IconPositionItem();
                mappedItem.Key = candidate.Source.Key;
                mappedItem.DisplayName = candidate.Source.DisplayName;
                mappedItem.X = x;
                mappedItem.Y = y;
                mapped[mappedItem.Key] = mappedItem;

            }

            return mapped;
        }

        private static void FindNearestFreeCell(
            int preferredColumn,
            int preferredRow,
            double desiredColumn,
            double desiredRow,
            int maxColumn,
            int maxRow,
            HashSet<int> occupied,
            out int placedColumn,
            out int placedRow)
        {
            double bestScore = double.MaxValue;
            int bestColumn = preferredColumn;
            int bestRow = preferredRow;

            for (int row = 0; row <= maxRow; row++)
            {
                for (int column = 0; column <= maxColumn; column++)
                {
                    if (occupied.Contains(CellKey(column, row))) continue;

                    double horizontalDistance = Math.Abs(column - desiredColumn);
                    double verticalDistance = Math.Abs(row - desiredRow);

                    double rowChangePenalty = (maxColumn + 1) * 2.0;
                    double score = horizontalDistance + verticalDistance * rowChangePenalty;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestColumn = column;
                        bestRow = row;
                    }
                }
            }

            placedColumn = bestColumn;
            placedRow = bestRow;
        }

        private static int InferGridSpacing(List<int> coordinates, int fallback)
        {
            if (coordinates == null || coordinates.Count < 2) return fallback;

            coordinates.Sort();
            List<int> unique = new List<int>();
            for (int i = 0; i < coordinates.Count; i++)
            {
                if (i == 0 || coordinates[i] != coordinates[i - 1])
                {
                    unique.Add(coordinates[i]);
                }
            }

            Dictionary<int, int> frequency = new Dictionary<int, int>();
            for (int i = 1; i < unique.Count; i++)
            {
                int difference = unique[i] - unique[i - 1];
                if (difference < 32 || difference > 512) continue;

                int count;
                frequency.TryGetValue(difference, out count);
                frequency[difference] = count + 1;
            }

            int bestSpacing = fallback;
            int bestFrequency = 0;
            foreach (KeyValuePair<int, int> pair in frequency)
            {
                if (pair.Value > bestFrequency ||
                    (pair.Value == bestFrequency && pair.Key < bestSpacing))
                {
                    bestSpacing = pair.Key;
                    bestFrequency = pair.Value;
                }
            }

            return bestFrequency > 0 ? bestSpacing : fallback;
        }

        private static int CalculateLastGridIndex(int workSize, int origin, int spacing)
        {
            if (spacing <= 0) return 0;
            int availableTopLeftSpan = Math.Max(0, workSize - origin - spacing);
            return Math.Max(0, availableTopLeftSpan / spacing);
        }

        private static int ParseResolutionWidth(string resolution, int fallback)
        {
            int width = fallback;
            int height = 1080;
            ParseResolution(resolution, ref width, ref height);
            return width;
        }

        private static int ParseResolutionHeight(string resolution, int fallback)
        {
            int width = 1920;
            int height = fallback;
            ParseResolution(resolution, ref width, ref height);
            return height;
        }

        private static void ParseResolution(string resolution, ref int width, ref int height)
        {
            if (string.IsNullOrEmpty(resolution)) return;
            string[] parts = resolution.ToLower().Split(
                new char[] { 'x', '*', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            int parsedWidth;
            int parsedHeight;
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out parsedWidth) &&
                int.TryParse(parts[1], out parsedHeight))
            {
                width = parsedWidth;
                height = parsedHeight;
            }
        }

        private static int CellKey(int column, int row)
        {
            return (row << 16) | (column & 0xFFFF);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }
}
