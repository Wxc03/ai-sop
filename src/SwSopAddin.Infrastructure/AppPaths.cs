using System;
using System.IO;

namespace SwSopAddin.Infrastructure
{
    /// <summary>
    /// 集中管理本插件的运行时路径,所有读写都从这里取,避免到处拼字符串。
    /// 全部基于 %AppData%\SwSopAddin\,这样用户级数据与代码安装位置解耦。
    /// </summary>
    public static class AppPaths
    {
        public static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SwSopAddin");

        public static readonly string LogsDir    = Path.Combine(AppDataDir, "logs");
        public static readonly string ConfigJson = Path.Combine(AppDataDir, "config.json");
        public static readonly string LicensePath = Path.Combine(AppDataDir, "license.lic");

        /// <summary>调用方任意时候调一次,保证目录都在。</summary>
        public static void EnsureDirs()
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(LogsDir);
        }
    }
}
