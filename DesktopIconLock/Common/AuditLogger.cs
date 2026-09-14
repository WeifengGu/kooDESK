using System;
using System.IO;
using System.Text;

namespace DesktopIconLock.Common
{
    public static class AuditLogger
    {
        private static readonly object _lock = new object();
        private static readonly string _logDirectory;
        private static readonly string _processTraceId = GenerateTraceId();
        [ThreadStatic]
        private static string _threadTraceId;

        static AuditLogger()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string projectLogDir = Path.Combine(baseDir, "logs");
                if (!Directory.Exists(projectLogDir))
                {
                    Directory.CreateDirectory(projectLogDir);
                }
                _logDirectory = projectLogDir;
            }
            catch
            {
                _logDirectory = Path.Combine(Path.GetTempPath(), "DesktopIconLock_logs");
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
            }
        }

        public static string CurrentTraceId
        {
            get
            {
                return string.IsNullOrEmpty(_threadTraceId)
                    ? _processTraceId
                    : _threadTraceId;
            }
            set { _threadTraceId = value; }
        }

        public static string GenerateTraceId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public static void SetNewTraceId()
        {
            CurrentTraceId = GenerateTraceId();
        }

        private static void WriteLog(string tag, string level, string message, string traceId)
        {
            string tid = traceId ?? CurrentTraceId;
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logLine = string.Format("[{0}] [{1}] [{2}] traceId={3} {4}", now, level, tag, tid, message);

            try
            {
                Console.WriteLine(logLine);
            }
            catch { }

            lock (_lock)
            {
                try
                {
                    string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                    string fileName = level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                        ? string.Format("error-{0}.log", dateStr)
                        : string.Format("audit-dev-{0}.log", dateStr);
                    string fullPath = Path.Combine(_logDirectory, fileName);
                    File.AppendAllText(fullPath, logLine + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void LogRequestArrival(string entryPoint, string source, string keyParams, string explanation, string traceId)
        {
            string msg = string.Format("入口={0} 来源={1} 关键参数=[{2}] 说明={3}", entryPoint, source, keyParams, explanation);
            WriteLog("请求到达", "INFO", msg, traceId);
        }

        public static void LogMiddlewareCheck(string layer, string checkTarget, bool passed, string reason, string traceId)
        {
            string result = passed ? "通过" : "拦截/拒绝";
            string msg = string.Format("层={0} 检查目标={1} 结果={2} 原因={3}", layer, checkTarget, result, reason);
            WriteLog("中间件/拦截器", passed ? "DEBUG" : "WARN", msg, traceId);
        }

        public static void LogRejected(string layer, string reason, int statusCode, string businessImpact, string suggestion, string traceId)
        {
            string msg = string.Format("拦截层={0} 结果=拒绝 原因={1} 返回状态码={2} 业务影响={3} 建议={4} 说明=请求未进入后续业务逻辑", layer, reason, statusCode, businessImpact, suggestion);
            WriteLog("请求被拦截", "WARN", msg, traceId);
        }

        public static void LogBusinessEntry(string stepName, string keyInputs, string explanation, string traceId)
        {
            string msg = string.Format("step={0} 关键输入=[{1}] 说明={2}", stepName, keyInputs, explanation);
            WriteLog("业务入口", "INFO", msg, traceId);
        }

        public static void LogRuleDecision(string ruleName, string compareValues, string decision, string reason, string traceId)
        {
            string msg = string.Format("规则={0} 比较值=[{1}] 判断结果={2} 原因={3}", ruleName, compareValues, decision, reason);
            WriteLog("分支与规则", "DEBUG", msg, traceId);
        }

        public static void LogCalculationStep(string calcName, int stepIndex, string source, string beforeVal, string formula, string afterVal, string nextDestination, string traceId)
        {
            string msg = string.Format("计算项目={0} 步骤={1} 来源={2} 计算前值={3} 计算方式/公式=[{4}] 计算后值={5} 传递去向={6}", calcName, stepIndex, source, beforeVal, formula, afterVal, nextDestination);
            WriteLog(string.Format("计算过程-步骤{0}", stepIndex), "DEBUG", msg, traceId);
        }

        public static void LogParamValidation(string step, string field, string source, object actualVal, string rule, bool passed, string failReason, string businessImpact, string traceId)
        {
            string result = passed ? "通过" : "失败";
            string valStr = actualVal != null ? actualVal.ToString() : "null";
            string msg = string.Format("step={0} 参数={1} 来源={2} 实际值={3} 规则={4} 结果={5}", step, field, source, valStr, rule, result);
            if (!passed)
            {
                msg += string.Format(" 失败原因={0} businessImpact={1}", failReason, businessImpact);
                WriteLog("参数校验", "WARN", msg, traceId);
            }
            else
            {
                WriteLog("参数校验", "DEBUG", msg, traceId);
            }
        }

        public static void LogStateChange(string step, string targetObject, string beforeState, string afterState, string reason, string traceId)
        {
            string msg = string.Format("step={0} 对象={1} 原状态={2} 新状态={3} 原因={4}", step, targetObject, beforeState, afterState, reason);
            WriteLog("状态流转", "INFO", msg, traceId);
        }

        public static void LogStorageOperation(string intent, string targetObject, string condition, int affectedCount, string actualResult, string traceId)
        {
            string msg = string.Format("意图={0} 业务对象={1} 条件=[{2}] 影响行/记录数={3} 结果={4}", intent, targetObject, condition, affectedCount, actualResult);
            WriteLog("存储层操作", "INFO", msg, traceId);
        }

        public static void LogResponseReturn(string step, int statusCode, string responseSummary, long durationMs, string businessResult, string businessImpact, string traceId)
        {
            string msg = string.Format("step={0} 状态码={1} 响应摘要=[{2}] 耗时={3}ms 业务结果={4} 业务影响={5}", step, statusCode, responseSummary, durationMs, businessResult, businessImpact);
            WriteLog("响应返回", "INFO", msg, traceId);
        }

        public static void LogException(string stage, string exceptionType, string reason, string businessImpact, bool handled, Exception ex, string traceId)
        {
            string exDetail = ex != null ? string.Format(" 异常堆栈={0}", ex.Message) : "";
            string msg = string.Format("发生阶段={0} 异常类型={1} 原因={2} 业务影响={3} 是否已捕获处理={4}{5}", stage, exceptionType, reason, businessImpact, handled ? "是" : "否", exDetail);
            WriteLog("异常与错误", "ERROR", msg, traceId);
        }

        public static void Info(string tag, string message, string traceId)
        {
            WriteLog(tag, "INFO", message, traceId);
        }

        public static void Debug(string tag, string message, string traceId)
        {
            WriteLog(tag, "DEBUG", message, traceId);
        }

        public static void Warn(string tag, string message, string traceId)
        {
            WriteLog(tag, "WARN", message, traceId);
        }
    }
}
