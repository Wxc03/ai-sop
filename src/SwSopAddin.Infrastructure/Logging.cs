using System;
using System.IO;
using System.Text;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace SwSopAddin.Infrastructure
{
    /// <summary>
    /// 全插件统一日志入口。Host 在 ConnectToSW 里调一次 Init(),其他地方拿 Logger 就用。
    /// 不用 NLog.config 文件,完全代码内配置 — 避免 SW 进程 cwd 不可预测导致的 NLog 配置找不到。
    /// </summary>
    public static class Logging
    {
        private static bool _initialized;
        private static readonly object _lock = new object();

        public static void Init(LogLevel minLevel = null)
        {
            lock (_lock)
            {
                if (_initialized) return;

                AppPaths.EnsureDirs();

                var config = new LoggingConfiguration();

                var fileTarget = new FileTarget("file")
                {
                    FileName = Path.Combine(AppPaths.LogsDir, "sop-${shortdate}.log"),
                    Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}",
                    Encoding = Encoding.UTF8,
                    KeepFileOpen = false,           // SW 崩了也要让日志落盘
                    ConcurrentWrites = true,        // 多 SW 实例同时跑时不锁
                    ArchiveAboveSize = 10485760,    // 单文件 ≥10 MB 滚动
                    MaxArchiveFiles = 14,           // 保留 14 个归档
                    ArchiveNumbering = ArchiveNumberingMode.Date,
                };

                config.AddTarget(fileTarget);
                config.LoggingRules.Add(new LoggingRule("*", minLevel ?? LogLevel.Info, fileTarget));

                LogManager.Configuration = config;
                _initialized = true;

                ForType(typeof(Logging)).Info("=== Logging initialized,LogsDir={0} ===", AppPaths.LogsDir);
            }
        }

        public static Logger ForType(Type t) => LogManager.GetLogger(t.FullName);
        public static Logger Named(string name) => LogManager.GetLogger(name);
    }
}
