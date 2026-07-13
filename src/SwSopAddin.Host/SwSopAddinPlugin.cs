using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NLog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SwSopAddin.Infrastructure;
using SwSopAddin.Orchestration;

namespace SwSopAddin.Host
{
    /// <summary>
    /// W1.3.b 版 — 最小可加载 AddIn + NLog 日志 + ConfigStore 加载。
    /// 真正业务从 W2 起挂在这里。
    /// </summary>
    [Guid(AddInGuid)]
    [ComVisible(true)]
    public class SwSopAddinPlugin : ISwAddin
    {
        // 一旦确定**永远不要改**,改了 SW 找不到插件。
        public const string AddInGuid = "B3F5C7A1-8E2D-4A9B-9C3F-1D8E5A7B6C9D";

        private const string AddInTitle = "SOP 生成器";
        private const string AddInDescription = "SolidWorks 2024 装配 SOP 一体化自动生成";

        private const int CmdGroupId = 1001;
        private const int CmdIdGenerate   = 0;
        private const int CmdIdStepByStep = 1;
        private const int CmdIdConfig     = 2;
        private const int CmdIdAbout      = 3;

        private static readonly Logger Log = Logging.ForType(typeof(SwSopAddinPlugin));

        private ISldWorks _swApp;
        private ICommandManager _cmdMgr;
        private int _addinCookie;
        private ConfigStore _config;

        #region ISwAddin

        public bool ConnectToSW(object thisSW, int cookie)
        {
            // 第一件事 — 日志要在抛任何异常之前能用
            try { Logging.Init(); } catch { /* 日志初始化失败也不阻塞加载 */ }
            Log.Info("===== ConnectToSW called, cookie={0} =====", cookie);

            _swApp = (ISldWorks)thisSW;
            _addinCookie = cookie;
            _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);

            try
            {
                _config = ConfigStore.LoadOrCreate();
                Log.Info("Config loaded: ExplodeDistance={0}mm, PaperSize={1}, OutputDir={2}",
                    _config.Explode.DefaultDistanceMm,
                    _config.Drawing.PaperSize,
                    _config.Pdf.OutputDir);

                _cmdMgr = _swApp.GetCommandManager(_addinCookie);
                AddCommandMgr();
                Log.Info("Command menu registered: group={0}, 4 buttons", CmdGroupId);

                _swApp.SendMsgToUser2(
                    "[" + AddInTitle + "] 已加载 — W3 版本 v0.5",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);

                Log.Info("ConnectToSW done OK");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ConnectToSW failed");
                _swApp.SendMsgToUser2(
                    "[" + AddInTitle + "] 加载失败:\n" + ex.Message + "\n\n详见日志:" + AppPaths.LogsDir,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            Log.Info("===== DisconnectFromSW called =====");
            try
            {
                RemoveCommandMgr();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "RemoveCommandMgr 异常,忽略继续");
            }

            if (_swApp != null)
            {
                Marshal.ReleaseComObject(_swApp);
                _swApp = null;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Log.Info("DisconnectFromSW done");
            LogManager.Shutdown();   // 把缓存的日志刷盘
            return true;
        }

        #endregion

        #region 菜单注册

        private void AddCommandMgr()
        {
            int errors = 0;
            CommandGroup cmdGroup = _cmdMgr.CreateCommandGroup2(
                UserID: CmdGroupId,
                Title: AddInTitle,
                ToolTip: AddInDescription,
                Hint: "",
                Position: -1,
                IgnorePreviousVersion: true,
                Errors: ref errors);

            if (cmdGroup == null)
                throw new Exception("CreateCommandGroup2 返回 null,Errors=" + errors);

            int menuToolbarOption =
                (int)swCommandItemType_e.swMenuItem |
                (int)swCommandItemType_e.swToolbarItem;

            cmdGroup.AddCommandItem2(
                Name: "一键生成 SOP",
                Position: -1,
                HintString: "运行完整 SOP 自动生成流程",
                ToolTip: "一键生成",
                ImageListIndex: 0,
                CallbackFunction: nameof(OnGenerate),
                EnableMethod: "",
                UserID: CmdIdGenerate,
                MenuTBOption: menuToolbarOption);

            cmdGroup.AddCommandItem2(
                Name: "分步执行",
                Position: -1,
                HintString: "按步骤执行 SOP 生成,便于调试",
                ToolTip: "分步执行",
                ImageListIndex: 0,
                CallbackFunction: nameof(OnStepByStep),
                EnableMethod: "",
                UserID: CmdIdStepByStep,
                MenuTBOption: menuToolbarOption);

            cmdGroup.AddCommandItem2(
                Name: "配置...",
                Position: -1,
                HintString: "打开 SOP 生成参数配置窗体",
                ToolTip: "配置",
                ImageListIndex: 0,
                CallbackFunction: nameof(OnConfig),
                EnableMethod: "",
                UserID: CmdIdConfig,
                MenuTBOption: menuToolbarOption);

            cmdGroup.AddCommandItem2(
                Name: "关于...",
                Position: -1,
                HintString: "关于本插件",
                ToolTip: "关于",
                ImageListIndex: 0,
                CallbackFunction: nameof(OnAbout),
                EnableMethod: "",
                UserID: CmdIdAbout,
                MenuTBOption: menuToolbarOption);

            cmdGroup.HasToolbar = true;
            cmdGroup.HasMenu = true;
            cmdGroup.Activate();

            ForceToolbarAlwaysVisible(cmdGroup);
        }

        /// <summary>
        /// 用户反馈:希望"自动生成SOP"4 个按钮不管切到装配体还是工程图界面都常驻左上角
        /// (类似截图那种贴顶排列的经典工具栏)。
        /// CommandGroup 默认只在"当前活动文档类型第一次触发时"由 SW 记住可见性,
        /// 用户如果之前手动关过一次就再也不会自动出现——这里每次 ConnectToSW 都主动
        /// 对 Part/Assembly/Drawing 三种文档类型强制 SetToolbarVisibility(true),
        /// 并 DockingState 设成贴顶(swDockTop),不依赖用户手动 "查看 > 工具栏" 勾选。
        /// 真机反射确认(SW2024 interop):ICommandGroup.SetToolbarVisibility(bool, int docType)
        /// 的 docType 就是 swDocumentTypes_e 数值。
        /// </summary>
        private static void ForceToolbarAlwaysVisible(CommandGroup cmdGroup)
        {
            try
            {
                cmdGroup.DockingState = (int)swToolbarDockStatePosition_e.swDockTop;

                var docTypes = new[]
                {
                    swDocumentTypes_e.swDocPART,
                    swDocumentTypes_e.swDocASSEMBLY,
                    swDocumentTypes_e.swDocDRAWING,
                };
                foreach (var docType in docTypes)
                {
                    cmdGroup.SetToolbarVisibility(true, (int)docType);
                }
                Log.Info("ForceToolbarAlwaysVisible: DockingState=swDockTop, 强制 Part/Assembly/Drawing 三种文档类型工具栏可见");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ForceToolbarAlwaysVisible 失败(不影响菜单/命令本身可用)");
            }
        }

        private void RemoveCommandMgr()
        {
            if (_cmdMgr != null && _swApp != null)
            {
                _cmdMgr.RemoveCommandGroup(CmdGroupId);
                Marshal.ReleaseComObject(_cmdMgr);
                _cmdMgr = null;
            }
        }

        #endregion

        #region 按钮回调

        public void OnGenerate()
        {
            Log.Info("OnGenerate clicked");
            try
            {
                var workflow = new SopWorkflow();
                var result = workflow.RunMvp(_swApp, _config);

                int icon = result.Success
                    ? (int)swMessageBoxIcon_e.swMbInformation
                    : (int)swMessageBoxIcon_e.swMbStop;

                _swApp.SendMsgToUser2(result.SummaryForUser(), icon, (int)swMessageBoxBtn_e.swMbOk);

                Log.Info("OnGenerate done: success={0}, explodeSteps={1}, balloons={2}, bom={3}, drawing='{4}'",
                    result.Success, result.ExplodeStepCount, result.BalloonCount, result.BomInserted, result.DrawingSavedPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OnGenerate 异常");
                _swApp.SendMsgToUser2(
                    "[一键生成 SOP] 异常:\n" + ex.Message + "\n\n" +
                    "日志: " + AppPaths.LogsDir,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        public void OnStepByStep()
        {
            Log.Info("OnStepByStep clicked");
            try
            {
                // 拿 SW 主窗口作为 owner,避免窗被 SW 遮挡
                IntPtr hwnd = new IntPtr(_swApp.IFrameObject().GetHWnd());
                var owner = new System.Windows.Forms.NativeWindow();
                owner.AssignHandle(hwnd);

                int chosenStep = 0;
                try
                {
                    using (var dlg = new SwSopAddin.UI.StepChoiceForm())
                    {
                        dlg.ShowDialog(owner);
                        if (dlg.DialogResult != System.Windows.Forms.DialogResult.OK) return;
                        chosenStep = dlg.SelectedStep;
                    }
                }
                finally
                {
                    owner.ReleaseHandle();
                }
                if (chosenStep < 1) return;

                Log.Info("OnStepByStep: 选 step={0},开始单步执行", chosenStep);
                var workflow = new SopWorkflow();
                var singleResult = workflow.RunStep(_swApp, _config, chosenStep);

                int icon = singleResult.Success
                    ? (int)swMessageBoxIcon_e.swMbInformation
                    : (int)swMessageBoxIcon_e.swMbStop;
                _swApp.SendMsgToUser2(
                    "[分步执行] Step " + chosenStep + " 完成\n\n" + singleResult.SummaryForUser(),
                    icon,
                    (int)swMessageBoxBtn_e.swMbOk);

                Log.Info("OnStepByStep done: step={0}, success={1}", chosenStep, singleResult.Success);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OnStepByStep 异常");
                _swApp.SendMsgToUser2(
                    "[分步执行] 异常:\n" + ex.Message + "\n\n" +
                    "日志: " + AppPaths.LogsDir,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        public void OnConfig()
        {
            Log.Info("OnConfig clicked");
            try
            {
                // 取 SW 主窗口句柄作为 dialog 的 owner,避免弹窗被 SW 主窗口遮挡
                IntPtr hwnd = new IntPtr(_swApp.IFrameObject().GetHWnd());
                var owner = new System.Windows.Forms.NativeWindow();
                owner.AssignHandle(hwnd);
                try
                {
                    using (var dlg = new SwSopAddin.UI.ConfigForm(_config))
                    {
                        dlg.ShowDialog(owner);
                    }
                }
                finally
                {
                    owner.ReleaseHandle();
                }
                Log.Info("ConfigForm closed");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OnConfig failed");
                _swApp.SendMsgToUser2(
                    "配置窗体打开失败:\n" + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        public void OnAbout()
        {
            Log.Info("OnAbout clicked");
            string version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version.ToString();
            _swApp.SendMsgToUser2(
                "SolidWorks 2024 装配 SOP 自动生成插件\n\n" +
                "版本: " + version + " (W3 — M4 视图 + M5 球标 + M6 BOM + 自动存盘)\n" +
                "GUID: " + AddInGuid + "\n" +
                "数据目录: " + AppPaths.AppDataDir + "\n\n" +
                "详细方案见 SW2024_SOP插件技术方案_V2.md",
                (int)swMessageBoxIcon_e.swMbInformation,
                (int)swMessageBoxBtn_e.swMbOk);
        }

        #endregion

        #region COM 注册回调

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string keyName = "{" + t.GUID.ToString().ToUpper() + "}";

            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\SolidWorks\AddIns", writable: true))
            {
                if (root == null)
                    throw new InvalidOperationException(
                        "未找到 HKLM\\SOFTWARE\\SolidWorks\\AddIns — 请确认 SolidWorks 已安装。");

                using (RegistryKey k = root.CreateSubKey(keyName))
                {
                    k.SetValue("", 1, RegistryValueKind.DWord);
                    k.SetValue("Description", AddInDescription);
                    k.SetValue("Title", AddInTitle);
                }
            }

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\SolidWorks\AddInsStartup\" + keyName))
            {
                k.SetValue("", 1, RegistryValueKind.DWord);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            string keyName = "{" + t.GUID.ToString().ToUpper() + "}";
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\SolidWorks\AddIns", writable: true))
                {
                    root?.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                }
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\SolidWorks\AddInsStartup", writable: true))
                {
                    root?.DeleteSubKey(keyName, throwOnMissingSubKey: false);
                }
            }
            catch
            {
                // 卸载流程不应被任何异常阻断
            }
        }

        #endregion
    }
}
