using System;
using NLog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Adapter
{
    /// <summary>
    /// SW API 静态包装层。唯一允许直接碰 COM 对象的层。
    /// 所有方法打 NLog,文档类型校验一次性做完,Service 层不重复。
    /// 调用方负责对返回的 COM RCW 调 Marshal.ReleaseComObject 释放。
    /// </summary>
    public static class SwApiWrapper
    {
        private static readonly Logger Log = Logging.ForType(typeof(SwApiWrapper));

        public enum ActivateResult
        {
            Ok,
            NoDoc,
            WrongType,
        }

        /// <summary>
        /// 取当前激活装配体。失败时 message 是给用户看的中文提示,可直接 SendMsgToUser2。
        /// 成功时返回 Ok + asm;asm 需要调用方自己释放。
        /// </summary>
        public static ActivateResult TryGetActiveAssembly(ISldWorks sw, out AssemblyDoc asm, out string message)
        {
            asm = null;
            message = null;

            if (sw == null)
            {
                message = "SldWorks 句柄为空";
                Log.Error(message);
                return ActivateResult.NoDoc;
            }

            // COM:sw.ActiveDoc 返回 object,必须显式 cast 到 ModelDoc2
            ModelDoc2 doc = (ModelDoc2)sw.ActiveDoc;
            if (doc == null)
            {
                message = "请先打开一个装配体(.SLDASM),再执行本操作。";
                Log.Warn("TryGetActiveAssembly: NoDoc (sw.ActiveDoc is null)");
                return ActivateResult.NoDoc;
            }

            int type = doc.GetType();
            if (type != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                string typeName = type == (int)swDocumentTypes_e.swDocPART ? "零件" :
                                  type == (int)swDocumentTypes_e.swDocDRAWING ? "工程图" : "未知(" + type + ")";
                message = "当前文档是 " + typeName + " (不是装配体)。请打开一个 .SLDASM 再操作。";
                Log.Warn("TryGetActiveAssembly: WrongType={0}", typeName);
                return ActivateResult.WrongType;
            }

            asm = (AssemblyDoc)doc;
            Log.Info("TryGetActiveAssembly: OK, doc='{0}'", doc.GetTitle());
            return ActivateResult.Ok;
        }

        /// <summary>
        /// 取当前激活配置(返回 IConfiguration,W2.2 创建爆炸时需要)。
        /// 注意:GetActiveConfiguration 在 IModelDoc2 接口上,不是 IAssemblyDoc。
        /// AssemblyDoc 通过 dispatch 路由能调,但编译器做静态分析不知道,需要 cast 到 IModelDoc2 才行。
        /// </summary>
        public static IConfiguration GetActiveConfiguration(AssemblyDoc asm)
        {
            if (asm == null) throw new ArgumentNullException(nameof(asm));
            IModelDoc2 modelDoc = (IModelDoc2)asm;
            IConfiguration cfg = (IConfiguration)modelDoc.GetActiveConfiguration();
            Log.Info("GetActiveConfiguration: name='{0}'", cfg.Name);
            return cfg;
        }

        /// <summary>
        /// Part A Phase 1 — 组件世界坐标包围盒(米),喂给 ExplodeLayoutPlanner 的 ComponentGeometry.Box。
        /// 反射已确认 IComponent2.GetBox(bool,bool) 返回 double[6] = [xmin,ymin,zmin,xmax,ymax,zmax]。
        /// 失败(异常或返回形状不对)返回 null,调用方按 IsSkipped/HasValidBox 语义处理,不抛异常中断整批规划。
        /// </summary>
        public static double[] GetComponentWorldBox(IComponent2 comp)
        {
            if (comp == null) throw new ArgumentNullException(nameof(comp));
            try
            {
                object boxObj = comp.GetBox(true, true);
                var box = boxObj as double[];
                if (box == null || box.Length != 6)
                {
                    Log.Warn("GetComponentWorldBox: GetBox 返回异常数据(comp={0})", SafeComponentName(comp));
                    return null;
                }
                return box;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "GetComponentWorldBox 失败(comp={0})", SafeComponentName(comp));
                return null;
            }
        }

        private static string SafeComponentName(IComponent2 comp)
        {
            try { return comp.Name2; } catch { return "?"; }
        }
    }
}
