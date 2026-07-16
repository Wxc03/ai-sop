using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NLog;
using SolidWorks.Interop.sldworks;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>
    /// M5 球标 — W3.2 MVP。
    /// 用 IDrawingDoc.AutoBalloon5 + AutoBalloonOptions 一键打标。
    /// MVP 配置:Lay=1(Square 均匀分布),Style=1(swBS_Circular 圆形),自动磁力线,忽略同名件合并。
    /// W4 起支持配置化(Lay/Style/Size 都从 config.Balloon 读)。
    /// </summary>
    public class BalloonService : IBalloonService
    {
        private static readonly Logger Log = Logging.ForType(typeof(BalloonService));

        // SW 枚举 int 值(直接用,避免绑定 swBalloonLayoutType_e 命名差异)
        // 1=swDetailingBalloonLayout_Square, 2=swDetailingBalloonLayout_Circle
        // 3=Top, 4=Bottom, 5=Right, 6=Left
        private const int LayoutSquare = 1;
        private const int LayoutCircle = 2;  // W6-fix
        // 1=swBS_Circular, 2=swBS_Triangle, 10=swBS_PolylineOut(粗线引出,宏录制实测)
        // 11=swBS_Square
        private const int StyleCircular = 1;
        private const int StylePolylineOut = 10;  // W6-fix:宏录制用户选这个能 work

        public int ApplyAutoBalloon(DrawingDoc drw, View view, int startNumber = 1)
        {
            if (drw == null) throw new ArgumentNullException(nameof(drw));
            if (view == null) throw new ArgumentNullException(nameof(view));

            // TODO(Part B 多 sheet 就绪化):startNumber 目前只占位、不生效。
            // 打通跨 sheet 球标编号连续性需要先找到 SW 侧能设置起始编号的 API。

            // view.Name 在部分 SW 2024 环境返回中文 placeholder(如 "工程图视图1"),
            // 用 GetName2() 拿最可靠的真名(ActivateView/SelectByID2 都要用它)。
            string viewName;
            try { viewName = view.GetName2(); }
            catch { viewName = view.Name; }
            Log.Info("ApplyAutoBalloon: view='{0}'", viewName);

            // W9+ 根因修复:AutoBalloon5 是 IDrawingDoc 级方法(无 view 参数),
            // 它只对"当前激活/选中的视图"打标。W7+ 起(AI 评估切文档 / P7b 加 3 张正交视图)
            // 最后激活的视图不再是爆炸 iso view,导致 AutoBalloon5 打在错误视图上 → 0 个球标。
            // 对比:M6 BOM 用 IView.InsertBomTable4(view 级,显式目标)一直 work。
            // 修法:AutoBalloon5 前用 IDrawingDoc.ActivateView(反射确认存在)激活目标 view,
            //       再 SelectByID2(..., "DRAWINGVIEW", ...) 选中它,把隐式目标钉死到 iso view。
            if (!string.IsNullOrEmpty(viewName))
            {
                try
                {
                    bool activated = drw.ActivateView(viewName);
                    Log.Info("ActivateView('{0}') 返 {1}", viewName, activated);
                }
                catch (Exception ex) { Log.Warn(ex, "ActivateView('{0}') 失败", viewName); }

                try
                {
                    var mde = ((IModelDoc2)drw).Extension;
                    // Type="DRAWINGVIEW",按名选中视图;Append=false 先清空其它选中,
                    // 让 AutoBalloon5 只作用在这一个 view 上。
                    bool selected = mde.SelectByID2(viewName, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);
                    Log.Info("SelectByID2('{0}', DRAWINGVIEW) 返 {1}", viewName, selected);
                }
                catch (Exception ex) { Log.Warn(ex, "SelectByID2('{0}') 失败", viewName); }
            }

            var opts = drw.CreateAutoBalloonOptions();
            opts.Layout = LayoutSquare;        // compact distribution around the exploded view
            opts.Style = StyleCircular;
            opts.IgnoreMultiple = true;       // 同名件只打 1 个标
            opts.InsertMagneticLine = false;  // avoid long leaders extending beyond the sheet border
            opts.Size = 2;                    // W6-fix:Medium size(宏录制)
            opts.LeaderAttachmentToFaces = false;  // W6-fix:宏录制

            // W6-fix:SW 2024 interop 没暴露 view.AutoBalloon(per-view),只能用 drw.AutoBalloon5
            object result = drw.AutoBalloon5(opts);
            int count = 0;
            if (result != null)
            {
                try
                {
                    var arr = (object[])result;
                    count = arr.Length;
                    // 释放每个 COM RCW(不持有强类型,统一 release)
                    foreach (var o in arr)
                    {
                        if (o != null)
                        {
                            ApplyBoldTextFormat(o);
                            ShortenLeader(o);
                            try { Marshal.ReleaseComObject(o); }
                            catch (Exception ex) { Log.Warn(ex, "ReleaseComObject(Balloon) 失败"); }
                        }
                    }
                }
                catch
                {
                    // 单个 BalloonAnnotation,不是数组
                    count = 1;
                    ApplyBoldTextFormat(result);
                    ShortenLeader(result);
                    try { Marshal.ReleaseComObject(result); }
                    catch (Exception ex) { Log.Warn(ex, "ReleaseComObject(Balloon) 失败"); }
                }
            }
            Log.Info("ApplyAutoBalloon done: {0} balloons inserted (view='{1}')", count, viewName);
            return count;
        }

        private static void ApplyBoldTextFormat(object balloon)
        {
            try
            {
                IAnnotation annotation = balloon as IAnnotation;
                if (annotation == null)
                {
                    var note = balloon as Note;
                    if (note != null) annotation = note.GetAnnotation() as IAnnotation;
                }
                if (annotation == null)
                {
                    Log.Warn("AutoBalloon returned '{0}' without IAnnotation; cannot apply text format", balloon.GetType().FullName);
                    return;
                }

                int formats = annotation.GetTextFormatCount();
                for (int i = 0; i < formats; i++)
                {
                    var format = annotation.GetTextFormat(i) as TextFormat;
                    if (format == null) continue;
                    format.Bold = true;
                    format.CharHeightInPts = 16;
                    bool applied = annotation.SetTextFormat(i, false, format);
                    Log.Info("Balloon text format: index={0}, bold=true, height=16pt, applied={1}", i, applied);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Unable to apply bold balloon text format");
            }
        }

        private static void ShortenLeader(object balloon)
        {
            try
            {
                var annotation = GetAnnotation(balloon);
                var note = GetNote(balloon, annotation);
                if (annotation == null) return;

                var label = ReadPoint(annotation.GetPosition(), 0);
                int annotationLeaders = annotation.GetLeaderCount();
                int noteLeaders = note != null ? note.GetLeaderCount() : 0;
                int leaderCount = noteLeaders > 0 ? noteLeaders : annotationLeaders;
                if (label == null || leaderCount < 1)
                {
                    Log.Warn("Balloon leader skipped: type={0}, annotationLeaders={1}, noteLeaders={2}, positionValues={3}",
                        balloon.GetType().FullName, annotationLeaders, noteLeaders, label == null ? 0 : 3);
                    return;
                }

                double[] attachment = null;
                double greatestDistance = 0;
                for (int leaderIndex = 0; leaderIndex < leaderCount; leaderIndex++)
                {
                    object rawPoints = noteLeaders > 0
                        ? note.GetLeaderAtIndex(leaderIndex)
                        : annotation.GetLeaderPointsAtIndex(leaderIndex);
                    var points = ReadPoints(rawPoints);
                    for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                    {
                        double distance = Distance(label, points[pointIndex]);
                        if (distance > greatestDistance)
                        {
                            greatestDistance = distance;
                            attachment = points[pointIndex];
                        }
                    }
                }

                // Keep a readable 18 mm leader, but pull every longer callout back toward
                // its model attachment point.  This is independent of SW's AutoBalloon layout.
                const double targetLength = 0.026;
                if (attachment == null || greatestDistance <= targetLength * 1.5)
                {
                    Log.Warn("Balloon leader skipped: readablePoints={0}, greatestDistance={1:F1}mm",
                        attachment != null, greatestDistance * 1000);
                    return;
                }

                double ratio = targetLength / greatestDistance;
                double x = attachment[0] + (label[0] - attachment[0]) * ratio;
                double y = attachment[1] + (label[1] - attachment[1]) * ratio;
                double z = attachment[2] + (label[2] - attachment[2]) * ratio;
                bool moved = annotation.SetPosition2(x, y, z);
                Log.Info("Balloon leader shortened: {0:F1}mm -> {1:F1}mm, moved={2}",
                    greatestDistance * 1000, targetLength * 1000, moved);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Unable to shorten balloon leader");
            }
        }

        private static IAnnotation GetAnnotation(object balloon)
        {
            var annotation = balloon as IAnnotation;
            if (annotation != null) return annotation;
            var note = balloon as Note;
            return note != null ? note.GetAnnotation() as IAnnotation : null;
        }

        private static Note GetNote(object balloon, IAnnotation annotation)
        {
            var note = balloon as Note;
            if (note != null) return note;
            try { return annotation != null ? annotation.GetSpecificAnnotation() as Note : null; }
            catch (Exception ex)
            {
                Log.Warn(ex, "Unable to resolve AutoBalloon Note");
                return null;
            }
        }

        private static double[] ReadPoint(object raw, int offset)
        {
            var values = ReadValues(raw);
            return values.Count >= offset + 3
                ? new[] { values[offset], values[offset + 1], values[offset + 2] }
                : null;
        }

        private static List<double[]> ReadPoints(object raw)
        {
            var values = ReadValues(raw);
            var points = new List<double[]>();
            for (int i = 0; i + 2 < values.Count; i += 3)
                points.Add(new[] { values[i], values[i + 1], values[i + 2] });
            return points;
        }

        private static List<double> ReadValues(object raw)
        {
            var values = new List<double>();
            var sequence = raw as System.Collections.IEnumerable;
            if (sequence == null) return values;
            foreach (var value in sequence)
            {
                try { values.Add(Convert.ToDouble(value)); }
                catch { return new List<double>(); }
            }
            return values;
        }

        private static double Distance(double[] first, double[] second)
        {
            double x = first[0] - second[0];
            double y = first[1] - second[1];
            double z = first[2] - second[2];
            return Math.Sqrt(x * x + y * y + z * z);
        }
    }
}
