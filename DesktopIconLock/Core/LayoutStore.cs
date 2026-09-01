using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopIconLock.Common;
using DesktopIconLock.Native;

namespace DesktopIconLock.Core
{
public class DesktopProfile
{
public string Resolution { get; set; }
public int Dpi { get; set; }
public string MonitorFingerprint { get; set; }
public int WorkAreaWidth { get; set; }
public int WorkAreaHeight { get; set; }
public int GridOriginX { get; set; }
public int GridOriginY { get; set; }
public int GridSpacingX { get; set; }
public int GridSpacingY { get; set; }
public Dictionary<string, IconPositionItem> Icons { get; set; }

public DesktopProfile()
{
Icons = new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);
}

public override string ToString()
{
return string.Format(
"Profile [分辨率={0}, DPI={1}, 指纹={2}, 工作区={3}x{4}, 网格原点=({5},{6}), 网格={7}x{8}, 图标数={9}]",
Resolution,
Dpi,
MonitorFingerprint,
WorkAreaWidth,
WorkAreaHeight,
GridOriginX,
GridOriginY,
GridSpacingX,
GridSpacingY,
Icons != null ? Icons.Count : 0);
}
}

public class LayoutConfig
{
public int Version { get; set; }
public DesktopProfile BaseProfile { get; set; }
public List<DesktopProfile> ExactProfiles { get; set; }

public LayoutConfig()
{
Version = 3;
ExactProfiles = new List<DesktopProfile>();
}
}

public class LayoutStore
{
private static readonly object _fileLock = new object();
private readonly string _configDirectory;
private readonly string _configFilePath;

public LayoutConfig CurrentConfig { get; private set; }
public string ConfigDirectory { get { return _configDirectory; } }

public LayoutStore()
    : this(null)
{
}

public LayoutStore(string configDirectory)
{
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
_configDirectory = string.IsNullOrEmpty(configDirectory)
? baseDir
: configDirectory;
_configFilePath = Path.Combine(_configDirectory, "layout.json");

if (!Directory.Exists(_configDirectory))
{
Directory.CreateDirectory(_configDirectory);
}

// 便携迁移：如果程序所在目录尚无 layout.json，但旧版 %LOCALAPPDATA%\DesktopIconLock 中存在，自动静默迁移
TryMigrateFromLegacyAppData();

LoadConfig();
}

private void TryMigrateFromLegacyAppData()
{
try
{
if (File.Exists(_configFilePath)) return;

string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
string legacyDir = Path.Combine(localAppData, "DesktopIconLock");
string legacyConfigFile = Path.Combine(legacyDir, "layout.json");

if (File.Exists(legacyConfigFile))
{
File.Copy(legacyConfigFile, _configFilePath, true);
AuditLogger.Info(
"便携模式迁移",
string.Format("已将旧版配置文件从 {0} 迁移至程序便携目录 {1}", legacyConfigFile, _configFilePath),
AuditLogger.CurrentTraceId);
}
}
catch (Exception ex)
{
AuditLogger.LogException(
"便携模式迁移",
ex.GetType().Name,
ex.Message,
"未完成旧配置迁移，将创建全新配置",
true,
ex,
AuditLogger.CurrentTraceId);
}
}

public void LoadConfig()
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.LogBusinessEntry("加载布局配置", _configFilePath, "读取持久化JSON配置及Profile列表", traceId);

lock (_fileLock)
{
if (File.Exists(_configFilePath))
{
try
{
string json = File.ReadAllText(_configFilePath, Encoding.UTF8);
CurrentConfig = ParseJson(json);
AuditLogger.LogStorageOperation("加载配置文件", "LayoutConfig", string.Format("路径:{0}", _configFilePath), 
(CurrentConfig.BaseProfile != null ? 1 : 0) + CurrentConfig.ExactProfiles.Count, 
string.Format("成功加载基准配置: {0}，精确配置数: {1}", CurrentConfig.BaseProfile != null, CurrentConfig.ExactProfiles.Count), traceId);
return;
}
catch (Exception ex)
{
AuditLogger.LogException("加载配置文件", "JsonParseException", ex.Message, "将初始化全新空配置", true, ex, traceId);
}
}

CurrentConfig = new LayoutConfig();
AuditLogger.Info("配置初始化", "未发现旧配置或配置解析失败，已初始化空白配置对象", traceId);
}
}

public void SaveConfig()
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.LogBusinessEntry("保存布局配置", _configFilePath, "序列化布局配置并创建带时间戳备份", traceId);

lock (_fileLock)
{
try
{
string json = SerializeJson(CurrentConfig);
File.WriteAllText(_configFilePath, json, Encoding.UTF8);

AuditLogger.LogStorageOperation("写入主配置文件", "layout.json", string.Format("路径:{0}", _configFilePath), 1, "主配置文件更新成功", traceId);

CreateBackup(json);
}
catch (Exception ex)
{
AuditLogger.LogException("保存布局配置", "FileWriteException", ex.Message, "布局未能落盘", false, ex, traceId);
}
}
}

public void SaveBaseProfile(MonitorProfileInfo monitor, Dictionary<string, IconPositionItem> icons)
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.LogBusinessEntry("设置基准Profile", string.Format("分辨率={0}, 图标数={1}", monitor.ResolutionKey, icons.Count), "保存用户设定的基准分辨率配置，用于后续自适应换算", traceId);

CurrentConfig.BaseProfile = new DesktopProfile();
CurrentConfig.BaseProfile.Resolution = monitor.ResolutionKey;
CurrentConfig.BaseProfile.Dpi = monitor.Dpi;
CurrentConfig.BaseProfile.MonitorFingerprint = monitor.MonitorFingerprint;
CurrentConfig.BaseProfile.Icons = new Dictionary<string, IconPositionItem>(icons, StringComparer.OrdinalIgnoreCase);
AdaptiveMapper.PopulateProfileGeometry(CurrentConfig.BaseProfile, monitor, icons);
CurrentConfig.Version = 3;

SaveConfig();
AuditLogger.Info("基准配置保存完成", string.Format("已成功保存基准Profile: {0}", CurrentConfig.BaseProfile), traceId);
}

public void SaveExactProfile(MonitorProfileInfo monitor, Dictionary<string, IconPositionItem> icons)
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.LogBusinessEntry("设置精确Profile", string.Format("分辨率={0}, DPI={1}, 图标数={2}", monitor.ResolutionKey, monitor.Dpi, icons.Count), "保存当前具体分辨率下的精确锁定坐标", traceId);

CurrentConfig.ExactProfiles.RemoveAll(delegate(DesktopProfile p)
{
return string.Equals(p.Resolution, monitor.ResolutionKey, StringComparison.OrdinalIgnoreCase) &&
 p.Dpi == monitor.Dpi &&
string.Equals(p.MonitorFingerprint, monitor.MonitorFingerprint, StringComparison.OrdinalIgnoreCase);
});

DesktopProfile newExact = new DesktopProfile();
newExact.Resolution = monitor.ResolutionKey;
newExact.Dpi = monitor.Dpi;
newExact.MonitorFingerprint = monitor.MonitorFingerprint;
newExact.Icons = new Dictionary<string, IconPositionItem>(icons, StringComparer.OrdinalIgnoreCase);
AdaptiveMapper.PopulateProfileGeometry(newExact, monitor, icons);

CurrentConfig.ExactProfiles.Add(newExact);

if (CurrentConfig.BaseProfile == null)
{
CurrentConfig.BaseProfile = new DesktopProfile();
CurrentConfig.BaseProfile.Resolution = monitor.ResolutionKey;
CurrentConfig.BaseProfile.Dpi = monitor.Dpi;
CurrentConfig.BaseProfile.MonitorFingerprint = monitor.MonitorFingerprint;
CurrentConfig.BaseProfile.Icons = new Dictionary<string, IconPositionItem>(icons, StringComparer.OrdinalIgnoreCase);
AdaptiveMapper.PopulateProfileGeometry(CurrentConfig.BaseProfile, monitor, icons);
AuditLogger.Info("自动初始化基准配置", "基准配置原为空，已同步设为基准Profile", traceId);
}

CurrentConfig.Version = 3;
SaveConfig();
AuditLogger.Info("精确配置保存完成", string.Format("已成功保存精确Profile: {0}", newExact), traceId);
}

public void SetBaseProfileFromHistory(DesktopProfile profile)
{
string traceId = AuditLogger.GenerateTraceId();
AuditLogger.LogRequestArrival(
"LayoutStore.SetBaseProfileFromHistory",
"历史布局管理",
string.Format("分辨率={0}, DPI={1}, 图标数={2}", profile.Resolution, profile.Dpi, profile.Icons.Count),
"将选中的历史布局复制为自适应基准Profile",
traceId);

CurrentConfig.BaseProfile = CloneProfile(profile);
CurrentConfig.Version = 3;
SaveConfig();

AuditLogger.LogResponseReturn(
"LayoutStore.SetBaseProfileFromHistory",
200,
string.Format("基准已更新为{0}@{1}%", profile.Resolution, (int)Math.Round(profile.Dpi * 100.0 / 96.0)),
0,
"成功",
"今后无精确Profile时将以该历史布局进行结构保持型自适应换算",
traceId);
}

public void ClearBaseProfile()
{
string traceId = AuditLogger.GenerateTraceId();
CurrentConfig.BaseProfile = null;
SaveConfig();
AuditLogger.LogStorageOperation(
"清除基准Profile",
"LayoutConfig.BaseProfile",
"历史基准记录已删除",
1,
"基准已清除",
traceId);
}

public static DesktopProfile CloneProfile(DesktopProfile source)
{
if (source == null) return null;

DesktopProfile clone = new DesktopProfile();
clone.Resolution = source.Resolution;
clone.Dpi = source.Dpi;
clone.MonitorFingerprint = source.MonitorFingerprint;
clone.WorkAreaWidth = source.WorkAreaWidth;
clone.WorkAreaHeight = source.WorkAreaHeight;
clone.GridOriginX = source.GridOriginX;
clone.GridOriginY = source.GridOriginY;
clone.GridSpacingX = source.GridSpacingX;
clone.GridSpacingY = source.GridSpacingY;

foreach (KeyValuePair<string, IconPositionItem> pair in source.Icons)
{
IconPositionItem item = pair.Value;
clone.Icons[pair.Key] = new IconPositionItem
{
Key = item.Key,
DisplayName = item.DisplayName,
X = item.X,
Y = item.Y
};
}

return clone;
}

public DesktopProfile FindExactProfile(string resolution, int dpi, string monitorFingerprint)
{
string traceId = AuditLogger.CurrentTraceId;
AuditLogger.LogBusinessEntry("检索精确Profile", string.Format("目标=[分辨率:{0}, DPI:{1}, 指纹:{2}]", resolution, dpi, monitorFingerprint), "查找是否预存过该分辨率的专用坐标", traceId);

for (int i = 0; i < CurrentConfig.ExactProfiles.Count; i++)
{
DesktopProfile p = CurrentConfig.ExactProfiles[i];
if (string.Equals(p.Resolution, resolution, StringComparison.OrdinalIgnoreCase) &&
p.Dpi == dpi &&
string.Equals(p.MonitorFingerprint, monitorFingerprint, StringComparison.OrdinalIgnoreCase))
{
AuditLogger.LogRuleDecision("精确Profile三元组全匹配", string.Format("目标:{0}_{1}_{2}", resolution, dpi, monitorFingerprint), "匹配成功", "完全符合三元组定义", traceId);
return p;
}
}

// 旧版会忽略DPI甚至显示器指纹，仅按分辨率套用精确坐标。
// 这正是125%布局被错误套到100%/150%后结构崩坏的主要原因之一。
AuditLogger.LogRuleDecision(
"精确Profile匹配",
string.Format("目标:{0}, DPI:{1}, 指纹:{2}", resolution, dpi, monitorFingerprint),
"未找到严格三元组匹配",
"禁止跨DPI/跨显示器误用精确坐标，转入结构保持型自适应换算",
traceId);
return null;
}

private void CreateBackup(string jsonContent)
{
try
{
string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
string backupName = string.Format("layout.bak.{0}.json", timestamp);
string backupPath = Path.Combine(_configDirectory, backupName);
File.WriteAllText(backupPath, jsonContent, Encoding.UTF8);

string[] bakFiles = Directory.GetFiles(_configDirectory, "layout.bak.*.json");
if (bakFiles.Length > 5)
{
Array.Sort(bakFiles);
int deleteCount = bakFiles.Length - 5;
for (int i = 0; i < deleteCount; i++)
{
try { File.Delete(bakFiles[i]); } catch { }
}
}
}
catch { }
}

public static string SerializeJson(LayoutConfig config)
{
StringBuilder sb = new StringBuilder();
sb.AppendLine("{");
sb.AppendLine(string.Format("  \"version\": {0},", config.Version));

if (config.BaseProfile != null)
{
sb.AppendLine("  \"baseProfile\": {");
SerializeProfileContent(sb, config.BaseProfile, "    ");
sb.AppendLine("  },");
}
else
{
sb.AppendLine("  \"baseProfile\": null,");
}

sb.AppendLine("  \"exactProfiles\": [");
for (int i = 0; i < config.ExactProfiles.Count; i++)
{
sb.AppendLine("    {");
SerializeProfileContent(sb, config.ExactProfiles[i], "      ");
sb.Append("    }");
if (i < config.ExactProfiles.Count - 1) sb.AppendLine(",");
else sb.AppendLine();
}
sb.AppendLine("  ]");
sb.AppendLine("}");
return sb.ToString();
}

private static void SerializeProfileContent(StringBuilder sb, DesktopProfile profile, string indent)
{
sb.AppendLine(string.Format("{0}\"resolution\": \"{1}\",", indent, EscapeString(profile.Resolution)));
sb.AppendLine(string.Format("{0}\"dpi\": {1},", indent, profile.Dpi));
sb.AppendLine(string.Format("{0}\"monitorFingerprint\": \"{1}\",", indent, EscapeString(profile.MonitorFingerprint)));
sb.AppendLine(string.Format("{0}\"workAreaWidth\": {1},", indent, profile.WorkAreaWidth));
sb.AppendLine(string.Format("{0}\"workAreaHeight\": {1},", indent, profile.WorkAreaHeight));
sb.AppendLine(string.Format("{0}\"gridOriginX\": {1},", indent, profile.GridOriginX));
sb.AppendLine(string.Format("{0}\"gridOriginY\": {1},", indent, profile.GridOriginY));
sb.AppendLine(string.Format("{0}\"gridSpacingX\": {1},", indent, profile.GridSpacingX));
sb.AppendLine(string.Format("{0}\"gridSpacingY\": {1},", indent, profile.GridSpacingY));
sb.AppendLine(string.Format("{0}\"icons\": {{", indent));

int count = 0;
foreach (KeyValuePair<string, IconPositionItem> kvp in profile.Icons)
{
count++;
IconPositionItem item = kvp.Value;
sb.Append(string.Format("{0}  \"{1}\": {{ \"displayName\": \"{2}\", \"x\": {3}, \"y\": {4} }}", indent, EscapeString(kvp.Key), EscapeString(item.DisplayName), item.X, item.Y));
if (count < profile.Icons.Count) sb.AppendLine(",");
else sb.AppendLine();
}

sb.AppendLine(string.Format("{0}}}", indent));
}

public static LayoutConfig ParseJson(string json)
{
LayoutConfig config = new LayoutConfig();
if (string.IsNullOrWhiteSpace(json)) return config;

int baseIdx = json.IndexOf("\"baseProfile\"", StringComparison.OrdinalIgnoreCase);
if (baseIdx >= 0)
{
int openBrace = json.IndexOf('{', baseIdx);
if (openBrace > 0)
{
int closeBrace = FindMatchingBrace(json, openBrace);
if (closeBrace > openBrace)
{
string baseJson = json.Substring(openBrace, closeBrace - openBrace + 1);
config.BaseProfile = ParseProfile(baseJson);
}
}
}

int exactIdx = json.IndexOf("\"exactProfiles\"", StringComparison.OrdinalIgnoreCase);
if (exactIdx >= 0)
{
int openBracket = json.IndexOf('[', exactIdx);
if (openBracket > 0)
{
int closeBracket = FindMatchingBracket(json, openBracket);
if (closeBracket > openBracket)
{
string arrayContent = json.Substring(openBracket + 1, closeBracket - openBracket - 1);
int cursor = 0;
while (cursor < arrayContent.Length)
{
int objStart = arrayContent.IndexOf('{', cursor);
if (objStart < 0) break;
int objEnd = FindMatchingBrace(arrayContent, objStart);
if (objEnd <= objStart) break;

string itemJson = arrayContent.Substring(objStart, objEnd - objStart + 1);
DesktopProfile profile = ParseProfile(itemJson);
if (profile != null)
{
config.ExactProfiles.Add(profile);
}
cursor = objEnd + 1;
}
}
}
}

return config;
}

private static DesktopProfile ParseProfile(string json)
{
if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;
DesktopProfile profile = new DesktopProfile();

profile.Resolution = ExtractJsonValue(json, "resolution") ?? "1920x1080";
string dpiStr = ExtractJsonValue(json, "dpi");
int dpi;
if (int.TryParse(dpiStr, out dpi)) profile.Dpi = dpi;
else profile.Dpi = 96;

profile.MonitorFingerprint = ExtractJsonValue(json, "monitorFingerprint") ?? "";
profile.WorkAreaWidth = ParseInt(ExtractJsonValue(json, "workAreaWidth"), 0);
profile.WorkAreaHeight = ParseInt(ExtractJsonValue(json, "workAreaHeight"), 0);
profile.GridOriginX = ParseInt(ExtractJsonValue(json, "gridOriginX"), 0);
profile.GridOriginY = ParseInt(ExtractJsonValue(json, "gridOriginY"), 0);
profile.GridSpacingX = ParseInt(ExtractJsonValue(json, "gridSpacingX"), 0);
profile.GridSpacingY = ParseInt(ExtractJsonValue(json, "gridSpacingY"), 0);

int iconsIdx = json.IndexOf("\"icons\"", StringComparison.OrdinalIgnoreCase);
if (iconsIdx >= 0)
{
int openBrace = json.IndexOf('{', iconsIdx);
if (openBrace > 0)
{
int closeBrace = FindMatchingBrace(json, openBrace);
if (closeBrace > openBrace)
{
string iconsContent = json.Substring(openBrace + 1, closeBrace - openBrace - 1);
ParseIconsDictionary(iconsContent, profile.Icons);
}
}
}

return profile;
}

private static void ParseIconsDictionary(string content, Dictionary<string, IconPositionItem> dict)
{
int cursor = 0;
while (cursor < content.Length)
{
int keyQuoteStart = content.IndexOf('\"', cursor);
if (keyQuoteStart < 0) break;
int keyQuoteEnd = FindClosingQuote(content, keyQuoteStart);
if (keyQuoteEnd < 0) break;

string rawKey = content.Substring(keyQuoteStart + 1, keyQuoteEnd - keyQuoteStart - 1);
string key = UnescapeString(rawKey);

int colon = content.IndexOf(':', keyQuoteEnd);
if (colon < 0) break;

int objStart = content.IndexOf('{', colon);
if (objStart < 0) break;

int objEnd = FindMatchingBrace(content, objStart);
if (objEnd < 0) break;

string iconObjJson = content.Substring(objStart, objEnd - objStart + 1);
string dispName = ExtractJsonValue(iconObjJson, "displayName") ?? key;
string xStr = ExtractJsonValue(iconObjJson, "x") ?? "0";
string yStr = ExtractJsonValue(iconObjJson, "y") ?? "0";

int x, y;
int.TryParse(xStr, out x);
int.TryParse(yStr, out y);

IconPositionItem item = new IconPositionItem();
item.Key = key;
item.DisplayName = dispName;
item.X = x;
item.Y = y;
dict[key] = item;

cursor = objEnd + 1;
}
}

private static string ExtractJsonValue(string json, string key)
{
int kIdx = json.IndexOf(string.Format("\"{0}\"", key), StringComparison.OrdinalIgnoreCase);
if (kIdx < 0) return null;
int colon = json.IndexOf(':', kIdx);
if (colon < 0) return null;

int cursor = colon + 1;
while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
if (cursor >= json.Length) return null;

if (json[cursor] == '\"')
{
int endQuote = FindClosingQuote(json, cursor);
if (endQuote > cursor)
{
return UnescapeString(json.Substring(cursor + 1, endQuote - cursor - 1));
}
}
else
{
int end = cursor;
while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' && !char.IsWhiteSpace(json[end]))
{
end++;
}
return json.Substring(cursor, end - cursor).Trim();
}
return null;
}

private static int ParseInt(string value, int fallback)
{
int parsed;
return int.TryParse(value, out parsed) ? parsed : fallback;
}

private static int FindClosingQuote(string s, int openQuote)
{
for (int i = openQuote + 1; i < s.Length; i++)
{
if (s[i] == '\"' && s[i - 1] != '\\') return i;
}
return -1;
}

private static int FindMatchingBrace(string s, int openBrace)
{
int depth = 0;
bool inString = false;
for (int i = openBrace; i < s.Length; i++)
{
char c = s[i];
if (c == '\"' && (i == 0 || s[i - 1] != '\\'))
{
inString = !inString;
}
else if (!inString)
{
if (c == '{') depth++;
else if (c == '}')
{
depth--;
if (depth == 0) return i;
}
}
}
return -1;
}

private static int FindMatchingBracket(string s, int openBracket)
{
int depth = 0;
bool inString = false;
for (int i = openBracket; i < s.Length; i++)
{
char c = s[i];
if (c == '\"' && (i == 0 || s[i - 1] != '\\'))
{
inString = !inString;
}
else if (!inString)
{
if (c == '[') depth++;
else if (c == ']')
{
depth--;
if (depth == 0) return i;
}
}
}
return -1;
}

private static string EscapeString(string s)
{
if (string.IsNullOrEmpty(s)) return "";
return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}

private static string UnescapeString(string s)
{
if (string.IsNullOrEmpty(s)) return "";
return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
}
}
}
