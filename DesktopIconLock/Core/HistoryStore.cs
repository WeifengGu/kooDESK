using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DesktopIconLock.Common;
using DesktopIconLock.Native;

namespace DesktopIconLock.Core
{
    public class HistoryLayoutRecord
    {
        public string Id { get; set; }
        public long Sequence { get; set; }
        public DateTime CreatedAt { get; set; }
        public string ProfilePath { get; set; }
        public string ScreenshotPath { get; set; }
        public DesktopProfile Profile { get; set; }
        public bool IsBase { get; set; }

        public int ScalePercent
        {
            get { return (int)Math.Round(Math.Max(96, Profile.Dpi) * 100.0 / 96.0); }
        }

        public string MenuText
        {
            get
            {
                string resolution = (Profile.Resolution ?? "未知")
                    .Replace("X", " x ")
                    .Replace("x", " x ");
                string text = string.Format(
                    "{0} 分辨率{1} 缩放 {2}% 序号{3}",
                    CreatedAt.ToString("yyyy年MM月dd日HH时mm分ss秒"),
                    resolution,
                    ScalePercent,
                    Sequence);
                return IsBase ? text + "（基准）" : text;
            }
        }
    }

    /// <summary>
    /// 每条历史布局由一个Profile JSON和一张全桌面PNG组成。
    /// 相同物理分辨率+DPI最多保留5条，不同组合互不影响。
    /// </summary>
    public class HistoryStore
    {
        private const int MaxRecordsPerMode = 5;
        private readonly object _syncRoot = new object();
        private readonly string _historyDirectory;
        private readonly string _recordsDirectory;
        private readonly string _screenshotsDirectory;
        private readonly string _baseMarkerPath;

        public HistoryStore(string configDirectory)
        {
            if (string.IsNullOrEmpty(configDirectory))
            {
                configDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }

            _historyDirectory = Path.Combine(configDirectory, "history");
            _recordsDirectory = Path.Combine(_historyDirectory, "records");
            _screenshotsDirectory = Path.Combine(_historyDirectory, "screenshots");
            _baseMarkerPath = Path.Combine(_historyDirectory, "base-record.txt");

            Directory.CreateDirectory(_recordsDirectory);
            Directory.CreateDirectory(_screenshotsDirectory);

            // 便携迁移：如果便携 history 目录为空，自动从旧版 %LOCALAPPDATA%\DesktopIconLock\history 迁移
            TryMigrateFromLegacyAppData();
        }

        private void TryMigrateFromLegacyAppData()
        {
            try
            {
                if (Directory.GetFiles(_recordsDirectory, "*.history.json").Length > 0)
                {
                    return;
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string legacyDir = Path.Combine(localAppData, "DesktopIconLock");
                string legacyHistoryDir = Path.Combine(legacyDir, "history");
                string legacyRecordsDir = Path.Combine(legacyHistoryDir, "records");
                string legacyScreenshotsDir = Path.Combine(legacyHistoryDir, "screenshots");
                string legacyBaseMarker = Path.Combine(legacyHistoryDir, "base-record.txt");

                if (!Directory.Exists(legacyHistoryDir)) return;

                if (Directory.Exists(legacyRecordsDir))
                {
                    string[] recordFiles = Directory.GetFiles(legacyRecordsDir, "*.history.json");
                    for (int i = 0; i < recordFiles.Length; i++)
                    {
                        string dest = Path.Combine(_recordsDirectory, Path.GetFileName(recordFiles[i]));
                        File.Copy(recordFiles[i], dest, true);
                    }
                }

                if (Directory.Exists(legacyScreenshotsDir))
                {
                    string[] screenshotFiles = Directory.GetFiles(legacyScreenshotsDir, "*.png");
                    for (int i = 0; i < screenshotFiles.Length; i++)
                    {
                        string dest = Path.Combine(_screenshotsDirectory, Path.GetFileName(screenshotFiles[i]));
                        File.Copy(screenshotFiles[i], dest, true);
                    }
                }

                if (File.Exists(legacyBaseMarker) && !File.Exists(_baseMarkerPath))
                {
                    File.Copy(legacyBaseMarker, _baseMarkerPath, true);
                }

                AuditLogger.Info(
                    "便携历史迁移",
                    string.Format("已将旧版历史记录与截图迁移至程序便携目录 {0}", _historyDirectory),
                    AuditLogger.CurrentTraceId);
            }
            catch (Exception ex)
            {
                AuditLogger.LogException(
                    "便携历史迁移",
                    ex.GetType().Name,
                    ex.Message,
                    "未完成旧版历史记录迁移",
                    true,
                    ex,
                    AuditLogger.CurrentTraceId);
            }
        }

        public HistoryLayoutRecord CreateRecord(
            MonitorProfileInfo monitor,
            Dictionary<string, IconPositionItem> icons)
        {
            return CreateRecord(monitor, icons, AuditLogger.GenerateTraceId());
        }

        public HistoryLayoutRecord CreateRecord(
            MonitorProfileInfo monitor,
            Dictionary<string, IconPositionItem> icons,
            string traceId)
        {
            AuditLogger.LogRequestArrival(
                "HistoryStore.CreateRecord",
                "保存当前布局",
                string.Format(
                    "分辨率={0}, DPI={1}({2}%), 图标数={3}",
                    monitor.ResolutionKey,
                    monitor.Dpi,
                    monitor.ScalePercent,
                    icons.Count),
                "保存布局坐标并生成完整桌面截图历史记录",
                traceId);

            lock (_syncRoot)
            {
                long sequence = GetNextSequence();
                DateTime createdAt = DateTime.Now;
                string id = string.Format(
                    "{0:D8}_{1}_{2}",
                    sequence,
                    createdAt.ToString("yyyyMMdd_HHmmss"),
                    Guid.NewGuid().ToString("N").Substring(0, 8));
                string profilePath = Path.Combine(_recordsDirectory, id + ".history.json");
                string screenshotPath = Path.Combine(_screenshotsDirectory, id + ".png");

                DesktopProfile profile = new DesktopProfile();
                profile.Resolution = monitor.ResolutionKey;
                profile.Dpi = monitor.Dpi;
                profile.MonitorFingerprint = monitor.MonitorFingerprint;
                profile.Icons = new Dictionary<string, IconPositionItem>(
                    icons,
                    StringComparer.OrdinalIgnoreCase);
                AdaptiveMapper.PopulateProfileGeometry(profile, monitor, icons);

                // 等待托盘菜单完成关闭，避免截图中包含菜单本身。
                Thread.Sleep(280);
                ScreenCaptureService.CaptureFullDesktop(screenshotPath, traceId);

                LayoutConfig wrapper = new LayoutConfig();
                wrapper.Version = 2;
                wrapper.BaseProfile = LayoutStore.CloneProfile(profile);
                string temporaryProfilePath = profilePath + ".tmp";
                File.WriteAllText(
                    temporaryProfilePath,
                    LayoutStore.SerializeJson(wrapper),
                    Encoding.UTF8);
                if (File.Exists(profilePath)) File.Delete(profilePath);
                File.Move(temporaryProfilePath, profilePath);

                HistoryLayoutRecord record = new HistoryLayoutRecord();
                record.Id = id;
                record.Sequence = sequence;
                record.CreatedAt = createdAt;
                record.ProfilePath = profilePath;
                record.ScreenshotPath = screenshotPath;
                record.Profile = profile;
                record.IsBase = string.Equals(
                    ReadBaseRecordId(),
                    id,
                    StringComparison.OrdinalIgnoreCase);

                AuditLogger.LogStorageOperation(
                    "新增历史布局",
                    "历史布局Profile和全桌面截图",
                    string.Format(
                        "记录={0}, 分辨率={1}, 缩放={2}%, Profile={3}, 截图={4}",
                        id,
                        monitor.ResolutionKey,
                        monitor.ScalePercent,
                        profilePath,
                        screenshotPath),
                    2,
                    "历史布局保存成功",
                    traceId);

                EnforceRetention(profile.Resolution, profile.Dpi, traceId);

                AuditLogger.LogResponseReturn(
                    "HistoryStore.CreateRecord",
                    200,
                    record.MenuText,
                    0,
                    "成功",
                    "该布局已进入历史布局管理菜单",
                    traceId);
                return record;
            }
        }

        public List<HistoryLayoutRecord> GetRecords()
        {
            lock (_syncRoot)
            {
                string baseId = ReadBaseRecordId();
                List<HistoryLayoutRecord> records = new List<HistoryLayoutRecord>();
                string[] files = Directory.GetFiles(_recordsDirectory, "*.history.json");
                for (int i = 0; i < files.Length; i++)
                {
                    HistoryLayoutRecord record = TryReadRecord(files[i], baseId);
                    if (record != null) records.Add(record);
                }

                records.Sort(delegate(HistoryLayoutRecord left, HistoryLayoutRecord right)
                {
                    return right.Sequence.CompareTo(left.Sequence);
                });
                return records;
            }
        }

        public bool DeleteRecord(HistoryLayoutRecord record, out bool deletedBase)
        {
            return DeleteRecord(
                record,
                out deletedBase,
                AuditLogger.GenerateTraceId());
        }

        private bool DeleteRecord(
            HistoryLayoutRecord record,
            out bool deletedBase,
            string traceId)
        {
            deletedBase = false;
            if (record == null) return false;

            lock (_syncRoot)
            {
                try
                {
                    string baseId = ReadBaseRecordId();
                    deletedBase = string.Equals(
                        baseId,
                        record.Id,
                        StringComparison.OrdinalIgnoreCase);

                    if (File.Exists(record.ProfilePath)) File.Delete(record.ProfilePath);
                    if (File.Exists(record.ScreenshotPath)) File.Delete(record.ScreenshotPath);
                    if (deletedBase && File.Exists(_baseMarkerPath))
                    {
                        File.Delete(_baseMarkerPath);
                    }

                    AuditLogger.LogStorageOperation(
                        "删除历史布局",
                        "历史布局Profile和截图",
                        string.Format("记录={0}, 是否基准={1}", record.Id, deletedBase),
                        2,
                        "删除成功",
                        traceId);
                    return true;
                }
                catch (Exception ex)
                {
                    AuditLogger.LogException(
                        "删除历史布局",
                        ex.GetType().Name,
                        ex.Message,
                        "历史记录可能未完整删除",
                        true,
                        ex,
                        traceId);
                    return false;
                }
            }
        }

        public void MarkAsBase(HistoryLayoutRecord record)
        {
            if (record == null) return;
            string traceId = AuditLogger.GenerateTraceId();
            lock (_syncRoot)
            {
                File.WriteAllText(_baseMarkerPath, record.Id, Encoding.UTF8);
                AuditLogger.LogStorageOperation(
                    "标记历史基准",
                    "base-record.txt",
                    string.Format("记录={0}, {1}", record.Id, record.MenuText),
                    1,
                    "基准标记保存成功",
                    traceId);
            }
        }

        private void EnforceRetention(string resolution, int dpi, string traceId)
        {
            List<HistoryLayoutRecord> matching = GetRecords().FindAll(
                delegate(HistoryLayoutRecord item)
                {
                    return string.Equals(
                        item.Profile.Resolution,
                        resolution,
                        StringComparison.OrdinalIgnoreCase) &&
                        item.Profile.Dpi == dpi;
                });

            if (matching.Count <= MaxRecordsPerMode) return;

            int remainingCount = matching.Count;
            // 从最旧记录开始清理，但自动轮转不得删除用户明确设置的基准。
            for (int i = matching.Count - 1; i >= 0 && remainingCount > MaxRecordsPerMode; i--)
            {
                if (matching[i].IsBase) continue;
                bool deletedBase;
                DeleteRecord(matching[i], out deletedBase, traceId);
                remainingCount--;
                AuditLogger.Info(
                    "历史记录自动轮转",
                    string.Format(
                        "分辨率={0}, DPI={1}, 自动删除最旧记录={2}, 删除的是基准={3}",
                        resolution,
                        dpi,
                        matching[i].Id,
                        deletedBase),
                    traceId);
            }
        }

        private HistoryLayoutRecord TryReadRecord(string profilePath, string baseId)
        {
            try
            {
                string fileName = Path.GetFileName(profilePath);
                string id = fileName.Substring(
                    0,
                    fileName.Length - ".history.json".Length);
                string[] parts = id.Split(new char[] { '_' });
                long sequence;
                if (parts.Length < 4 || !long.TryParse(parts[0], out sequence))
                {
                    return null;
                }

                DateTime createdAt;
                if (!DateTime.TryParseExact(
                    parts[1] + parts[2],
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out createdAt))
                {
                    createdAt = File.GetCreationTime(profilePath);
                }

                LayoutConfig wrapper = LayoutStore.ParseJson(
                    File.ReadAllText(profilePath, Encoding.UTF8));
                if (wrapper.BaseProfile == null) return null;

                HistoryLayoutRecord record = new HistoryLayoutRecord();
                record.Id = id;
                record.Sequence = sequence;
                record.CreatedAt = createdAt;
                record.ProfilePath = profilePath;
                string primaryScreenshot = Path.Combine(_screenshotsDirectory, id + ".png");
                if (File.Exists(primaryScreenshot))
                {
                    record.ScreenshotPath = primaryScreenshot;
                }
                else
                {
                    // 兜底查找旧路径
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string legacyScreenshot = Path.Combine(localAppData, "DesktopIconLock\\history\\screenshots", id + ".png");
                    record.ScreenshotPath = File.Exists(legacyScreenshot) ? legacyScreenshot : primaryScreenshot;
                }
                record.Profile = wrapper.BaseProfile;
                record.IsBase = string.Equals(
                    baseId,
                    id,
                    StringComparison.OrdinalIgnoreCase);
                return record;
            }
            catch (Exception ex)
            {
                AuditLogger.LogException(
                    "读取历史布局",
                    ex.GetType().Name,
                    ex.Message,
                    string.Format("跳过损坏记录={0}", profilePath),
                    true,
                    ex,
                    AuditLogger.CurrentTraceId);
                return null;
            }
        }

        private long GetNextSequence()
        {
            long maximum = 0;
            string[] files = Directory.GetFiles(_recordsDirectory, "*.history.json");
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                int separator = name.IndexOf('_');
                long sequence;
                if (separator > 0 &&
                    long.TryParse(name.Substring(0, separator), out sequence) &&
                    sequence > maximum)
                {
                    maximum = sequence;
                }
            }
            return maximum + 1;
        }

        private string ReadBaseRecordId()
        {
            try
            {
                return File.Exists(_baseMarkerPath)
                    ? File.ReadAllText(_baseMarkerPath, Encoding.UTF8).Trim()
                    : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
