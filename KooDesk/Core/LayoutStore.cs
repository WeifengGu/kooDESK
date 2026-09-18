using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KooDesk.Native;

namespace KooDesk.Core
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

        public int ScalePercent
        {
            get { return (int)Math.Round(Math.Max(96, Dpi) * 100.0 / 96.0); }
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

        public const int CurrentVersion = 3;

        public int Version { get; set; }
        public DesktopProfile BaseProfile { get; set; }
        public List<DesktopProfile> ExactProfiles { get; set; }

        public LayoutConfig()
        {
            Version = CurrentVersion;
            ExactProfiles = new List<DesktopProfile>();
        }
    }

    public class LayoutStore
    {

        public const string ConfigFileName = "kooDESK.json";

        private const string LegacyConfigFileName = "layout.json";

        private const string LegacyAppDataDirectoryName = "DesktopIconLock";

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
            _configFilePath = Path.Combine(_configDirectory, ConfigFileName);

            if (!Directory.Exists(_configDirectory))
            {
                Directory.CreateDirectory(_configDirectory);
            }

            MigrateLegacyConfig();

            RemoveLegacyHistoryDirectory();

            LoadConfig();
        }

        private void RemoveLegacyHistoryDirectory()
        {
            string legacyHistoryDirectory = Path.Combine(_configDirectory, "history");
            if (!Directory.Exists(legacyHistoryDirectory)) return;

            try
            {
                Directory.Delete(legacyHistoryDirectory, true);
            }
            catch
            {

            }
        }

        private void MigrateLegacyConfig()
        {
            try
            {
                if (File.Exists(_configFilePath)) return;

                string legacyPortableFile = Path.Combine(_configDirectory, LegacyConfigFileName);
                if (File.Exists(legacyPortableFile))
                {
                    File.Move(legacyPortableFile, _configFilePath);
                    return;
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string legacyAppDataFile = Path.Combine(
                    Path.Combine(localAppData, LegacyAppDataDirectoryName),
                    LegacyConfigFileName);
                if (File.Exists(legacyAppDataFile))
                {
                    File.Copy(legacyAppDataFile, _configFilePath, true);
                }
            }
            catch
            {

            }
        }

        public void LoadConfig()
        {
            lock (_fileLock)
            {
                CurrentConfig = ReadConfigFromDisk();
            }
        }

        private LayoutConfig ReadConfigFromDisk()
        {
            if (File.Exists(_configFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_configFilePath, Encoding.UTF8);
                    LayoutConfig loaded = ParseJson(json);
                    return loaded;
                }
                catch
                {

                }
            }

            return new LayoutConfig();
        }

        public bool SaveConfig()
        {

            lock (_fileLock)
            {
                try
                {
                    string json = SerializeJson(CurrentConfig);
                    string temporaryPath = _configFilePath + ".tmp";
                    File.WriteAllText(temporaryPath, json, Encoding.UTF8);
                    if (File.Exists(_configFilePath))
                    {
                        File.Delete(_configFilePath);
                    }
                    File.Move(temporaryPath, _configFilePath);

                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool SaveBaseProfile(MonitorProfileInfo monitor, Dictionary<string, IconPositionItem> icons)
        {

            bool saved;
            lock (_fileLock)
            {
                CurrentConfig.BaseProfile = BuildProfile(monitor, icons);
                CurrentConfig.Version = LayoutConfig.CurrentVersion;
                saved = SaveConfig();
            }

            return saved;
        }

        public bool SaveBaseAndExactProfile(MonitorProfileInfo monitor, Dictionary<string, IconPositionItem> icons)
        {

            bool saved;
            lock (_fileLock)
            {
                CurrentConfig.BaseProfile = BuildProfile(monitor, icons);
                CurrentConfig.ExactProfiles.RemoveAll(delegate(DesktopProfile p)
                {
                    return IsSameEnvironment(p, monitor);
                });
                CurrentConfig.ExactProfiles.Add(BuildProfile(monitor, icons));
                CurrentConfig.Version = LayoutConfig.CurrentVersion;
                saved = SaveConfig();
            }

            return saved;
        }

        public bool SaveExactProfile(MonitorProfileInfo monitor, Dictionary<string, IconPositionItem> icons)
        {

            bool saved;
            lock (_fileLock)
            {
                CurrentConfig.ExactProfiles.RemoveAll(delegate(DesktopProfile p)
                {
                return IsSameEnvironment(p, monitor);
                });

                DesktopProfile newExact = BuildProfile(monitor, icons);
                CurrentConfig.ExactProfiles.Add(newExact);

                if (CurrentConfig.BaseProfile == null)
                {
                    CurrentConfig.BaseProfile = BuildProfile(monitor, icons);
                }

                CurrentConfig.Version = LayoutConfig.CurrentVersion;
                saved = SaveConfig();
            }

            return saved;
        }

        private static DesktopProfile BuildProfile(MonitorProfileInfo monitor, Dictionary<string, IconPositionItem> icons)
        {
            DesktopProfile profile = new DesktopProfile();
            profile.Resolution = monitor.ResolutionKey;
            profile.Dpi = monitor.Dpi;
            profile.MonitorFingerprint = monitor.MonitorFingerprint;
            profile.Icons = icons != null
            ? new Dictionary<string, IconPositionItem>(icons, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IconPositionItem>(StringComparer.OrdinalIgnoreCase);
            AdaptiveMapper.PopulateProfileGeometry(profile, monitor, profile.Icons);
            return profile;
        }

        private static bool IsSameEnvironment(DesktopProfile profile, MonitorProfileInfo monitor)
        {
            if (profile == null || monitor == null) return false;

            return string.Equals(profile.Resolution, monitor.ResolutionKey, StringComparison.OrdinalIgnoreCase) &&
            profile.Dpi == monitor.Dpi &&
            string.Equals(profile.MonitorFingerprint, monitor.MonitorFingerprint, StringComparison.OrdinalIgnoreCase);
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

            if (source.Icons != null)
            {
                foreach (KeyValuePair<string, IconPositionItem> pair in source.Icons)
                {
                    IconPositionItem item = pair.Value;
                    clone.Icons[pair.Key] = item == null
                    ? null
                    : new IconPositionItem
                    {
                        Key = item.Key,
                        DisplayName = item.DisplayName,
                        X = item.X,
                        Y = item.Y
                    };
                }
            }

            return clone;
        }

        public DesktopProfile FindExactProfile(string resolution, int dpi, string monitorFingerprint)
        {

            for (int i = 0; i < CurrentConfig.ExactProfiles.Count; i++)
            {
                DesktopProfile p = CurrentConfig.ExactProfiles[i];
                if (string.Equals(p.Resolution, resolution, StringComparison.OrdinalIgnoreCase) &&
                    p.Dpi == dpi &&
                    string.Equals(p.MonitorFingerprint, monitorFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }

            return null;
        }

        public static string SerializeJson(LayoutConfig config)
        {
            if (config == null) config = new LayoutConfig();

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
            if (profile == null) profile = new DesktopProfile();

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

            int total = profile.Icons != null ? profile.Icons.Count : 0;
            int count = 0;
            if (profile.Icons != null)
            {
                foreach (KeyValuePair<string, IconPositionItem> kvp in profile.Icons)
                {
                    count++;
                    IconPositionItem item = kvp.Value;
                    sb.Append(string.Format(
                        "{0}  \"{1}\": {{ \"displayName\": \"{2}\", \"x\": {3}, \"y\": {4} }}",
                        indent,
                        EscapeString(kvp.Key),
                        EscapeString(item != null ? item.DisplayName : kvp.Key),
                        item != null ? item.X : 0,
                        item != null ? item.Y : 0));
                    if (count < total) sb.AppendLine(",");
                    else sb.AppendLine();
                }
            }

            sb.AppendLine(string.Format("{0}}}", indent));
        }

        public static LayoutConfig ParseJson(string json)
        {
            LayoutConfig config = new LayoutConfig();
            if (string.IsNullOrWhiteSpace(json)) return config;

            int version;
            if (TryExtractInt(json, "version", out version) && version > 0) config.Version = version;

            int baseBrace = FindObjectValueStart(json, "baseProfile");
            if (baseBrace >= 0)
            {
                int closeBrace = FindMatchingBrace(json, baseBrace);
                if (closeBrace > baseBrace)
                {
                    config.BaseProfile = ParseProfile(json.Substring(baseBrace, closeBrace - baseBrace + 1));
                }
            }

            int exactBracket = FindValueStart(json, "exactProfiles", '[');
            if (exactBracket >= 0)
            {
                int closeBracket = FindMatchingBracket(json, exactBracket);
                if (closeBracket > exactBracket)
                {
                    string arrayContent = json.Substring(exactBracket + 1, closeBracket - exactBracket - 1);
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

            int iconsBrace = FindObjectValueStart(json, "icons");
            if (iconsBrace >= 0)
            {
                int closeBrace = FindMatchingBrace(json, iconsBrace);
                if (closeBrace > iconsBrace)
                {
                    string iconsContent = json.Substring(iconsBrace + 1, closeBrace - iconsBrace - 1);
                    ParseIconsDictionary(iconsContent, profile.Icons);
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
                int x = ParseInt(ExtractJsonValue(iconObjJson, "x"), 0);
                int y = ParseInt(ExtractJsonValue(iconObjJson, "y"), 0);

                IconPositionItem item = new IconPositionItem();
                item.Key = key;
                item.DisplayName = dispName;
                item.X = x;
                item.Y = y;
                dict[key] = item;

                cursor = objEnd + 1;
            }
        }

        private static bool TryFindValueStart(string json, string key, out int valueStart)
        {
            valueStart = 0;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return false;

            string token = "\"" + key + "\"";
            int searchFrom = 0;
            while (true)
            {
                int kIdx = json.IndexOf(token, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (kIdx < 0) return false;

                int cursor = kIdx + token.Length;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
                if (cursor < json.Length && json[cursor] == ':')
                {
                    cursor++;
                    while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
                    valueStart = cursor;
                    return true;
                }

                searchFrom = kIdx + token.Length;
            }
        }

        private static int FindValueStart(string json, string key, char expected)
        {
            int start;
            if (TryFindValueStart(json, key, out start) && start < json.Length && json[start] == expected)
            {
                return start;
            }
            return -1;
        }

        private static int FindObjectValueStart(string json, string key)
        {
            return FindValueStart(json, key, '{');
        }

        private static bool TryExtractInt(string json, string key, out int value)
        {
            return int.TryParse(ExtractJsonValue(json, key), out value);
        }

        private static string ExtractJsonValue(string json, string key)
        {
            int cursor;
            if (!TryFindValueStart(json, key, out cursor) || cursor >= json.Length) return null;

            if (json[cursor] == '\"')
            {
                int endQuote = FindClosingQuote(json, cursor);
                if (endQuote > cursor)
                {
                    return UnescapeString(json.Substring(cursor + 1, endQuote - cursor - 1));
                }
                return null;
            }

            int end = cursor;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' && !char.IsWhiteSpace(json[end]))
            {
                end++;
            }
            return json.Substring(cursor, end - cursor).Trim();
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static int FindClosingQuote(string s, int openQuote)
        {
            bool escaped = false;
            for (int i = openQuote + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '\"') return i;
            }
            return -1;
        }

        private static int FindMatchingBrace(string s, int openBrace)
        {
            return FindMatchingDelimiter(s, openBrace, '{', '}');
        }

        private static int FindMatchingBracket(string s, int openBracket)
        {
            return FindMatchingDelimiter(s, openBracket, '[', ']');
        }

        private static int FindMatchingDelimiter(string s, int open, char openChar, char closeChar)
        {
            if (s == null || open < 0 || open >= s.Length || s[open] != openChar) return -1;

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escaped) { escaped = false; }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '\"') { inString = false; }
                    continue;
                }

                if (c == '\"') inString = true;
                else if (c == openChar) depth++;
                else if (c == closeChar)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static string EscapeString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                    if (c < ' ')
                    {
                        sb.Append(string.Format("\\u{0:X4}", (int)c));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
                }
            }
            return sb.ToString();
        }

        private static string UnescapeString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('\\') < 0) return s;

            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char next = s[++i];
                switch (next)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                    if (i + 4 < s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                            break;
                        }
                    }
                    sb.Append(next);
                    break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }
    }
}
