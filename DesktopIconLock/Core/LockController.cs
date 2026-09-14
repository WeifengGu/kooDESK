using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using DesktopIconLock.Common;
using DesktopIconLock.Native;

namespace DesktopIconLock.Core
{
public class LockController : IDisposable
{
private const int MaxRepeatedUnresolvedAutoCorrections = 2;
private readonly LayoutStore _store;
private readonly HistoryStore _historyStore;
private readonly object _stateSync = new object();
private bool _isLocked;
private Dictionary<string, IconPositionItem> _unlockedLayoutSnapshot;
private IntPtr _winEventHook;
private User32.WinEventDelegate _winEventProc;
private IntPtr _desktopListViewHandle;

private Timer _displayChangeDebounceTimer;
private Timer _locationChangeDebounceTimer;
private Timer _displayStabilizationTimer;
private Timer _explorerMonitorTimer;
private int _lastExplorerPid;
private bool _isApplying;
private DateTime _suppressLocationEventsUntilUtc;
private bool _automaticCorrectionPaused;
private string _lastUnresolvedCorrectionSignature;
private int _repeatedUnresolvedCorrectionCount;
private string _lastAppliedEnvironmentSignature;
private DateTime _lastIgnoredWinEventLogUtc;
private bool _desktopMouseDragObserved;
private bool _positionCheckPending;
private bool _positionCheckTimerScheduled;

public bool IsLocked
{
get { return _isLocked; }
}

public LayoutStore Store { get { return _store; } }
public HistoryStore History { get { return _historyStore; } }

public LockController(LayoutStore store)
{
_store = store;
_historyStore = new HistoryStore(store.ConfigDirectory);
_isLocked = true;
_unlockedLayoutSnapshot = null;
_winEventHook = IntPtr.Zero;
_desktopListViewHandle = IntPtr.Zero;
_isApplying = false;
_automaticCorrectionPaused = false;
_lastUnresolvedCorrectionSignature = null;
_repeatedUnresolvedCorrectionCount = 0;
_lastAppliedEnvironmentSignature = null;
_lastIgnoredWinEventLogUtc = DateTime.MinValue;
_desktopMouseDragObserved = false;
_positionCheckPending = false;
_positionCheckTimerScheduled = false;

_winEventProc = new User32.WinEventDelegate(OnWinEvent);

_displayChangeDebounceTimer = new Timer(OnDisplayChangeDebounced, null, Timeout.Infinite, Timeout.Infinite);
_locationChangeDebounceTimer = new Timer(OnLocationChangeDebounced, null, Timeout.Infinite, Timeout.Infinite);
_displayStabilizationTimer = new Timer(OnDisplayStabilizationCheck, null, Timeout.Infinite, Timeout.Infinite);

_lastExplorerPid = GetExplorerPid();
_explorerMonitorTimer = new Timer(OnCheckExplorerProcess, null, 5000, 5000);

RegisterDesktopLocationHook();
}

public void Unlock()
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"LockController.Unlock",
"托盘锁定切换入口",
string.Format("当前锁定状态={0}", _isLocked),
"在进入解锁状态前捕获桌面布局，作为再次锁定时的变更比较基线",
traceId);

if (!_isLocked)
{
AuditLogger.LogRejected(
"解锁状态校验",
"当前已经处于解锁状态",
409,
"锁定状态和桌面布局均未变化",
"继续调整图标或重新点击锁定",
traceId);
return;
}

Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
_unlockedLayoutSnapshot = DesktopLayoutChangeDetector.Clone(currentIcons);
ResetAutomaticCorrectionCircuit(
    "用户主动解锁，后续再次锁定时应以用户确认的布局为准",
    traceId);
AuditLogger.LogStorageOperation(
"保存解锁时内存快照",
"桌面图标位置临时基线",
"仅保存在当前进程内存，不写入布局配置或历史记录",
_unlockedLayoutSnapshot != null ? _unlockedLayoutSnapshot.Count : 0,
"解锁比较基线已准备完成",
traceId);

SetLockState(false, "用户请求解锁并开始调整桌面图标", traceId);
AuditLogger.LogResponseReturn(
"LockController.Unlock",
200,
string.Format("已进入解锁状态，比较基线图标数={0}", currentIcons.Count),
0,
"成功",
"用户可以移动图标，再次锁定时将先检查布局变化",
traceId);
}

public DesktopLayoutChangeResult InspectUnlockedLayoutChange()
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"LockController.InspectUnlockedLayoutChange",
"托盘锁定切换入口",
string.Format(
"当前锁定状态={0}, 是否存在解锁快照={1}",
_isLocked,
_unlockedLayoutSnapshot != null),
"比较解锁时与当前桌面的图标集合和坐标",
traceId);

if (_isLocked)
{
AuditLogger.LogRejected(
"锁定前布局检查",
"当前已经处于锁定状态",
409,
"不会重复执行锁定操作",
"如需调整布局请先解锁",
traceId);
throw new InvalidOperationException("当前桌面已经处于锁定状态。");
}

Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
DesktopLayoutChangeResult result = DesktopLayoutChangeDetector.Compare(
_unlockedLayoutSnapshot,
currentIcons);

AuditLogger.LogRuleDecision(
"解锁期间桌面布局变更检测",
result.Summary,
result.HasChanged ? "布局已发生变动，需要用户选择" : "布局未发生变动，可以直接恢复并锁定",
result.ComparisonAvailable
? "按稳定图标标识比较新增、删除和坐标变化"
: "缺少可用的解锁快照，为避免覆盖当前桌面，按已发生变动处理",
traceId);
AuditLogger.LogResponseReturn(
"LockController.InspectUnlockedLayoutChange",
200,
result.Summary,
0,
"成功",
result.HasChanged ? "界面应显示保存、取消、恢复三选项" : "界面可以直接执行原锁定恢复流程",
traceId);
return result;
}

public HistoryLayoutRecord SaveCurrentLayoutAndLock()
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"LockController.SaveCurrentLayoutAndLock",
"桌面布局变动提示框/保存当前布局",
string.Format("当前锁定状态={0}", _isLocked),
"保存当前图标位置和历史截图，成功后锁定当前布局",
traceId);

if (_isLocked)
{
AuditLogger.LogRejected(
"保存后锁定状态校验",
"当前已经处于锁定状态",
409,
"不会重复保存或改变锁定状态",
"如需更新布局请先解锁",
traceId);
throw new InvalidOperationException("当前桌面已经处于锁定状态。");
}

HistoryLayoutRecord record = SaveCurrentAsExact(traceId, "桌面布局变动提示框");
SetLockState(true, "用户确认保存当前布局，保存成功后锁定当前图标位置", traceId);
_unlockedLayoutSnapshot = null;

AuditLogger.LogResponseReturn(
"LockController.SaveCurrentLayoutAndLock",
200,
string.Format("历史记录={0}, 图标数={1}", record.Id, record.Profile.Icons.Count),
0,
"成功",
"当前桌面已保存并成为新的锁定目标，不执行旧布局恢复",
traceId);
return record;
}

public void RestoreSavedLayoutAndLock(string reason)
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"LockController.RestoreSavedLayoutAndLock",
"托盘锁定切换入口",
string.Format("当前锁定状态={0}, 原因={1}", _isLocked, reason),
"进入锁定状态后调用既有布局匹配与恢复决策链",
traceId);

if (_isLocked)
{
AuditLogger.LogRejected(
"恢复后锁定状态校验",
"当前已经处于锁定状态",
409,
"不会重复恢复或改变锁定状态",
"无需重复点击锁定",
traceId);
return;
}

SetLockState(true, reason, traceId);
_unlockedLayoutSnapshot = null;
ApplyCurrentLayout(reason);
AuditLogger.LogResponseReturn(
"LockController.RestoreSavedLayoutAndLock",
200,
"已进入锁定状态并完成既有布局恢复调用",
0,
"成功",
"后续图标偏移将继续按原锁定机制纠正",
traceId);
}

private void SetLockState(bool value, string reason, string traceId)
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
AuditLogger.LogStateChange(
"锁定状态切换",
"LockController.IsLocked",
previous.ToString(),
value.ToString(),
reason,
traceId);
}

public void Start()
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogBusinessEntry("启动决策调度器", "初始化", "检测当前显示器并应用保存的布局", traceId);
EnsureBaselineIntegrity();
ApplyCurrentLayout("服务初始化启动");
}

public void ApplyCurrentLayout(string reason)
{
bool automaticReason = IsAutomaticRecoveryReason(reason);
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;

lock (_stateSync)
{
if (!_isLocked)
{
AuditLogger.Debug("布局应用跳过", string.Format("当前处于解锁状态，不执行位置还原 (原因: {0})", reason), traceId);
return;
}

if (automaticReason && _automaticCorrectionPaused)
{
AuditLogger.LogRejected(
"自动锁定熔断器",
"同一显示环境下连续恢复失败，已暂停自动纠正以阻止桌面图标持续抽搐",
409,
"不会再自动写入可能与Explorer网格不兼容的坐标",
"请解锁后检查布局，或在当前显示环境重新保存布局",
traceId);
return;
}

if (_isApplying)
{
AuditLogger.Debug(
"布局应用跳过",
"已有一个布局恢复任务正在执行，本次事件不并发写入桌面ListView",
traceId);
return;
}

_isApplying = true;
_suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(2);
}

AuditLogger.LogRequestArrival(
"LockController.ApplyCurrentLayout",
"内部调度器/事件触发",
string.Format("原因={0}, 自动触发={1}", reason, automaticReason),
"先读取当前桌面和当前真实网格；确认存在差异后才允许写入图标坐标",
traceId);

try
{
MonitorProfileInfo primary = DisplayInfo.GetPrimaryMonitor();
Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
if (currentIcons.Count == 0)
{
AuditLogger.LogRejected(
"布局恢复前置校验",
"未读取到任何当前桌面图标，可能仍处于Explorer或显示器重建中",
503,
"不写入空布局，避免覆盖用户当前桌面",
"等待桌面稳定后由新的显示事件再次检查",
traceId);
return;
}

DesktopProfile runtimeGeometry = BuildRuntimeGeometry(primary, currentIcons);
string runtimeSignature = BuildEnvironmentSignature(primary, runtimeGeometry);
AuditLogger.LogBusinessEntry(
"决策布局选择",
string.Format("显示器=[{0}], 当前网格={1}x{2}, 当前图标数={3}",
primary,
runtimeGeometry.GridSpacingX,
runtimeGeometry.GridSpacingY,
currentIcons.Count),
"先确认保存Profile能否落在当前Explorer真实网格，再选择精确恢复或拓扑自适应",
traceId);

Dictionary<string, IconPositionItem> targetPositions = null;
string targetSource = null;
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
AuditLogger.LogRejected(
"精确Profile可用图标检查",
"保存的精确Profile没有任何图标仍存在于当前桌面",
404,
"不对当前桌面写入坐标，避免将过期Profile套用到无关图标",
"请在当前显示环境重新保存布局",
traceId);
return;
}

ProfileGeometryCompatibility compatibility =
AdaptiveMapper.CheckRuntimeGeometryCompatibility(availableExactProfile, runtimeGeometry);

if (compatibility.IsCompatible)
{
targetPositions = availableExactProfile.Icons;
targetSource = "兼容的精确Profile";
AuditLogger.LogRuleDecision(
"精确Profile运行时兼容性",
compatibility.Reason,
"直接采用精确坐标",
"分辨率、DPI、显示器身份、工作区和桌面网格均兼容",
traceId);
}
else
{
targetPositions = AdaptiveMapper.Map(
availableExactProfile,
primary,
runtimeGeometry);
targetSource = "精确Profile按当前Explorer网格重映射";
AuditLogger.LogRuleDecision(
"精确Profile运行时兼容性",
compatibility.Reason,
"拒绝直接写入旧绝对坐标，改为拓扑自适应",
"避免旧Profile的网格步进与当前Explorer网格不一致时发生写入—吸附—再写入循环",
traceId);
}
}
else if (_store.CurrentConfig.BaseProfile != null &&
_store.CurrentConfig.BaseProfile.Icons.Count > 0)
{
DesktopProfile availableBaseProfile =
CreateCurrentIconSubset(_store.CurrentConfig.BaseProfile, currentIcons);
if (availableBaseProfile.Icons.Count == 0)
{
AuditLogger.LogRejected(
"基准Profile可用图标检查",
"基准Profile没有任何图标仍存在于当前桌面",
404,
"不对当前桌面写入坐标，避免使用完全过期的布局",
"请在当前显示环境重新保存基准布局",
traceId);
return;
}

targetPositions = AdaptiveMapper.Map(
availableBaseProfile,
primary,
runtimeGeometry);
targetSource = "基准Profile按当前Explorer网格映射";
AuditLogger.LogRuleDecision(
"精确Profile判定",
"无可用精确Profile",
"按当前Explorer网格执行结构保持型自适应",
string.Format(
"基准={0}@{1}%，可用图标={2}，当前网格={3}x{4}",
availableBaseProfile.Resolution,
(int)Math.Round(availableBaseProfile.Dpi * 100.0 / 96.0),
availableBaseProfile.Icons.Count,
runtimeGeometry.GridSpacingX,
runtimeGeometry.GridSpacingY),
traceId);
}
else
{
AuditLogger.Info(
"配置未就绪",
"当前未保存任何基准配置或精确配置，暂不执行移动",
traceId);
return;
}

IconPositionComparison before =
IconAccessor.ComparePositions(targetPositions, currentIcons);
AuditLogger.LogRuleDecision(
"恢复前坐标差异检查",
before.Summary,
before.HasDifferences ? "确认存在差异，允许进入写入流程" : "差异为0，直接跳过",
before.HasDifferences
? string.Format("目标来源={0}", targetSource)
: "避免无差异时切换网格样式、触发Explorer重绘或误监听事件",
traceId);

if (!before.HasDifferences)
{
ResetAutomaticCorrectionCircuit(
"当前图标坐标已与目标一致",
traceId);
lock (_stateSync)
{
_lastAppliedEnvironmentSignature = runtimeSignature;
}
AuditLogger.LogResponseReturn(
"LockController.ApplyCurrentLayout",
200,
before.Summary,
0,
"跳过",
"图标已经处于目标位置，未修改任何ListView样式或坐标",
traceId);
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
reason,
traceId);
}
else
{
ResetAutomaticCorrectionCircuit(
"恢复后回读确认所有目标图标已落在当前网格允许的位置",
traceId);
lock (_stateSync)
{
_lastAppliedEnvironmentSignature =
BuildEnvironmentSignature(
primary,
BuildRuntimeGeometry(primary, verifiedIcons));
}
}

AuditLogger.LogResponseReturn(
"LockController.ApplyCurrentLayout",
after.HasDifferences ? 206 : 200,
string.Format(
"目标来源={0}; 恢复前[{1}]; 写入阶段匹配/写入={2}; 恢复后[{3}]",
targetSource,
before.Summary,
matchedOrWritten,
after.Summary),
0,
after.HasDifferences ? "部分完成" : "成功",
after.HasDifferences
? "当前Explorer仍未接受全部目标坐标；自动恢复将按熔断规则限制重试"
: "图标已恢复且回读确认无偏差",
traceId);
}
catch (Exception ex)
{
AuditLogger.LogException(
"LockController.ApplyCurrentLayout",
"ApplyLayoutException",
ex.Message,
"图标应用中断，已保留当前桌面并结束本轮恢复",
true,
ex,
traceId);
}
finally
{
lock (_stateSync)
{
_isApplying = false;
_suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(2);
}
}
}

public void OnDisplayChanged(string eventName)
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"OnDisplayChanged",
"WM_DISPLAYCHANGE/WM_DPICHANGED/相关工作区变化",
string.Format("事件={0}", eventName),
"显示模式可能正在切换，取消旧的自动失败状态并等待桌面稳定后再决定是否恢复",
traceId);

lock (_stateSync)
{
_suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(6);
_automaticCorrectionPaused = false;
_lastUnresolvedCorrectionSignature = null;
_repeatedUnresolvedCorrectionCount = 0;
_lastAppliedEnvironmentSignature = null;
_desktopMouseDragObserved = false;
_positionCheckPending = false;
_positionCheckTimerScheduled = false;
}

// Windows在分辨率、DPI和Explorer网格切换后会分批重建桌面。
// 先等待较长稳定期；第二个计时器只检查环境是否又变动，不能无条件重复写入。
_displayChangeDebounceTimer.Change(2200, Timeout.Infinite);
_displayStabilizationTimer.Change(5200, Timeout.Infinite);
}

private void OnDisplayChangeDebounced(object state)
{
AuditLogger.Debug(
"显示变化防抖完成",
"首个稳定窗口到达，重新绑定桌面ListView并按当前实测网格评估布局",
AuditLogger.CurrentTraceId);
RegisterDesktopLocationHook();
ApplyCurrentLayout("分辨率/显示器变化事件触发");
}

private void OnDisplayStabilizationCheck(object state)
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
if (!_isLocked) return;
RegisterDesktopLocationHook();
string currentSignature = TryReadEnvironmentSignature(traceId);
string previousSignature;
lock (_stateSync)
{
previousSignature = _lastAppliedEnvironmentSignature;
}

if (!string.IsNullOrEmpty(currentSignature) &&
string.Equals(currentSignature, previousSignature, StringComparison.Ordinal))
{
AuditLogger.LogRuleDecision(
"显示设置二次稳定校验",
string.Format("当前环境签名={0}", currentSignature),
"跳过重复恢复",
"显示器、工作区和Explorer网格均未再变化，不能重复写入图标",
traceId);
return;
}

AuditLogger.LogRuleDecision(
"显示设置二次稳定校验",
string.Format("上次环境签名={0}; 当前环境签名={1}", previousSignature ?? "<空>", currentSignature ?? "<读取失败>"),
"环境仍有变化，允许再次评估",
"仅在桌面环境与首轮恢复时不同的情况下才进入恢复链路",
traceId);
ApplyCurrentLayout("显示设置变化后的二次稳定校验");
}

public void RegisterDesktopLocationHook()
{
string traceId = AuditLogger.CurrentTraceId;
try
{
IntPtr oldHook;
lock (_stateSync)
{
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
uint tid = User32.GetWindowThreadProcessId(hListView, out pid);

IntPtr newHook = User32.SetWinEventHook(
User32.EVENT_OBJECT_LOCATIONCHANGE,
User32.EVENT_OBJECT_LOCATIONCHANGE,
IntPtr.Zero,
_winEventProc,
pid,
tid,
User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS
);
lock (_stateSync)
{
_winEventHook = newHook;
_desktopListViewHandle = hListView;
}

AuditLogger.Info(
"WinEventHook注册",
string.Format(
"已挂钩桌面ListView句柄 0x{0:X} (PID={1}, TID={2})；回调将只接受该句柄的具体图标客户区位置事件",
hListView.ToInt64(),
pid,
tid),
traceId);
}
else
{
AuditLogger.Warn("WinEventHook注册", "未获取到桌面ListView句柄，稍后将在监控中重试", traceId);
}
}
catch (Exception ex)
{
AuditLogger.LogException("WinEventHook注册", "HookException", ex.Message, "位置变动监听降级", true, ex, traceId);
}
}

private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
{
bool shouldQueue = false;
bool shouldScheduleTimer = false;
string ignoredReason = null;
string acceptedReason = null;
DateTime now = DateTime.UtcNow;
bool leftMouseButtonDown = User32.IsLeftMouseButtonDown();

lock (_stateSync)
{
if (!_isLocked)
{
ignoredReason = "当前处于解锁状态";
}
else if (_isApplying)
{
ignoredReason = "本工具正在写入坐标，本次事件属于恢复过程";
}
else if (now < _suppressLocationEventsUntilUtc)
{
ignoredReason = "仍处于恢复后的事件抑制窗口";
}
else if (_automaticCorrectionPaused)
{
ignoredReason = "自动纠正熔断器已暂停本显示环境的写入";
}
else if (eventType != User32.EVENT_OBJECT_LOCATIONCHANGE)
{
ignoredReason = "事件类型不是位置变化";
}
else
{
DesktopLocationEventDecision decision =
DesktopLocationEventClassifier.Evaluate(
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

if (decision.ShouldQueuePositionCheck)
{
_positionCheckPending = true;
shouldQueue = true;
acceptedReason = decision.Reason;
if (!_positionCheckTimerScheduled)
{
_positionCheckTimerScheduled = true;
shouldScheduleTimer = true;
}
}
else
{
ignoredReason = decision.Reason;
}
}
}

if (!shouldQueue)
{
LogIgnoredWinEvent(hwnd, idObject, idChild, ignoredReason);
return;
}

if (shouldScheduleTimer)
{
AuditLogger.Debug(
"桌面图标位置事件",
string.Format(
"已建立一次主动验收任务：原因={0}; hwnd=0x{1:X}, idObject={2}, idChild={3}, 左键按下={4}。后续高频光标事件不会重置计时器。",
acceptedReason,
hwnd.ToInt64(),
idObject,
idChild,
leftMouseButtonDown),
AuditLogger.CurrentTraceId);
_locationChangeDebounceTimer.Change(180, Timeout.Infinite);
}
}

private void OnLocationChangeDebounced(object state)
{
bool waitAndRetry = false;
string waitReason = null;
bool shouldApply = false;
lock (_stateSync)
{
if (!_isLocked || _automaticCorrectionPaused)
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
waitReason = "已有布局恢复正在执行";
}
else if (DateTime.UtcNow < _suppressLocationEventsUntilUtc)
{
waitAndRetry = true;
waitReason = "仍处于上一轮恢复后的事件抑制窗口";
}
else if (_desktopMouseDragObserved && User32.IsLeftMouseButtonDown())
{
waitAndRetry = true;
waitReason = "鼠标左键仍按下，用户尚未结束拖动";
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
AuditLogger.Debug(
"桌面拖动验收等待",
string.Format(
"原因={0}；本次主动验收任务不会依赖新的光标或任务栏事件，200ms后自行重试",
waitReason),
AuditLogger.CurrentTraceId);
_locationChangeDebounceTimer.Change(200, Timeout.Infinite);
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

private string TryReadEnvironmentSignature(string traceId)
{
try
{
MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
Dictionary<string, IconPositionItem> icons =
IconAccessor.ReadCurrentIconPositions();
if (icons.Count == 0)
{
AuditLogger.LogRejected(
"显示稳定性检查",
"未读取到桌面图标，Explorer可能仍未准备完成",
503,
"不将空桌面误判为稳定环境",
"继续等待后续显示事件或Explorer重启检测",
traceId);
return null;
}

DesktopProfile runtime = BuildRuntimeGeometry(monitor, icons);
string signature = BuildEnvironmentSignature(monitor, runtime);
AuditLogger.LogBusinessEntry(
"显示稳定性检查",
string.Format("环境签名={0}, 图标数={1}", signature, icons.Count),
"使用分辨率、DPI、工作区和实测网格共同判断桌面是否仍在变化",
traceId);
return signature;
}
catch (Exception ex)
{
AuditLogger.LogException(
"显示稳定性检查",
ex.GetType().Name,
ex.Message,
"无法确认当前桌面环境，二次稳定检查将保守地允许后续恢复重新评估",
true,
ex,
traceId);
return null;
}
}

private void RegisterUnresolvedCorrection(
bool automaticReason,
MonitorProfileInfo monitor,
DesktopProfile runtimeGeometry,
IconPositionComparison after,
string reason,
string traceId)
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
AuditLogger.Warn(
"人工布局应用未完全收敛",
string.Format(
"原因={0}; {1}。此次为用户主动操作，保留结果但不触发自动纠正熔断。",
reason,
after.Summary),
traceId);
return;
}

bool paused = false;
int repeatedCount;
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

repeatedCount = _repeatedUnresolvedCorrectionCount;
if (repeatedCount >= MaxRepeatedUnresolvedAutoCorrections)
{
_automaticCorrectionPaused = true;
paused = true;
}
}

AuditLogger.LogRuleDecision(
"自动纠正失败熔断检查",
string.Format(
"失败签名={0}; 连续次数={1}; {2}",
signature,
repeatedCount,
after.Summary),
paused ? "暂停自动纠正" : "仅保留一次受控重试机会",
paused
? "相同显示环境和相同网格下连续无法收敛，继续写入只会导致图标抽搐和错位"
: "首次发现回读不一致，可能仍处于Explorer短暂稳定阶段",
traceId);

if (paused)
{
AuditLogger.LogRejected(
"自动锁定熔断器",
string.Format(
"连续{0}次无法让Explorer接受目标坐标；当前实测网格={1}x{2}",
repeatedCount,
runtimeGeometry.GridSpacingX,
runtimeGeometry.GridSpacingY),
409,
"已停止后续自动写入，防止图标继续被程序和Explorer反复拉扯",
"请在当前显示环境重新保存布局，或解锁后人工整理图标",
traceId);
}
}

private void ResetAutomaticCorrectionCircuit(string reason, string traceId)
{
bool wasPaused;
int previousCount;
lock (_stateSync)
{
wasPaused = _automaticCorrectionPaused;
previousCount = _repeatedUnresolvedCorrectionCount;
_automaticCorrectionPaused = false;
_lastUnresolvedCorrectionSignature = null;
_repeatedUnresolvedCorrectionCount = 0;
}

if (wasPaused || previousCount > 0)
{
AuditLogger.LogStateChange(
"自动纠正熔断器状态",
"LockController.AutoCorrectionCircuit",
string.Format("暂停={0}, 连续失败={1}", wasPaused, previousCount),
"暂停=False, 连续失败=0",
reason,
traceId);
}
}

private void LogIgnoredWinEvent(
IntPtr hwnd,
int idObject,
int idChild,
string reason)
{
if (string.IsNullOrEmpty(reason)) return;

bool shouldLog = false;
lock (_stateSync)
{
if (DateTime.UtcNow - _lastIgnoredWinEventLogUtc > TimeSpan.FromSeconds(5))
{
_lastIgnoredWinEventLogUtc = DateTime.UtcNow;
shouldLog = true;
}
}

if (shouldLog)
{
AuditLogger.Debug(
"桌面位置事件已忽略",
string.Format(
"原因={0}; hwnd=0x{1:X}, idObject={2}, idChild={3}。忽略非具体桌面图标事件，避免RDP/Explorer重绘触发恢复。",
reason,
hwnd.ToInt64(),
idObject,
idChild),
AuditLogger.CurrentTraceId);
}
}

private void OnCheckExplorerProcess(object state)
{
try
{
int currentPid = GetExplorerPid();
if (currentPid > 0 && currentPid != _lastExplorerPid)
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogStateChange("Explorer进程变更检测", "explorer.exe", _lastExplorerPid.ToString(), currentPid.ToString(), "检测到资源管理器重启", traceId);
_lastExplorerPid = currentPid;

Thread.Sleep(1000);
RegisterDesktopLocationHook();
ApplyCurrentLayout("Explorer重启恢复");
}
}
catch (Exception ex)
{
AuditLogger.LogException(
"Explorer进程变更检测",
ex.GetType().Name,
ex.Message,
"Explorer重启监控本轮未能完成，将等待下一个5秒检测周期",
true,
ex,
AuditLogger.CurrentTraceId);
}
}

private int GetExplorerPid()
{
try
{
Process[] procs = Process.GetProcessesByName("explorer");
if (procs.Length > 0)
{
return procs[0].Id;
}
}
catch { }
return 0;
}

public HistoryLayoutRecord SaveCurrentAsExact()
{
string traceId = AuditLogger.GenerateTraceId();
return SaveCurrentAsExact(traceId, "托盘右键菜单");
}

private HistoryLayoutRecord SaveCurrentAsExact(string traceId, string source)
{
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival("SaveCurrentAsExact", source, "保存当前布局和历史截图", "捕获当前桌面坐标、更新精确Profile并保存全桌面截图", traceId);

Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
_store.SaveExactProfile(monitor, currentIcons);
HistoryLayoutRecord historyRecord = _historyStore.CreateRecord(monitor, currentIcons, traceId);

AuditLogger.LogResponseReturn(
"SaveCurrentAsExact",
200,
string.Format("已保存{0}个图标坐标，历史记录={1}", currentIcons.Count, historyRecord.Id),
0,
"成功",
"精确配置和带截图历史记录均已更新",
traceId);
return historyRecord;
}

public void SaveCurrentAsBase()
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival("SaveCurrentAsBase", "托盘右键菜单", "保存当前布局为基准配置", "捕获当前桌面坐标并保存为自适应换算源", traceId);

Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
_store.SaveBaseProfile(monitor, currentIcons);

AuditLogger.LogResponseReturn("SaveCurrentAsBase", 200, string.Format("已保存 {0} 个图标坐标为基准", currentIcons.Count), 0, "成功", "基准配置已更新", traceId);
}

public List<HistoryLayoutRecord> GetHistoryRecords()
{
return _historyStore.GetRecords();
}

public bool DeleteHistoryRecord(HistoryLayoutRecord record)
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"DeleteHistoryRecord",
"历史布局预览按钮",
record != null ? record.MenuText : "记录为空",
"删除历史Profile和对应全桌面截图",
traceId);

bool deletedBase;
bool success = _historyStore.DeleteRecord(record, out deletedBase);
if (success && deletedBase)
{
_store.ClearBaseProfile();
}

AuditLogger.LogResponseReturn(
"DeleteHistoryRecord",
success ? 200 : 500,
string.Format("删除结果={0}, 删除的是基准={1}", success, deletedBase),
0,
success ? "成功" : "失败",
success ? "历史记录和截图已删除" : "历史记录保留",
traceId);
return success;
}

public void SetHistoryRecordAsBase(HistoryLayoutRecord record)
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"SetHistoryRecordAsBase",
"历史布局预览按钮",
record != null ? record.MenuText : "记录为空",
"把历史布局设为新的自适应基准",
traceId);

if (record == null || record.Profile == null)
{
AuditLogger.LogRejected(
"设置历史基准",
"历史记录或Profile为空",
400,
"基准未改变",
"重新打开历史布局管理后再试",
traceId);
return;
}

_store.SetBaseProfileFromHistory(record.Profile);
_historyStore.MarkAsBase(record);
AuditLogger.LogResponseReturn(
"SetHistoryRecordAsBase",
200,
record.MenuText,
0,
"成功",
"新的自适应基准已生效",
traceId);
}

public void ApplyHistoryRecord(HistoryLayoutRecord record)
{
if (record == null || record.Profile == null) return;

string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
AuditLogger.LogRequestArrival(
"ApplyHistoryRecord",
"历史布局预览按钮",
record.MenuText,
"将历史布局应用到当前桌面；模式不同时自动执行结构保持型换算",
traceId);

lock (_stateSync)
{
if (_isApplying) return;
_isApplying = true;
_suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(2);
}

try
{
MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
Dictionary<string, IconPositionItem> currentIcons =
IconAccessor.ReadCurrentIconPositions();
DesktopProfile runtimeGeometry = BuildRuntimeGeometry(monitor, currentIcons);
DesktopProfile availableHistoryProfile =
CreateCurrentIconSubset(record.Profile, currentIcons);
if (availableHistoryProfile.Icons.Count == 0)
{
AuditLogger.LogRejected(
"历史布局可用图标检查",
"历史记录中的图标当前都不存在于桌面",
404,
"不写入任何坐标，避免过期历史布局影响当前桌面",
"请在当前显示环境重新保存一套布局",
traceId);
return;
}

Dictionary<string, IconPositionItem> positions;
bool exactMode =
string.Equals(availableHistoryProfile.Resolution, monitor.ResolutionKey, StringComparison.OrdinalIgnoreCase) &&
availableHistoryProfile.Dpi == monitor.Dpi &&
string.Equals(availableHistoryProfile.MonitorFingerprint, monitor.MonitorFingerprint, StringComparison.OrdinalIgnoreCase);

ProfileGeometryCompatibility compatibility = exactMode
? AdaptiveMapper.CheckRuntimeGeometryCompatibility(availableHistoryProfile, runtimeGeometry)
: null;

if (exactMode && compatibility.IsCompatible)
{
positions = availableHistoryProfile.Icons;
AuditLogger.LogRuleDecision(
"历史布局应用模式",
"显示器三元组与当前Explorer网格均兼容",
"直接应用精确历史坐标",
"工作区、网格原点和网格步进未发生变化",
traceId);
}
else
{
positions = AdaptiveMapper.Map(
availableHistoryProfile,
monitor,
runtimeGeometry);
AuditLogger.LogRuleDecision(
"历史布局应用模式",
string.Format(
"历史={0}@{1}%, 当前={2}@{3}%, 网格兼容性={4}",
availableHistoryProfile.Resolution,
(int)Math.Round(availableHistoryProfile.Dpi * 100.0 / 96.0),
monitor.ResolutionKey,
monitor.ScalePercent,
compatibility != null ? compatibility.Reason : "显示器三元组不一致"),
"按当前Explorer真实网格进行结构保持型自适应应用",
"避免历史绝对坐标与当前网格不兼容时被Explorer吸附到错误位置",
traceId);
}

int applied = IconAccessor.ApplyPositions(positions);
AuditLogger.LogResponseReturn(
"ApplyHistoryRecord",
200,
string.Format("目标图标数={0}, 匹配/写入数={1}", positions.Count, applied),
0,
"成功",
"历史布局已应用到当前桌面",
traceId);
}
finally
{
lock (_stateSync)
{
_isApplying = false;
_suppressLocationEventsUntilUtc = DateTime.UtcNow.AddSeconds(1.5);
}
}
}

private void EnsureBaselineIntegrity()
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.CurrentTraceId = traceId;
try
{
Dictionary<string, IconPositionItem> currentIcons = IconAccessor.ReadCurrentIconPositions();
if (currentIcons.Count == 0)
{
AuditLogger.LogRejected(
"启动配置完整性检查",
"当前未读取到桌面图标",
404,
"不覆盖现有配置，避免误写空布局",
"等待Explorer桌面就绪后重试",
traceId);
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

AuditLogger.LogRuleDecision(
"启动配置完整性检查",
string.Format(
"当前图标数={0}, 基准图标数={1}, 最低合理数={2}, 几何信息缺失={3}",
currentIcons.Count,
baseProfile != null && baseProfile.Icons != null ? baseProfile.Icons.Count : 0,
minimumReasonableCount,
geometryMissing),
profileMissing || iconCoverageInvalid || geometryMissing ? "重建当前基准" : "保留已有基准",
profileMissing
? "尚未保存基准布局"
: iconCoverageInvalid
? "旧配置只覆盖少量图标，疑似测试数据或损坏配置"
: geometryMissing
? "旧版配置缺少物理工作区和网格拓扑参数"
: "配置完整且图标覆盖率正常",
traceId);

if (profileMissing || iconCoverageInvalid || geometryMissing)
{
MonitorProfileInfo monitor = DisplayInfo.GetPrimaryMonitor();
_store.SaveBaseProfile(monitor, currentIcons);
_store.SaveExactProfile(monitor, currentIcons);
AuditLogger.LogResponseReturn(
"启动配置完整性检查",
200,
string.Format("已用当前真实桌面重建基准和精确配置，图标数={0}", currentIcons.Count),
0,
"成功",
"旧版错误分辨率/DPI配置不会再破坏当前桌面布局",
traceId);
}
}
catch (Exception ex)
{
AuditLogger.LogException(
"启动配置完整性检查",
ex.GetType().Name,
ex.Message,
"保留现有配置并继续启动",
true,
ex,
traceId);
}
}

public void Dispose()
{
if (_winEventHook != IntPtr.Zero)
{
User32.UnhookWinEvent(_winEventHook);
_winEventHook = IntPtr.Zero;
}
if (_displayChangeDebounceTimer != null) _displayChangeDebounceTimer.Dispose();
if (_locationChangeDebounceTimer != null) _locationChangeDebounceTimer.Dispose();
if (_displayStabilizationTimer != null) _displayStabilizationTimer.Dispose();
if (_explorerMonitorTimer != null) _explorerMonitorTimer.Dispose();
}
}
}
