using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using KooDesk.Native;

namespace KooDesk.Core
{
    public class LockController : IDisposable
    {
        private const int MaxRepeatedUnresolvedAutoCorrections = 2;

        private const int DisplaySettleDelayMs = 2200;
        private const int DisplayStabilizationDelayMs = 5200;

        private const int PositionCheckDelayMs = 180;
        private const int PositionCheckRetryDelayMs = 200;

        private const int ApplySuppressSeconds = 2;
        private const int DisplayChangeSuppressSeconds = 6;

        private const int MaxDeferredCheckDelayMs = 6000;
        private const int ExplorerMonitorIntervalMs = 5000;

        private const int ExplorerRecoveryDelayMs = 1000;

        private readonly LayoutStore _store;
        private readonly object _stateSync = new object();
        private bool _disposed;
        private bool _isLocked;
        private Dictionary<string, IconPositionItem> _unlockedLayoutSnapshot;
        private IntPtr _winEventHook;
        private User32.WinEventDelegate _winEventProc;
        private IntPtr _desktopListViewHandle;

        private IntPtr _rehookMarshalWindow;

        private Timer _displayChangeDebounceTimer;
        private Timer _locationChangeDebounceTimer;
        private Timer _displayStabilizationTimer;
        private Timer _recoveryTimer;
        private string _pendingRecoveryReason;
        private Timer _explorerMonitorTimer;
        private int _lastExplorerPid;
        private bool _isApplying;
        private DateTime _suppressLocationEventsUntilUtc;
        private bool _automaticCorrectionPaused;
        private string _lastUnresolvedCorrectionSignature;
        private int _repeatedUnresolvedCorrectionCount;
        private string _lastAppliedEnvironmentSignature;
        private bool _desktopMouseDragObserved;
        private bool _positionCheckPending;
        private bool _positionCheckTimerScheduled;

        public bool IsLocked
        {
            get { lock (_stateSync) { return _isLocked; } }
        }

        public LayoutStore Store { get { return _store; } }

        public bool HasSavedBaseLayout { get { return _store.CurrentConfig.BaseProfile != null; } }

        public LockController(LayoutStore store)
        {
            _store = store;
            _isLocked = true;
            _unlockedLayoutSnapshot = null;
            _winEventHook = IntPtr.Zero;
            _desktopListViewHandle = IntPtr.Zero;
            _isApplying = false;
            _automaticCorrectionPaused = false;
            _lastUnresolvedCorrectionSignature = null;
            _repeatedUnresolvedCorrectionCount = 0;
            _lastAppliedEnvironmentSignature = null;
            _desktopMouseDragObserved = false;
            _positionCheckPending = false;
            _positionCheckTimerScheduled = false;

            _winEventProc = new User32.WinEventDelegate(OnWinEvent);

            _displayChangeDebounceTimer = new Timer(OnDisplayChangeDebounced, null, Timeout.Infinite, Timeout.Infinite);
            _locationChangeDebounceTimer = new Timer(OnLocationChangeDebounced, null, Timeout.Infinite, Timeout.Infinite);
            _displayStabilizationTimer = new Timer(OnDisplayStabilizationCheck, null, Timeout.Infinite, Timeout.Infinite);
            _recoveryTimer = new Timer(OnRecoveryTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

            _lastExplorerPid = GetExplorerPid();
            _explorerMonitorTimer = new Timer(OnCheckExplorerProcess, null, ExplorerMonitorIntervalMs, ExplorerMonitorIntervalMs);

            RegisterDesktopLocationHook();
        }

        public void Unlock()
        {

            if (!IsLocked)
            {
                return;
            }

            Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
            _unlockedLayoutSnapshot = DesktopLayoutChangeDetector.Clone(currentIcons);
            ResetAutomaticCorrectionCircuit(
                "用户主动解锁，后续再次锁定时应以用户确认的布局为准");

            SetLockState(false, "用户请求解锁并开始调整桌面图标");
        }

        public DesktopLayoutChangeResult InspectUnlockedLayoutChange()
        {

            if (IsLocked)
            {
                throw new InvalidOperationException("当前桌面已经处于锁定状态。");
            }

            Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
            DesktopLayoutChangeResult result = DesktopLayoutChangeDetector.Compare(
                _unlockedLayoutSnapshot,
                currentIcons);

            return result;
        }

        public DesktopProfile SaveCurrentLayoutAndLock()
        {

            if (IsLocked)
            {
                throw new InvalidOperationException("当前桌面已经处于锁定状态。");
            }

            DesktopProfile profile = SaveCurrentLayout();
            SetLockState(true, "用户确认保存当前布局，保存成功后锁定当前图标位置");
            _unlockedLayoutSnapshot = null;

            return profile;
        }

        public void RestoreSavedLayoutAndLock(string reason)
        {

            if (IsLocked)
            {
                return;
            }

            SetLockState(true, reason);
            _unlockedLayoutSnapshot = null;
            ApplyCurrentLayout(reason);
        }

        private void SetLockState(bool value, string reason)
        {
            bool previous;
            lock (_stateSync)
            {
                previous = _isLocked;
                _isLocked = value;
                if (!value)
                {
                    _desktopMouseDragObserved = false;
                    _positionCheckPending = false;
                    _positionCheckTimerScheduled = false;
                }
            }
        }

        public void Start()
        {
            EnsureBaselineIntegrity();

            Unlock();
        }

        public void ApplyCurrentLayout(string reason)
        {
            bool automaticReason = IsAutomaticRecoveryReason(reason);

            lock (_stateSync)
            {
                if (_disposed) return;

                if (!_isLocked)
                {
                    return;
                }

                if (automaticReason && _automaticCorrectionPaused)
                {
                    return;
                }

                if (_isApplying)
                {
                    return;
                }

                _isApplying = true;
                _suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(ApplySuppressSeconds);
            }

            try
            {
                MonitorProfileInfo primary = DisplayInfo.GetPrimaryMonitor();
                Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
                if (currentIcons.Count == 0)
                {
                    return;
                }

                DesktopProfile runtimeGeometry = BuildRuntimeGeometry(primary, currentIcons);
                string runtimeSignature = BuildEnvironmentSignature(primary, runtimeGeometry);

                Dictionary<string, IconPositionItem> targetPositions = null;
                DesktopProfile exactProfile = _store.FindExactProfile(
                    primary.ResolutionKey,
                    primary.Dpi,
                    primary.MonitorFingerprint);

                if (exactProfile != null && exactProfile.Icons.Count > 0)
                {
                    DesktopProfile availableExactProfile =
                    CreateCurrentIconSubset(exactProfile, currentIcons);
                    if (availableExactProfile.Icons.Count == 0)
                    {
                        return;
                    }

                    ProfileGeometryCompatibility compatibility =
                    AdaptiveMapper.CheckRuntimeGeometryCompatibility(availableExactProfile, runtimeGeometry);

                    if (compatibility.IsCompatible)
                    {
                        targetPositions = availableExactProfile.Icons;
                    }
                    else
                    {
                        targetPositions = AdaptiveMapper.Map(
                            availableExactProfile,
                            primary,
                            runtimeGeometry);
                    }
                }
                else if (_store.CurrentConfig.BaseProfile != null &&
                    _store.CurrentConfig.BaseProfile.Icons.Count > 0)
                {
                    DesktopProfile availableBaseProfile =
                    CreateCurrentIconSubset(_store.CurrentConfig.BaseProfile, currentIcons);
                    if (availableBaseProfile.Icons.Count == 0)
                    {
                        return;
                    }

                    targetPositions = AdaptiveMapper.Map(
                        availableBaseProfile,
                        primary,
                        runtimeGeometry);
                }
                else
                {
                    return;
                }

                IconPositionComparison before =
                IconAccessor.ComparePositions(targetPositions, currentIcons);

                if (!before.HasDifferences)
                {
                    ResetAutomaticCorrectionCircuit(
                    "当前图标坐标已与目标一致");
                    lock (_stateSync)
                    {
                        _lastAppliedEnvironmentSignature = runtimeSignature;
                    }
                    return;
                }

                int matchedOrWritten = IconAccessor.ApplyPositions(targetPositions);
                Dictionary<string, IconPositionItem> verifiedIcons =
                IconAccessor.ReadCurrentIconPositions();
                IconPositionComparison after =
                IconAccessor.ComparePositions(targetPositions, verifiedIcons);

                if (after.HasDifferences)
                {
                    RegisterUnresolvedCorrection(
                    automaticReason,
                    primary,
                    runtimeGeometry,
                    after,
                    reason);
                }
                else
                {
                    ResetAutomaticCorrectionCircuit(
                    "恢复后回读确认所有目标图标已落在当前网格允许的位置");
                    lock (_stateSync)
                    {
                        _lastAppliedEnvironmentSignature =
                        BuildEnvironmentSignature(
                            primary,
                            BuildRuntimeGeometry(primary, verifiedIcons));
                    }
                }

            }
            finally
            {
                lock (_stateSync)
                {
                    _isApplying = false;
                    _suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(ApplySuppressSeconds);
                }
            }
        }

        public void OnDisplayChanged(string eventName)
        {

            lock (_stateSync)
            {

                _suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(DisplayChangeSuppressSeconds);
                _automaticCorrectionPaused = false;
                _lastUnresolvedCorrectionSignature = null;
                _repeatedUnresolvedCorrectionCount = 0;
                _lastAppliedEnvironmentSignature = null;
                _desktopMouseDragObserved = false;
                _positionCheckPending = false;
                _positionCheckTimerScheduled = false;
            }

            _displayChangeDebounceTimer.Change(DisplaySettleDelayMs, Timeout.Infinite);
            _displayStabilizationTimer.Change(DisplayStabilizationDelayMs, Timeout.Infinite);
        }

        private void OnDisplayChangeDebounced(object state)
        {
            RegisterDesktopLocationHook();
            ApplyCurrentLayout("分辨率/显示器变化事件触发");
        }

        private void OnDisplayStabilizationCheck(object state)
        {
            lock (_stateSync)
            {
                if (_disposed || !_isLocked) return;
            }
            RegisterDesktopLocationHook();
            string currentSignature = TryReadEnvironmentSignature();
            string previousSignature;
            lock (_stateSync)
            {
                previousSignature = _lastAppliedEnvironmentSignature;
            }

            if (!string.IsNullOrEmpty(currentSignature) &&
                string.Equals(currentSignature, previousSignature, StringComparison.Ordinal))
            {
                return;
            }

            ApplyCurrentLayout("显示设置变化后的二次稳定校验");
        }

        public void SetRehookMarshalWindow(IntPtr hwnd)
        {
            lock (_stateSync)
            {
                _rehookMarshalWindow = hwnd;
            }
        }

        public void RegisterDesktopLocationHook()
        {
            IntPtr marshalWindow;
            lock (_stateSync)
            {

                if (_disposed) return;
                marshalWindow = _rehookMarshalWindow;
            }

            if (marshalWindow != IntPtr.Zero)
            {
                uint ownerPid;
                uint ownerTid = User32.GetWindowThreadProcessId(marshalWindow, out ownerPid);
                if (ownerTid != 0 && ownerTid != User32.GetCurrentThreadId())
                {

                    IntPtr freshHandle = User32.GetDesktopListViewHandle();
                    if (freshHandle != IntPtr.Zero)
                    {
                        lock (_stateSync)
                        {
                            _desktopListViewHandle = freshHandle;
                        }
                    }

                    User32.PostMessage(
                        marshalWindow,
                        (uint)User32.WM_APP_REHOOK_LOCATION,
                        IntPtr.Zero,
                        IntPtr.Zero);
                    return;
                }
            }

            InstallDesktopLocationHook();
        }

        private void InstallDesktopLocationHook()
        {
            try
            {
                IntPtr oldHook;
                lock (_stateSync)
                {
                    if (_disposed) return;
                    oldHook = _winEventHook;
                    _winEventHook = IntPtr.Zero;
                    _desktopListViewHandle = IntPtr.Zero;
                }

                if (oldHook != IntPtr.Zero)
                {
                    User32.UnhookWinEvent(oldHook);
                }

                IntPtr hListView = User32.GetDesktopListViewHandle();
                if (hListView != IntPtr.Zero)
                {
                    uint pid;
                    User32.GetWindowThreadProcessId(hListView, out pid);

                    IntPtr newHook = User32.SetWinEventHook(
                        User32.EVENT_OBJECT_LOCATIONCHANGE,
                        User32.EVENT_OBJECT_LOCATIONCHANGE,
                        IntPtr.Zero,
                        _winEventProc,
                        pid,
                        0,
                        User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS
                        );

                    IntPtr discardHook = IntPtr.Zero;
                    lock (_stateSync)
                    {
                        if (_disposed)
                        {
                            discardHook = newHook;
                        }
                        else
                        {
                            _winEventHook = newHook;
                        }
                        _desktopListViewHandle = hListView;
                    }

                    if (discardHook != IntPtr.Zero)
                    {
                        User32.UnhookWinEvent(discardHook);
                    }
                }
            }
            catch
            {

            }
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (eventType != User32.EVENT_OBJECT_LOCATIONCHANGE)
            {
                return;
            }

            bool leftMouseButtonDown = User32.IsLeftMouseButtonDown();
            bool shouldScheduleTimer = false;
            int checkDelayMs = PositionCheckDelayMs;
            DateTime now = DateTime.UtcNow;

            lock (_stateSync)
            {
                if (_disposed || !_isLocked || _automaticCorrectionPaused)
                {
                    return;
                }

                DesktopLocationEventDecision decision = DesktopLocationEventClassifier.Evaluate(
                    _desktopListViewHandle,
                    hwnd,
                    idObject,
                    idChild,
                    leftMouseButtonDown,
                    _desktopMouseDragObserved);

                if (decision.MarksMouseDrag)
                {
                    _desktopMouseDragObserved = true;
                }

                if (decision.ShouldQueuePositionCheck && !_positionCheckTimerScheduled)
                {

                    _positionCheckPending = true;
                    _positionCheckTimerScheduled = true;
                    shouldScheduleTimer = true;

                    if (_isApplying)
                    {
                        checkDelayMs = PositionCheckRetryDelayMs;
                    }
                    else if (now < _suppressLocationEventsUntilUtc)
                    {
                        checkDelayMs =
                            (int)Math.Ceiling(
                                (_suppressLocationEventsUntilUtc - now).TotalMilliseconds) +
                            PositionCheckDelayMs;
                        if (checkDelayMs < PositionCheckRetryDelayMs)
                        {
                            checkDelayMs = PositionCheckRetryDelayMs;
                        }
                        if (checkDelayMs > MaxDeferredCheckDelayMs)
                        {
                            checkDelayMs = MaxDeferredCheckDelayMs;
                        }
                    }
                }
                else if (decision.ShouldQueuePositionCheck)
                {
                    _positionCheckPending = true;
                }
            }

            if (shouldScheduleTimer)
            {
                _locationChangeDebounceTimer.Change(checkDelayMs, Timeout.Infinite);
            }
        }

        private void OnLocationChangeDebounced(object state)
        {
            bool waitAndRetry = false;
            bool shouldApply = false;
            lock (_stateSync)
            {
                if (_disposed || !_isLocked || _automaticCorrectionPaused)
                {
                    _desktopMouseDragObserved = false;
                    _positionCheckPending = false;
                    _positionCheckTimerScheduled = false;
                    return;
                }

                if (!_positionCheckPending)
                {
                    _positionCheckTimerScheduled = false;
                    return;
                }

                if (_isApplying)
                {
                    waitAndRetry = true;
                }
                else if (DateTime.UtcNow < _suppressLocationEventsUntilUtc)
                {
                    waitAndRetry = true;
                }
                else if (_desktopMouseDragObserved && User32.IsLeftMouseButtonDown())
                {
                    waitAndRetry = true;
                }
                else
                {
                    _positionCheckPending = false;
                    _desktopMouseDragObserved = false;
                    _positionCheckTimerScheduled = false;
                    shouldApply = true;
                }
            }

            if (waitAndRetry)
            {
                _locationChangeDebounceTimer.Change(PositionCheckRetryDelayMs, Timeout.Infinite);
                return;
            }

            if (!shouldApply)
            {
                return;
            }
            ApplyCurrentLayout("图标位置发生非预期偏移，执行锁定纠正");
        }

        private static bool IsAutomaticRecoveryReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return true;

            return reason.IndexOf("事件", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("稳定校验", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("非预期偏移", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("巡检", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("Explorer重启", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("服务初始化", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static DesktopProfile BuildRuntimeGeometry(
            MonitorProfileInfo monitor,
            Dictionary<string, IconPositionItem> icons)
        {
            DesktopProfile runtime = new DesktopProfile();
            runtime.Resolution = monitor.ResolutionKey;
            runtime.Dpi = monitor.Dpi;
            runtime.MonitorFingerprint = monitor.MonitorFingerprint;
            runtime.Icons = DesktopLayoutChangeDetector.Clone(icons);
            AdaptiveMapper.PopulateProfileGeometry(runtime, monitor, runtime.Icons);
            return runtime;
        }

        private static DesktopProfile CreateCurrentIconSubset(
            DesktopProfile source,
            Dictionary<string, IconPositionItem> currentIcons)
        {
            DesktopProfile subset = source != null
            ? LayoutStore.CloneProfile(source)
            : new DesktopProfile();
            subset.Icons.Clear();

            if (source == null || source.Icons == null || currentIcons == null)
            {
                return subset;
            }

            Dictionary<string, List<IconPositionItem>> currentGroups =
            new Dictionary<string, List<IconPositionItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IconPositionItem> pair in currentIcons)
            {
                if (pair.Value == null) continue;
                string name = IconAccessor.ExtractBaseDisplayName(pair.Key, pair.Value.DisplayName);
                List<IconPositionItem> list;
                if (!currentGroups.TryGetValue(name, out list))
                {
                    list = new List<IconPositionItem>();
                    currentGroups[name] = list;
                }
                list.Add(pair.Value);
            }

            Dictionary<string, List<IconPositionItem>> sourceGroups =
            new Dictionary<string, List<IconPositionItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IconPositionItem> pair in source.Icons)
            {
                if (pair.Value == null) continue;
                string name = IconAccessor.ExtractBaseDisplayName(pair.Key, pair.Value.DisplayName);
                List<IconPositionItem> list;
                if (!sourceGroups.TryGetValue(name, out list))
                {
                    list = new List<IconPositionItem>();
                    sourceGroups[name] = list;
                }
                list.Add(pair.Value);
            }

            foreach (KeyValuePair<string, List<IconPositionItem>> sg in sourceGroups)
            {
                string name = sg.Key;
                List<IconPositionItem> sList = sg.Value;
                List<IconPositionItem> cList;
                if (!currentGroups.TryGetValue(name, out cList) || cList.Count == 0)
                {
                    continue;
                }

                int countToTake = Math.Min(sList.Count, cList.Count);
                for (int i = 0; i < countToTake; i++)
                {
                    IconPositionItem item = sList[i];
                    subset.Icons[item.Key] = new IconPositionItem
                    {
                        Key = item.Key,
                        DisplayName = item.DisplayName,
                        X = item.X,
                        Y = item.Y
                    };
                }
            }

            return subset;
        }

        private static string BuildEnvironmentSignature(
            MonitorProfileInfo monitor,
            DesktopProfile runtimeGeometry)
        {
            if (monitor == null || runtimeGeometry == null) return null;

            return string.Format(
                "{0}|{1}|{2}|{3}x{4}|origin={5},{6}|grid={7}x{8}",
                monitor.ResolutionKey,
                monitor.Dpi,
                monitor.MonitorFingerprint,
                runtimeGeometry.WorkAreaWidth,
                runtimeGeometry.WorkAreaHeight,
                runtimeGeometry.GridOriginX,
                runtimeGeometry.GridOriginY,
                runtimeGeometry.GridSpacingX,
                runtimeGeometry.GridSpacingY);
        }

        private string TryReadEnvironmentSignature()
        {
            try
            {
                MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
                Dictionary<string, IconPositionItem> icons =
                IconAccessor.ReadCurrentIconPositions();
                if (icons.Count == 0)
                {
                    return null;
                }

                DesktopProfile runtime = BuildRuntimeGeometry(monitor, icons);
                string signature = BuildEnvironmentSignature(monitor, runtime);
                return signature;
            }
            catch
            {
                return null;
            }
        }

        private void RegisterUnresolvedCorrection(
            bool automaticReason,
            MonitorProfileInfo monitor,
            DesktopProfile runtimeGeometry,
            IconPositionComparison after,
            string reason)
        {
            string signature = string.Format(
            "{0}|{1}|grid={2}x{3}|mismatch={4}|missing={5}",
            monitor.ResolutionKey,
            monitor.Dpi,
            runtimeGeometry.GridSpacingX,
            runtimeGeometry.GridSpacingY,
            after.MismatchCount,
            after.MissingTargetIconCount);

            if (!automaticReason)
            {
                return;
            }

            lock (_stateSync)
            {
                if (string.Equals(
                _lastUnresolvedCorrectionSignature,
                signature,
                StringComparison.Ordinal))
                {
                    _repeatedUnresolvedCorrectionCount++;
                }
                else
                {
                    _lastUnresolvedCorrectionSignature = signature;
                    _repeatedUnresolvedCorrectionCount = 1;
                }

                if (_repeatedUnresolvedCorrectionCount >= MaxRepeatedUnresolvedAutoCorrections)
                {
                    _automaticCorrectionPaused = true;
                }
            }
        }

        private void ResetAutomaticCorrectionCircuit(string reason)
        {
            lock (_stateSync)
            {
                _automaticCorrectionPaused = false;
                _lastUnresolvedCorrectionSignature = null;
                _repeatedUnresolvedCorrectionCount = 0;
            }
        }

        private void OnCheckExplorerProcess(object state)
        {
            try
            {
                int currentPid = GetExplorerPid();
                int previousPid;
                lock (_stateSync) { previousPid = _lastExplorerPid; }

                if (currentPid > 0 && currentPid != previousPid)
                {
                    lock (_stateSync) { _lastExplorerPid = currentPid; }

                    ScheduleRecovery("Explorer重启恢复", ExplorerRecoveryDelayMs);
                }
            }
            catch
            {
            }

            VerifyLockedLayout();
        }

        private void VerifyLockedLayout()
        {
            lock (_stateSync)
            {
                if (_disposed || !_isLocked) return;
                if (_isApplying) return;
                if (_automaticCorrectionPaused) return;
                if (_positionCheckPending) return;
                if (DateTime.UtcNow < _suppressLocationEventsUntilUtc) return;
            }

            ApplyCurrentLayout("锁定巡检自动恢复");
        }

        private int GetExplorerPid()
        {
            Process[] procs = null;
            try
            {
                procs = Process.GetProcessesByName("explorer");
                if (procs.Length > 0)
                {
                    return procs[0].Id;
                }
            }
            catch
            {
            }
            finally
            {
                if (procs != null)
                {
                    for (int i = 0; i < procs.Length; i++)
                    {
                        if (procs[i] != null) procs[i].Dispose();
                    }
                }
            }
            return 0;
        }

        private void ScheduleRecovery(string reason, int delayMs)
        {
            lock (_stateSync)
            {
                if (_disposed) return;
                _pendingRecoveryReason = reason;
                _recoveryTimer.Change(delayMs, Timeout.Infinite);
            }
        }

        private void OnRecoveryTimerElapsed(object state)
        {
            string reason;
            lock (_stateSync)
            {
                if (_disposed) return;
                reason = _pendingRecoveryReason;
                _pendingRecoveryReason = null;
                _recoveryTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            try
            {
                RegisterDesktopLocationHook();
                ApplyCurrentLayout(reason);
            }
            catch
            {
            }
        }

        public DesktopProfile SaveCurrentLayout()
        {
            Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
            if (currentIcons.Count == 0)
            {
                throw new IOException("未读取到任何桌面图标，本次保存未生效。");
            }

            MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
            if (!_store.SaveBaseAndExactProfile(monitor, currentIcons))
            {
                throw new IOException(string.Format(
                    "主配置文件写入失败，本次保存未生效，请检查 {0} 是否可写（磁盘空间、只读属性或被其他程序占用）。",
                    Path.Combine(_store.ConfigDirectory, LayoutStore.ConfigFileName)));
            }

            return _store.CurrentConfig.BaseProfile;
        }

        public bool ApplyBaseLayout()
        {

            DesktopProfile baseProfile = _store.CurrentConfig.BaseProfile;
            if (baseProfile == null || baseProfile.Icons == null || baseProfile.Icons.Count == 0)
            {
                return false;
            }

            lock (_stateSync)
            {
                if (_disposed || _isApplying)
                {
                    return false;
                }
                _isApplying = true;
                _suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(ApplySuppressSeconds);
            }

            try
            {
                MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
                Dictionary<string, IconPositionItem> currentIcons =
                IconAccessor.ReadCurrentIconPositions();
                DesktopProfile runtimeGeometry = BuildRuntimeGeometry(monitor, currentIcons);
                DesktopProfile availableBaseProfile =
                CreateCurrentIconSubset(baseProfile, currentIcons);
                if (availableBaseProfile.Icons.Count == 0)
                {
                    return false;
                }

                Dictionary<string, IconPositionItem> positions;
                bool exactMode =
                string.Equals(availableBaseProfile.Resolution, monitor.ResolutionKey, StringComparison.OrdinalIgnoreCase) &&
                availableBaseProfile.Dpi == monitor.Dpi &&
                string.Equals(availableBaseProfile.MonitorFingerprint, monitor.MonitorFingerprint, StringComparison.OrdinalIgnoreCase);

                ProfileGeometryCompatibility compatibility = exactMode
                ? AdaptiveMapper.CheckRuntimeGeometryCompatibility(availableBaseProfile, runtimeGeometry)
                : null;

                if (exactMode && compatibility.IsCompatible)
                {
                    positions = availableBaseProfile.Icons;
                }
                else
                {
                    positions = AdaptiveMapper.Map(
                    availableBaseProfile,
                    monitor,
                    runtimeGeometry);
                }

                int applied = IconAccessor.ApplyPositions(positions);
                if (applied == 0)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                lock (_stateSync)
                {
                    _isApplying = false;
                    _suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(ApplySuppressSeconds);
                }
            }
        }

        private void EnsureBaselineIntegrity()
        {
            try
            {
                Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
                if (currentIcons.Count == 0)
                {
                    return;
                }

                DesktopProfile baseProfile = _store.CurrentConfig.BaseProfile;
                int minimumReasonableCount = Math.Max(3, currentIcons.Count / 2);
                bool profileMissing = baseProfile == null;
                bool iconCoverageInvalid = baseProfile != null &&
                (baseProfile.Icons == null || baseProfile.Icons.Count < minimumReasonableCount);
                bool geometryMissing = baseProfile != null &&
                (baseProfile.WorkAreaWidth <= 0 ||
                    baseProfile.WorkAreaHeight <= 0 ||
                    baseProfile.GridSpacingX <= 0 ||
                    baseProfile.GridSpacingY <= 0);

                if (profileMissing || iconCoverageInvalid || geometryMissing)
                {
                    MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
                    _store.SaveBaseAndExactProfile(monitor, currentIcons);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            lock (_stateSync)
            {
                if (_disposed) return;
                _disposed = true;
                _isLocked = false;
                _pendingRecoveryReason = null;

                if (_winEventHook != IntPtr.Zero)
                {
                    User32.UnhookWinEvent(_winEventHook);
                    _winEventHook = IntPtr.Zero;
                }
            }

            DisposeTimer(ref _displayChangeDebounceTimer);
            DisposeTimer(ref _locationChangeDebounceTimer);
            DisposeTimer(ref _displayStabilizationTimer);
            DisposeTimer(ref _recoveryTimer);
            DisposeTimer(ref _explorerMonitorTimer);
        }

        private static void DisposeTimer(ref Timer timer)
        {
            Timer local = timer;
            timer = null;
            if (local != null) local.Dispose();
        }
    }
}
