using System;
using System.Collections.Generic;
using System.IO;
using DesktopIconLock.Common;
using DesktopIconLock.Core;
using DesktopIconLock.Native;

namespace DesktopIconLock.Tests
{
    class TestRunner
    {
        static int passed;
        static int total;

        static void Main(string[] args)
        {
            DisplayInfo.EnablePerMonitorDpiAwareness();
            Console.WriteLine("DesktopIconLock v3 桌面网格稳定性测试");
            Run("真实物理显示信息", TestDisplayInfo);
            Run("当前布局网格分析", TestGeometryAnalysis);
            Run("同配置映射恒等性", TestIdentityMapping);
            Run("跨分辨率与DPI无重叠映射", TestCrossModeMapping);
            Run("精确Profile严格区分DPI", TestStrictExactProfile);
            Run("精确Profile保存保留不同DPI", TestExactProfileKeepsDpiVariants);
            Run("运行时网格兼容性检查", TestRuntimeGridCompatibility);
            Run("运行时实测网格映射", TestRuntimeGridMapping);
            Run("桌面拖动事件分类", TestDesktopLocationEventClassification);
            Run("v3配置独立目录持久化", TestStorePersistence);
            Run("解锁布局变更检测", TestDesktopLayoutChangeDetection);
            Run("历史布局同模式最多5条且基准不被轮转删除", TestHistoryRetention);
            Run("历史布局截图、基准和删除", TestHistoryOperations);
            Console.WriteLine(string.Format("RESULT {0}/{1}", passed, total));
            Environment.ExitCode = passed == total ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            total++;
            try { test(); passed++; Console.WriteLine("[PASS] " + name); }
            catch(Exception ex) { Console.WriteLine("[FAIL] " + name + ": " + ex.Message); }
        }

        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static void TestDisplayInfo()
        {
            MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
            Assert(monitor.Width >= 800 && monitor.Height >= 600, "物理分辨率异常");
            Assert(monitor.Dpi >= 96, "DPI异常");
            Assert(monitor.WorkArea.Width > 0 && monitor.WorkArea.Height > 0, "工作区异常");
            Assert(!string.IsNullOrEmpty(monitor.MonitorFingerprint), "显示器指纹为空");
            Console.WriteLine("  " + monitor);
        }

        static DesktopProfile BuildCurrentProfile(out MonitorProfileInfo monitor)
        {
            Dictionary<string, IconPositionItem> icons = IconAccessor.ReadCurrentIconPositions();
            Assert(icons.Count >= 10, "未读取到足够的真实桌面图标");
            monitor = DisplayInfo.GetPrimaryMonitor();
            DesktopProfile profile = new DesktopProfile();
            profile.Resolution = monitor.ResolutionKey;
            profile.Dpi = monitor.Dpi;
            profile.MonitorFingerprint = monitor.MonitorFingerprint;
            profile.Icons = icons;
            AdaptiveMapper.PopulateProfileGeometry(profile, monitor, icons);
            return profile;
        }

        static void TestGeometryAnalysis()
        {
            MonitorProfileInfo monitor;
            DesktopProfile profile = BuildCurrentProfile(out monitor);
            Assert(profile.GridSpacingX >= 48, "X网格过小");
            Assert(profile.GridSpacingY >= 48, "Y网格过小");
            Assert(profile.WorkAreaWidth == monitor.WorkArea.Width, "工作区宽度未保存");
            Assert(profile.WorkAreaHeight == monitor.WorkArea.Height, "工作区高度未保存");
            Console.WriteLine(string.Format("  origin=({0},{1}) grid={2}x{3}", profile.GridOriginX, profile.GridOriginY, profile.GridSpacingX, profile.GridSpacingY));
        }

        static void TestIdentityMapping()
        {
            MonitorProfileInfo monitor;
            DesktopProfile profile = BuildCurrentProfile(out monitor);
            Dictionary<string, IconPositionItem> mapped = AdaptiveMapper.Map(profile, monitor);
            Assert(mapped.Count == profile.Icons.Count, "映射图标数变化");
            foreach(KeyValuePair<string,IconPositionItem> pair in profile.Icons)
            {
                IconPositionItem target = mapped[pair.Key];
                Assert(target.X == pair.Value.X && target.Y == pair.Value.Y, "同配置映射不恒等: " + pair.Key);
            }
        }

        static void TestCrossModeMapping()
        {
            MonitorProfileInfo current;
            DesktopProfile profile = BuildCurrentProfile(out current);
            MonitorProfileInfo target = new MonitorProfileInfo();
            target.Width = 1600; target.Height = 900; target.Dpi = 96;
            target.MonitorFingerprint = current.MonitorFingerprint;
            target.Bounds = new User32.RECT { Left=0, Top=0, Right=1600, Bottom=900 };
            target.WorkArea = new User32.RECT { Left=0, Top=0, Right=1600, Bottom=860 };
            Dictionary<string, IconPositionItem> mapped = AdaptiveMapper.Map(profile, target);
            HashSet<string> cells = new HashSet<string>();
            foreach(KeyValuePair<string,IconPositionItem> pair in mapped)
            {
                IconPositionItem item = pair.Value;
                Assert(item.X >= 0 && item.Y >= 0 && item.X < target.WorkArea.Width && item.Y < target.WorkArea.Height, "图标越界: " + pair.Key);
                Assert(cells.Add(item.X + ":" + item.Y), "图标重叠: " + pair.Key);
            }
            Assert(mapped["icolock"].X < mapped["ScreenToGif.exe"].X, "左侧与中部组顺序破坏");
            Assert(mapped["ScreenToGif.exe"].X < mapped["FanControl"].X, "中部与右侧组顺序破坏");
        }

        static void TestStrictExactProfile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DesktopIconLockTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                LayoutStore store = new LayoutStore(dir);
                MonitorProfileInfo monitor;
                DesktopProfile profile = BuildCurrentProfile(out monitor);
                store.SaveExactProfile(monitor, profile.Icons);
                Assert(store.FindExactProfile(monitor.ResolutionKey, monitor.Dpi, monitor.MonitorFingerprint) != null, "严格Profile未命中");
                Assert(store.FindExactProfile(monitor.ResolutionKey, monitor.Dpi + 24, monitor.MonitorFingerprint) == null, "不同DPI被错误命中");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestExactProfileKeepsDpiVariants()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DesktopIconLockTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                LayoutStore store = new LayoutStore(dir);
                MonitorProfileInfo monitor;
                DesktopProfile profile = BuildCurrentProfile(out monitor);
                store.SaveExactProfile(monitor, profile.Icons);

                MonitorProfileInfo alternateDpi = new MonitorProfileInfo();
                alternateDpi.Width = monitor.Width;
                alternateDpi.Height = monitor.Height;
                alternateDpi.Dpi = monitor.Dpi + 24;
                alternateDpi.MonitorFingerprint = monitor.MonitorFingerprint;
                alternateDpi.Bounds = monitor.Bounds;
                alternateDpi.WorkArea = monitor.WorkArea;
                store.SaveExactProfile(alternateDpi, profile.Icons);

                Assert(
                    store.FindExactProfile(
                        monitor.ResolutionKey,
                        monitor.Dpi,
                        monitor.MonitorFingerprint) != null,
                    "保存另一套DPI时误删除了原精确Profile");
                Assert(
                    store.FindExactProfile(
                        alternateDpi.ResolutionKey,
                        alternateDpi.Dpi,
                        alternateDpi.MonitorFingerprint) != null,
                    "另一套DPI精确Profile未保存");
                int retainedVariants = store.CurrentConfig.ExactProfiles.FindAll(
                    delegate(DesktopProfile item)
                    {
                        return string.Equals(
                                   item.Resolution,
                                   monitor.ResolutionKey,
                                   StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(
                                   item.MonitorFingerprint,
                                   monitor.MonitorFingerprint,
                                   StringComparison.OrdinalIgnoreCase) &&
                               (item.Dpi == monitor.Dpi ||
                                item.Dpi == alternateDpi.Dpi);
                    }).Count;
                Assert(retainedVariants == 2, "不同DPI未分别保留两套精确Profile");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestRuntimeGridCompatibility()
        {
            DesktopProfile saved = BuildSyntheticProfile(131, 171);
            DesktopProfile sameRuntime = BuildSyntheticProfile(131, 171);
            ProfileGeometryCompatibility same =
                AdaptiveMapper.CheckRuntimeGeometryCompatibility(saved, sameRuntime);
            Assert(same.IsCompatible, "完全相同的运行时网格被错误判定为不兼容");

            DesktopProfile changedRuntime = BuildSyntheticProfile(131, 201);
            ProfileGeometryCompatibility changed =
                AdaptiveMapper.CheckRuntimeGeometryCompatibility(saved, changedRuntime);
            Assert(!changed.IsCompatible, "171与201的垂直网格差异未被识别");
            Assert(changed.Reason.Contains("网格步进不一致"), "网格不兼容原因不清晰");
        }

        static void TestRuntimeGridMapping()
        {
            DesktopProfile source = BuildSyntheticProfile(131, 171);
            MonitorProfileInfo target = new MonitorProfileInfo();
            target.Width = 3840;
            target.Height = 2160;
            target.Dpi = 168;
            target.MonitorFingerprint = "PHYSICAL_TEST";
            target.Bounds = new User32.RECT { Left=0, Top=0, Right=3840, Bottom=2160 };
            target.WorkArea = new User32.RECT { Left=0, Top=0, Right=3840, Bottom=2076 };

            DesktopProfile runtime = BuildSyntheticProfile(131, 201);
            Dictionary<string, IconPositionItem> mapped =
                AdaptiveMapper.Map(source, target, runtime);

            Assert(mapped["top"].X == 23 && mapped["top"].Y == 2, "顶部图标未保留当前网格原点");
            Assert(mapped["row1"].Y == 203, "第1行未映射到当前201步进网格");
            Assert(mapped["row2"].Y == 404, "第2行未映射到当前201步进网格");
            Assert(mapped["bottom"].Y == 1811, "底部图标未压缩到当前工作区可用的最后网格行");
            Assert(
                mapped["bottom"].X == 3298,
                "运行时网格映射错误把底部图标挤到其它列");
        }

        static DesktopProfile BuildSyntheticProfile(int gridX, int gridY)
        {
            DesktopProfile profile = new DesktopProfile();
            profile.Resolution = "3840x2160";
            profile.Dpi = 168;
            profile.MonitorFingerprint = "PHYSICAL_TEST";
            profile.WorkAreaWidth = 3840;
            profile.WorkAreaHeight = 2076;
            profile.GridOriginX = 23;
            profile.GridOriginY = 2;
            profile.GridSpacingX = gridX;
            profile.GridSpacingY = gridY;
            profile.Icons["top"] = BuildIcon("top", 23, 2);
            profile.Icons["row1"] = BuildIcon("row1", 23, 2 + gridY);
            profile.Icons["row2"] = BuildIcon("row2", 23, 2 + gridY * 2);
            profile.Icons["bottom"] = BuildIcon("bottom", 3298, 2 + gridY * 11);
            return profile;
        }

        static void TestDesktopLocationEventClassification()
        {
            IntPtr desktop = new IntPtr(0x1234);

            DesktopLocationEventDecision ordinaryCursor =
                DesktopLocationEventClassifier.Evaluate(
                    desktop,
                    IntPtr.Zero,
                    User32.OBJID_CURSOR,
                    0,
                    false,
                    false);
            Assert(
                !ordinaryCursor.ShouldQueuePositionCheck,
                "普通鼠标移动被错误识别为桌面图标拖动");

            DesktopLocationEventDecision dragCursor =
                DesktopLocationEventClassifier.Evaluate(
                    desktop,
                    IntPtr.Zero,
                    User32.OBJID_CURSOR,
                    0,
                    true,
                    false);
            Assert(
                dragCursor.ShouldQueuePositionCheck && dragCursor.MarksMouseDrag,
                "按住左键的光标事件未建立拖动检查");

            DesktopLocationEventDecision dragReleased =
                DesktopLocationEventClassifier.Evaluate(
                    desktop,
                    IntPtr.Zero,
                    User32.OBJID_CURSOR,
                    0,
                    false,
                    true);
            Assert(
                dragReleased.ShouldQueuePositionCheck,
                "已观察到拖动后释放鼠标时未安排坐标验收");

            DesktopLocationEventDecision directIcon =
                DesktopLocationEventClassifier.Evaluate(
                    desktop,
                    desktop,
                    User32.OBJID_CLIENT,
                    3,
                    false,
                    false);
            Assert(
                directIcon.ShouldQueuePositionCheck,
                "桌面ListView具体图标事件被错误忽略");

            DesktopLocationEventDecision listViewWindowNoise =
                DesktopLocationEventClassifier.Evaluate(
                    desktop,
                    desktop,
                    User32.OBJID_CLIENT,
                    0,
                    false,
                    false);
            Assert(
                !listViewWindowNoise.ShouldQueuePositionCheck,
                "ListView自身重绘事件被错误识别为具体图标移动");
        }

        static void TestStorePersistence()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DesktopIconLockTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                MonitorProfileInfo monitor;
                DesktopProfile profile = BuildCurrentProfile(out monitor);
                LayoutStore store = new LayoutStore(dir);
                store.SaveBaseProfile(monitor, profile.Icons);
                LayoutStore reloaded = new LayoutStore(dir);
                Assert(reloaded.CurrentConfig.Version == 3, "配置版本不是3");
                Assert(reloaded.CurrentConfig.BaseProfile.GridSpacingX > 0, "网格X未持久化");
                Assert(reloaded.CurrentConfig.BaseProfile.GridSpacingY > 0, "网格Y未持久化");
                Assert(reloaded.CurrentConfig.BaseProfile.Icons.Count == profile.Icons.Count, "图标数量持久化不一致");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestDesktopLayoutChangeDetection()
        {
            Dictionary<string, IconPositionItem> baseline =
                new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);
            baseline["A"] = BuildIcon("A", 10, 20);
            baseline["B"] = BuildIcon("B", 30, 40);

            Dictionary<string, IconPositionItem> unchanged =
                DesktopLayoutChangeDetector.Clone(baseline);
            DesktopLayoutChangeResult unchangedResult =
                DesktopLayoutChangeDetector.Compare(baseline, unchanged);
            Assert(!unchangedResult.HasChanged, "相同布局被错误识别为已变动");
            Assert(
                unchangedResult.MovedIconCount == 0 &&
                unchangedResult.AddedIconCount == 0 &&
                unchangedResult.RemovedIconCount == 0,
                "相同布局产生了错误的变动统计");

            Dictionary<string, IconPositionItem> moved =
                DesktopLayoutChangeDetector.Clone(baseline);
            moved["A"].X = 11;
            DesktopLayoutChangeResult movedResult =
                DesktopLayoutChangeDetector.Compare(baseline, moved);
            Assert(movedResult.HasChanged, "坐标变化未被识别");
            Assert(movedResult.MovedIconCount == 1, "移动图标数量统计错误");

            Dictionary<string, IconPositionItem> iconSetChanged =
                DesktopLayoutChangeDetector.Clone(baseline);
            iconSetChanged.Remove("A");
            iconSetChanged["C"] = BuildIcon("C", 50, 60);
            DesktopLayoutChangeResult setResult =
                DesktopLayoutChangeDetector.Compare(baseline, iconSetChanged);
            Assert(setResult.HasChanged, "新增和删除图标未被识别");
            Assert(setResult.AddedIconCount == 1, "新增图标数量统计错误");
            Assert(setResult.RemovedIconCount == 1, "删除图标数量统计错误");

            DesktopLayoutChangeResult unavailableResult =
                DesktopLayoutChangeDetector.Compare(null, unchanged);
            Assert(unavailableResult.HasChanged, "缺少解锁快照时不应直接视为未变化");
            Assert(!unavailableResult.ComparisonAvailable, "缺少快照时比较状态错误");
        }

        static IconPositionItem BuildIcon(string key, int x, int y)
        {
            IconPositionItem item = new IconPositionItem();
            item.Key = key;
            item.DisplayName = key;
            item.X = x;
            item.Y = y;
            return item;
        }

        static void TestHistoryRetention()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DesktopIconLockHistoryTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "history\\records"));
                Directory.CreateDirectory(Path.Combine(dir, "history\\screenshots"));

                MonitorProfileInfo monitor;
                DesktopProfile current = BuildCurrentProfile(out monitor);
                HistoryStore history = new HistoryStore(dir);
                HistoryLayoutRecord first = history.CreateRecord(monitor, current.Icons);
                history.MarkAsBase(first);

                for (int i = 0; i < 5; i++)
                {
                    history.CreateRecord(monitor, current.Icons);
                }

                List<HistoryLayoutRecord> records = history.GetRecords();
                int sameModeCount = records.FindAll(delegate(HistoryLayoutRecord item)
                {
                    return item.Profile.Resolution == monitor.ResolutionKey &&
                        item.Profile.Dpi == monitor.Dpi;
                }).Count;
                Assert(sameModeCount == 5, "同一分辨率和DPI没有限制为5条");
                Assert(records.Exists(delegate(HistoryLayoutRecord item) { return item.Id == first.Id && item.IsBase; }), "基准记录被自动轮转删除");

                MonitorProfileInfo differentDpi = new MonitorProfileInfo();
                differentDpi.Width = monitor.Width;
                differentDpi.Height = monitor.Height;
                differentDpi.Dpi = monitor.Dpi + 24;
                differentDpi.MonitorFingerprint = monitor.MonitorFingerprint;
                differentDpi.Bounds = monitor.Bounds;
                differentDpi.WorkArea = monitor.WorkArea;
                history.CreateRecord(differentDpi, current.Icons);
                Assert(history.GetRecords().Count >= 6, "不同分辨率/DPI组合未独立保存");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestHistoryOperations()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DesktopIconLockHistoryTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "history\\records"));
                Directory.CreateDirectory(Path.Combine(dir, "history\\screenshots"));

                MonitorProfileInfo monitor;
                DesktopProfile current = BuildCurrentProfile(out monitor);
                HistoryStore history = new HistoryStore(dir);
                LayoutStore store = new LayoutStore(Path.Combine(dir, "config"));
                HistoryLayoutRecord record = history.CreateRecord(monitor, current.Icons);
                Assert(File.Exists(record.ProfilePath), "历史Profile文件不存在");
                Assert(File.Exists(record.ScreenshotPath), "历史全桌面截图不存在");
                Assert(record.MenuText.Contains("年") &&
                    record.MenuText.Contains("分辨率") &&
                    record.MenuText.Contains("缩放") &&
                    record.MenuText.Contains("序号"),
                    "历史菜单行格式不完整");

                history.MarkAsBase(record);
                store.SetBaseProfileFromHistory(record.Profile);
                HistoryLayoutRecord reloaded = history.GetRecords()[0];
                Assert(reloaded.IsBase, "历史基准标记未保存");
                Assert(store.CurrentConfig.BaseProfile.Icons.Count == current.Icons.Count, "基准Profile图标数不一致");

                bool deletedBase;
                Assert(history.DeleteRecord(reloaded, out deletedBase), "历史记录删除失败");
                Assert(deletedBase, "删除基准记录时未识别基准状态");
                Assert(!File.Exists(record.ProfilePath) && !File.Exists(record.ScreenshotPath), "历史文件未完整删除");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
