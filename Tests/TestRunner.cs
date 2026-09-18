using System;
using System.Collections.Generic;
using System.IO;
using KooDesk.Core;
using KooDesk.Native;

namespace KooDesk.Tests
{
    class TestRunner
    {
        static int passed;
        static int total;

        static void Main(string[] args)
        {
            DisplayInfo.EnablePerMonitorDpiAwareness();
            Console.WriteLine("kooDESK v3 桌面网格稳定性测试");
            Run("真实物理显示信息", TestDisplayInfo);
            Run("当前布局网格分析", TestGeometryAnalysis);
            Run("同配置映射恒等性", TestIdentityMapping);
            Run("跨分辨率与DPI无重叠映射", TestCrossModeMapping);
            Run("左右分组拓扑顺序保持", TestStructurePreservedMapping);
            Run("精确Profile严格区分DPI", TestStrictExactProfile);
            Run("精确Profile保存保留不同DPI", TestExactProfileKeepsDpiVariants);
            Run("运行时网格兼容性检查", TestRuntimeGridCompatibility);
            Run("运行时实测网格映射", TestRuntimeGridMapping);
            Run("桌面拖动事件分类", TestDesktopLocationEventClassification);
            Run("v3配置独立目录持久化", TestStorePersistence);
            Run("解锁布局变更检测", TestDesktopLayoutChangeDetection);
            Run("基准与精确配置同时落盘", TestBaseAndExactSavedTogether);
            Run("旧版历史布局目录自动清理", TestLegacyHistoryDirectoryCleanup);
            Run("桌面同名文件图标排版防错乱与匹配", TestDuplicateNameIconHandling);
            Run("基准为null时不误读精确Profile", TestNullBaseProfileParse);
            Run("图标名特殊字符转义往返", TestIconNameEscapingRoundTrip);
            Run("旧版layout.json自动改名为kooDESK.json", TestLegacyPortableConfigMigration);
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
            Assert(icons.Count >= 3, "未读取到足够的真实桌面图标");
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

        static MonitorProfileInfo BuildSyntheticMonitor(int width, int height, int dpi, int workAreaHeight)
        {
            MonitorProfileInfo monitor = new MonitorProfileInfo();
            monitor.DeviceName = "\\.\\TEST";
            monitor.MonitorFingerprint = "MON_TEST_SYNTHETIC";
            monitor.Width = width;
            monitor.Height = height;
            monitor.Dpi = dpi;
            monitor.Bounds = new User32.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
            monitor.WorkArea = new User32.RECT { Left = 0, Top = 0, Right = width, Bottom = workAreaHeight };
            return monitor;
        }

        static void TestStructurePreservedMapping()
        {
            DesktopProfile baseProfile = new DesktopProfile();
            baseProfile.Resolution = "2000x1000";
            baseProfile.Dpi = 96;
            baseProfile.MonitorFingerprint = "MON_TEST_SYNTHETIC";
            baseProfile.WorkAreaWidth = 2000;
            baseProfile.WorkAreaHeight = 1000;
            baseProfile.GridOriginX = 0;
            baseProfile.GridOriginY = 0;
            baseProfile.GridSpacingX = 100;
            baseProfile.GridSpacingY = 100;
            baseProfile.Icons["L"] = BuildIcon("L", 0, 0);
            baseProfile.Icons["M"] = BuildIcon("M", 1000, 100);
            baseProfile.Icons["R"] = BuildIcon("R", 1900, 200);

            MonitorProfileInfo target = BuildSyntheticMonitor(1280, 1024, 96, 984);
            Dictionary<string, IconPositionItem> mapped = AdaptiveMapper.Map(baseProfile, target);

            Assert(mapped.Count == 3, "合成映射图标数不一致");
            Assert(mapped["L"].X == 0, "左侧组列位置改变: " + mapped["L"].X);
            Assert(mapped["M"].X == 600, "中部组列位置改变: " + mapped["M"].X);
            Assert(mapped["R"].X == 1100, "右侧组列位置改变: " + mapped["R"].X);
            Assert(mapped["L"].Y < mapped["M"].Y && mapped["M"].Y < mapped["R"].Y, "行顺序被破坏");
        }

        static void TestNullBaseProfileParse()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KooDeskTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string json =
                    "{\n" +
                    "  \"version\": 3,\n" +
                    "  \"baseProfile\": null,\n" +
                    "  \"exactProfiles\": [\n" +
                    "    {\n" +
                    "      \"resolution\": \"1920x1080\",\n" +
                    "      \"dpi\": 96,\n" +
                    "      \"monitorFingerprint\": \"MON_TEST\",\n" +
                    "      \"workAreaWidth\": 1920,\n" +
                    "      \"workAreaHeight\": 1040,\n" +
                    "      \"gridOriginX\": 7,\n" +
                    "      \"gridOriginY\": 7,\n" +
                    "      \"gridSpacingX\": 75,\n" +
                    "      \"gridSpacingY\": 96,\n" +
                    "      \"icons\": { \"demo\": { \"x\": 7, \"y\": 103 } }\n" +
                    "    }\n" +
                    "  ]\n" +
                    "}";
                File.WriteAllText(Path.Combine(dir, LayoutStore.ConfigFileName), json, System.Text.Encoding.UTF8);

                LayoutStore store = new LayoutStore(dir);
                Assert(store.CurrentConfig.BaseProfile == null, "baseProfile为null时被误读为第一个精确Profile");
                Assert(store.CurrentConfig.ExactProfiles.Count == 1, "精确Profile数量解析错误");
                Assert(store.CurrentConfig.Version == 3, "配置版本号未读取");
                Assert(store.CurrentConfig.ExactProfiles[0].Icons.ContainsKey("demo"), "精确Profile图标解析丢失");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestIconNameEscapingRoundTrip()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KooDeskTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                string[] trickyNames = new string[]
                {
                    "say\"hi\"",
                    "path\\to\\file",
                    "新建文件夹",
                    "tab\there",
                    "emoji\ud83d\udcc1"
                };

                Dictionary<string, IconPositionItem> icons = new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < trickyNames.Length; i++)
                {
                    icons[trickyNames[i]] = BuildIcon(trickyNames[i], 7 + i * 75, 7 + i * 96);
                }

                MonitorProfileInfo monitor = BuildSyntheticMonitor(1920, 1080, 96, 1040);
                LayoutStore store = new LayoutStore(dir);
                Assert(store.SaveExactProfile(monitor, icons), "含特殊字符的布局未能落盘");

                LayoutStore reloaded = new LayoutStore(dir);
                DesktopProfile exact = reloaded.FindExactProfile(monitor.ResolutionKey, monitor.Dpi, monitor.MonitorFingerprint);
                Assert(exact != null, "重新加载后未命中精确Profile");
                for (int i = 0; i < trickyNames.Length; i++)
                {
                    IconPositionItem item;
                    Assert(exact.Icons.TryGetValue(trickyNames[i], out item), "图标名解析丢失: " + trickyNames[i]);
                    Assert(item.X == 7 + i * 75 && item.Y == 7 + i * 96, "图标坐标往返不一致: " + trickyNames[i]);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
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

            Assert(mapped.Count == profile.Icons.Count, "跨模式映射图标数不一致");
        }

        static void TestStrictExactProfile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KooDeskTest_" + Guid.NewGuid().ToString("N"));
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
            string dir = Path.Combine(Path.GetTempPath(), "KooDeskTest_" + Guid.NewGuid().ToString("N"));
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

            DesktopLocationEventDecision selfChild =
                DesktopLocationEventClassifier.Evaluate(
                    desktop,
                    desktop,
                    User32.OBJID_CLIENT,
                    0,
                    false,
                    false);
            Assert(
                selfChild.ShouldQueuePositionCheck,
                "idChild=0的桌面ListView位置事件被错误忽略");

            DesktopLocationEventDecision childIdSelf =
                DesktopLocationEventClassifier.Evaluate(
                    desktop,
                    desktop,
                    User32.OBJID_WINDOW,
                    -1,
                    false,
                    false);
            Assert(
                childIdSelf.ShouldQueuePositionCheck,
                "CHILDID_SELF形式的桌面位置事件被错误忽略");

            DesktopLocationEventDecision foreignWindow =
                DesktopLocationEventClassifier.Evaluate(
                    desktop,
                    new IntPtr(0x9999),
                    User32.OBJID_CLIENT,
                    3,
                    false,
                    false);
            Assert(
                !foreignWindow.ShouldQueuePositionCheck,
                "非桌面窗口的位置事件被错误识别为图标移动");
        }

        static void TestStorePersistence()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KooDeskTest_" + Guid.NewGuid().ToString("N"));
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

        static void TestDuplicateNameIconHandling()
        {
            Dictionary<string, IconPositionItem> targetPositions =
                new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);
            targetPositions["新建文件夹#1"] = BuildIcon("新建文件夹#1", 50, 100);
            targetPositions["新建文件夹#1"].DisplayName = "新建文件夹";
            targetPositions["新建文件夹#2"] = BuildIcon("新建文件夹#2", 300, 400);
            targetPositions["新建文件夹#2"].DisplayName = "新建文件夹";
            targetPositions["此电脑"] = BuildIcon("此电脑", 50, 50);

            List<KeyValuePair<int, IconPositionItem>> desktopItems =
                new List<KeyValuePair<int, IconPositionItem>>
                {
                    new KeyValuePair<int, IconPositionItem>(0, new IconPositionItem { Key = "新建文件夹", DisplayName = "新建文件夹", X = 52, Y = 98 }),
                    new KeyValuePair<int, IconPositionItem>(1, new IconPositionItem { Key = "此电脑", DisplayName = "此电脑", X = 50, Y = 50 }),
                    new KeyValuePair<int, IconPositionItem>(2, new IconPositionItem { Key = "新建文件夹", DisplayName = "新建文件夹", X = 305, Y = 395 })
                };

            Dictionary<int, IconPositionItem> matched =
                IconAccessor.MatchDesktopItemsToTargets(desktopItems, targetPositions);
            Assert(matched.Count == 3, "同名图标匹配数量不为3");
            Assert(matched[0].Key == "新建文件夹#1" && matched[0].X == 50 && matched[0].Y == 100, "0号位未匹配到新建文件夹#1目标坐标");
            Assert(matched[2].Key == "新建文件夹#2" && matched[2].X == 300 && matched[2].Y == 400, "2号位未匹配到新建文件夹#2目标坐标");
            Assert(matched[1].Key == "此电脑" && matched[1].X == 50 && matched[1].Y == 50, "1号位未匹配到此电脑");

            Dictionary<string, IconPositionItem> currentPositions =
                new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);
            currentPositions["新建文件夹#1"] = BuildIcon("新建文件夹#1", 50, 100);
            currentPositions["新建文件夹#1"].DisplayName = "新建文件夹";
            currentPositions["新建文件夹#2"] = BuildIcon("新建文件夹#2", 300, 400);
            currentPositions["新建文件夹#2"].DisplayName = "新建文件夹";
            currentPositions["此电脑"] = BuildIcon("此电脑", 50, 50);

            IconPositionComparison cmp = IconAccessor.ComparePositions(targetPositions, currentPositions);
            Assert(cmp.MatchedIconCount == 3, "同名图标坐标比较匹配数不为3");
            Assert(cmp.MismatchCount == 0, "同名图标相同坐标被误判为偏差");

            DesktopLayoutChangeResult changeResult =
                DesktopLayoutChangeDetector.Compare(targetPositions, currentPositions);
            Assert(!changeResult.HasChanged, "同名图标相同布局被误判为变更");

            currentPositions["新建文件夹#2"].X = 350;
            DesktopLayoutChangeResult movedResult =
                DesktopLayoutChangeDetector.Compare(targetPositions, currentPositions);
            Assert(movedResult.HasChanged, "同名图标其中一个移动未被检测到");
            Assert(movedResult.MovedIconCount == 1, "同名图标移动统计不正确");
        }

        static void TestLegacyPortableConfigMigration()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KooDeskMigrationTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                MonitorProfileInfo monitor = BuildSyntheticMonitor(1920, 1080, 96, 1040);
                Dictionary<string, IconPositionItem> icons = new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);
                icons["demo"] = BuildIcon("demo", 7, 103);

                LayoutConfig seed = new LayoutConfig();
                seed.BaseProfile = new DesktopProfile();
                seed.BaseProfile.Resolution = monitor.ResolutionKey;
                seed.BaseProfile.Dpi = monitor.Dpi;
                seed.BaseProfile.MonitorFingerprint = monitor.MonitorFingerprint;
                seed.BaseProfile.Icons = icons;
                File.WriteAllText(
                    Path.Combine(dir, "layout.json"),
                    LayoutStore.SerializeJson(seed),
                    System.Text.Encoding.UTF8);

                LayoutStore store = new LayoutStore(dir);
                Assert(File.Exists(Path.Combine(dir, LayoutStore.ConfigFileName)), "kooDESK.json 未生成");
                Assert(!File.Exists(Path.Combine(dir, "layout.json")), "旧 layout.json 未被改名而仍然残留");
                Assert(store.CurrentConfig.BaseProfile != null &&
                    store.CurrentConfig.BaseProfile.Icons.ContainsKey("demo"), "迁移后布局未能原样读出");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
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

        static void TestBaseAndExactSavedTogether()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KooDeskBaseExactTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                MonitorProfileInfo monitor;
                DesktopProfile current = BuildCurrentProfile(out monitor);
                LayoutStore store = new LayoutStore(dir);
                Assert(store.SaveBaseAndExactProfile(monitor, current.Icons), "基准与精确配置保存失败");

                LayoutStore reloaded = new LayoutStore(dir);
                Assert(reloaded.CurrentConfig.BaseProfile != null, "基准Profile未落盘");
                Assert(
                    reloaded.CurrentConfig.BaseProfile.Icons.Count == current.Icons.Count,
                    "基准Profile图标数不一致");

                DesktopProfile exact = reloaded.CurrentConfig.ExactProfiles.Find(
                    delegate(DesktopProfile item)
                    {
                        return item.Resolution == monitor.ResolutionKey && item.Dpi == monitor.Dpi;
                    });
                Assert(exact != null, "当前显示环境的精确Profile未同时写入");
                Assert(exact.Icons.Count == current.Icons.Count, "精确Profile图标数不一致");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestLegacyHistoryDirectoryCleanup()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KooDeskHistoryCleanupTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "history\\records"));
                Directory.CreateDirectory(Path.Combine(dir, "history\\screenshots"));
                File.WriteAllText(
                    Path.Combine(dir, "history\\records\\00000001_20260101_000000_abcd1234.history.json"),
                    "{}");
                File.WriteAllText(
                    Path.Combine(dir, "history\\screenshots\\00000001_20260101_000000_abcd1234.png"),
                    "not-a-real-png");

                LayoutStore store = new LayoutStore(dir);
                Assert(!Directory.Exists(Path.Combine(dir, "history")), "旧版history目录未被清理");
                Assert(store.CurrentConfig != null, "清理旧目录后配置应仍能正常加载");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
