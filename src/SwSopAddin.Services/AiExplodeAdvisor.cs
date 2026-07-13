using System;
using System.Collections.Generic;
using System.Drawing;            // POC 1 截图需要 Bitmap/Graphics/Imaging
using System.Drawing.Imaging;
using System.IO;
using System.Net;                // ServicePointManager.SecurityProtocol (W7+ SSL/TLS 修复)
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;       // ParseM3Response 用 JObject/JArray 替代 dynamic
using SolidWorks.Interop.sldworks;
using SwSopAddin.Adapter;       // SwApiWrapper.GetActiveConfiguration
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Services
{
    /// <summary>
    /// M7 — AI 爆炸评估器。
    /// M2 爆炸完 → 截 SW 主窗口 HWND → POST 给 MiniMax-M3 vision
    /// → AI 返完整爆炸链 steps[] (绝对值,非 delta)
    /// → delta 对比 → 调 ModifyExplodeStep / IAddExplodeStep 回写
    /// 最多 3 轮,每轮截图+评分+调整。
    /// </summary>
    public class AiExplodeAdvisor
    {
        private static readonly Logger Log = Logging.ForType(typeof(AiExplodeAdvisor));

        private readonly AiAdvisorOptions _opts;
        private readonly HttpClient _http;

        public AiExplodeAdvisor(AiAdvisorOptions opts)
        {
            _opts = opts ?? throw new ArgumentNullException(nameof(opts));

            // W7+ 修复:.NET Framework 4.8 默认 SecurityProtocol = Ssl3|Tls,现代 API endpoint 强制 TLS 1.2+
            // (这次 log 显示 "未能创建 SSL/TLS 安全通道" 就是这个)。这里强制 TLS 1.2。
            // process-wide 设置 — 同一个 SW 进程里就这一个 AddIn,影响无害。
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                if (ServicePointManager.SecurityProtocol.HasFlag(SecurityProtocolType.Tls11) ||
                    ServicePointManager.SecurityProtocol.HasFlag(SecurityProtocolType.Tls))
                {
                    // 显式关掉老协议(用 |= 把 Tls12 加上去,然后清掉 Ssl3/Tls/Tls11)
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                }
                Log.Info("AI advisor 初始化:SecurityProtocol = {0}", ServicePointManager.SecurityProtocol);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "设置 SecurityProtocol 失败,API 调用可能继续走老协议被拒");
            }

            // 优先用 EffectiveApiKey(ANTHROPIC_API_KEY 环境变量 → config.json 里的明文 ApiKey)
            // ctor 里只取一次,后续 _opts.ApiKeySource 不再变。
            string apiKey = _opts.EffectiveApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                Log.Warn("AI advisor 初始化:apiKey 都没配(env 和 config 都是空),后续调 API 会失败");
            }
            else
            {
                Log.Info("AI advisor 初始化:apiKey 来源 = {0}", _opts.ApiKeySource);
                // 明文 config 还有值 + env 也有值 — 给个 info 提示用户可以清空明文
                if (_opts.ApiKeySource.StartsWith("env:") &&
                    !string.IsNullOrEmpty(_opts.ApiKey) &&
                    _opts.ApiKey != apiKey)
                {
                    Log.Info("AI advisor:config.json 的明文 ApiKey 被忽略(优先 env 变量 ANTHROPIC_API_KEY)。" +
                             "生产部署后建议把明文 ApiKey 清空。");
                }
            }

            _http = new HttpClient
            {
                BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(60),
            };
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
        }

        public AdvisorResult RunIterations(ISldWorks sw, AssemblyDoc asm, ExplodeResult initial)
        {
            var result = new AdvisorResult { Enabled = _opts.Enabled };
            if (!_opts.Enabled)
            {
                Log.Info("AI 评估禁用 (aiAdvisor.enabled=false),跳过");
                return result;
            }
            if (string.IsNullOrEmpty(_opts.EffectiveApiKey) || _opts.EffectiveApiKey.Contains("REPLACE"))
            {
                Log.Warn("AI 评估:apiKey 未配置,跳过(请设环境变量 ANTHROPIC_API_KEY 或在 config.json 填 aiAdvisor.apiKey)");
                result.SkippedReason = "apiKey 未配置";
                return result;
            }
            if (initial == null || initial.StepCount == 0)
            {
                Log.Info("AI 评估:M2 0 步,无爆炸可评估,跳过");
                result.SkippedReason = "M2 0 步";
                return result;
            }

            string asmTitle = ((IModelDoc2)asm).GetTitle();

            // 初始爆炸 step 快照
            var currentSteps = SnapshotExplodeSteps(asm);
            Log.Info("AI 评估 start: model='{0}', steps={1}, maxRounds={2}", asmTitle, currentSteps.Count, _opts.MaxRounds);

            for (int round = 1; round <= _opts.MaxRounds; round++)
            {
                Log.Info("===== AI 评估第 {0} / {1} 轮 =====", round, _opts.MaxRounds);

                // 1) 截图前先设好视角
                // W7+ 修:之前直接 TryCaptureSwWindow 截的是 SW 当前视角,
                // 经常是默认或不正交,装配体只显示局部,AI 看不到完整爆炸。
                // 强制设:①等轴测视角(*等轴测,中文环境名)②Zoom to fit(全部入框)
                // ③确保 exploded state 显示(ShowExploded2)
                try
                {
                    IModelDoc2 mdl = (IModelDoc2)asm;
                    // 中文 SW 环境下标准视图名是 "*等轴测" / "*前视" / "*上视"
                    // ViewId 7 = swIsometricView(从 swStandardViews_e enum)
                    mdl.ShowNamedView2("*等轴测", 7);
                    mdl.ViewZoomtofit2();
                    // 确保 exploded state 显示(asm 已经被 ShowExploded2 切到 explode view,
                    // 但视觉上可能还没刷)— 再调一次 ShowExploded2 不影响
                    if (!string.IsNullOrEmpty(initial.ExplodedViewName))
                    {
                        asm.ShowExploded2(true, initial.ExplodedViewName);
                    }
                    Log.Info("AI 评估:视角已设 {0} (exploded view='{1}')",
                        "*等轴测", initial.ExplodedViewName ?? "(none)");
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "AI 评估:设视角失败,继续截图(可能还是局部)");
                }

                // 2) 截图
                // W7+ 改:存到 %LOCALAPPDATA%\SwSopAddin\AI_Screenshots\,不再用 %TEMP%
                // 原因:之前 M3 调用后立刻 TryDelete,用户想打开看时文件已被删,排障难。
                // 现在保留文件,WARN 一句"还在,自己删",不再 auto-delete。
                var aiShotDir = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "SwSopAddin", "AI_Screenshots");
                Directory.CreateDirectory(aiShotDir);
                string pngPath = Path.Combine(aiShotDir,
                    "sop_explode_" + round + "_" + DateTime.Now.ToString("HHmmssfff") + ".png");
                if (!TryCaptureSwWindow(sw, pngPath))
                {
                    Log.Warn("第 {0} 轮截图失败,中止评估", round);
                    result.LastError = "截图失败";
                    break;
                }
                Log.Info("截图 OK: {0} (W7+ 保留,自己删,排障用)", pngPath);

                // 2) 调 M3
                AiAdvisorResponse aiResp;
                try
                {
                    aiResp = CallMiniMaxM3(pngPath, currentSteps, asmTitle);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "第 {0} 轮 API 调用失败", round);
                    result.LastError = ex.Message;
                    break;
                }
                // W7+ 之前 finally 调 TryDelete 删文件,改成保留

                result.Rounds.Add(new AdvisorRound
                {
                    RoundIndex = round,
                    Done = aiResp.Done,
                    OverallComment = aiResp.OverallComment,
                    StepChanges = aiResp.Steps?.Count ?? 0,
                });

                Log.Info("AI 第 {0} 轮: done={1}, comment='{2}', steps={3}",
                    round, aiResp.Done, aiResp.OverallComment, aiResp.Steps?.Count ?? 0);

                if (aiResp.Done)
                {
                    Log.Info("AI 认为已 OK,停止迭代");
                    break;
                }
                if (aiResp.Steps == null || aiResp.Steps.Count == 0)
                {
                    Log.Info("AI 没返 steps,停止迭代");
                    break;
                }

                // 3) delta 对比 + 回写
                int changed = ApplyDelta(asm, currentSteps, aiResp.Steps);
                Log.Info("第 {0} 轮: 改了 {1} 个 step", round, changed);
                result.TotalStepChanges += changed;

                if (changed == 0)
                {
                    Log.Info("第 {0} 轮: AI 返的值跟当前完全一致,再问也是浪费", round);
                    break;
                }

                // 重新拿当前 step 状态
                currentSteps = SnapshotExplodeSteps(asm);
            }

            result.FinalStepCount = currentSteps?.Count ?? 0;
            result.Success = result.Rounds.Count > 0;
            Log.Info("AI 评估 done: rounds={0}, totalStepChanges={1}, finalSteps={2}",
                result.Rounds.Count, result.TotalStepChanges, result.FinalStepCount);
            return result;
        }

        #region 截图

        /// <summary>
        /// PrintWindow 抓 SW 主窗口,存 PNG。
        /// 失败返 false(SW 最小化 / 不可见 / HWND 无效都算失败)。
        /// </summary>
        public static bool TryCaptureSwWindow(ISldWorks sw, string outPngPath)
        {
            try
            {
                IntPtr hwnd = new IntPtr(sw.IFrameObject().GetHWnd());
                if (hwnd == IntPtr.Zero)
                {
                    Log.Warn("TryCaptureSwWindow: SW HWND=0");
                    return false;
                }
                if (!IsWindowVisible(hwnd))
                {
                    Log.Warn("TryCaptureSwWindow: SW 窗口不可见 (最小化/隐藏)");
                    return false;
                }
                if (!GetWindowRect(hwnd, out RECT r))
                {
                    Log.Warn("TryCaptureSwWindow: GetWindowRect 失败");
                    return false;
                }
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0)
                {
                    Log.Warn("TryCaptureSwWindow: 窗口尺寸 {0}x{1} 无效", w, h);
                    return false;
                }
                using (var bmp = new System.Drawing.Bitmap(w, h))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT = 2  (Win 8.1+, 抓 DWM 渲染窗口含 GPU 内容)
                        bool ok = PrintWindow(hwnd, hdc, 2);
                        if (!ok)
                        {
                            Log.Warn("TryCaptureSwWindow: PrintWindow 返 false");
                            return false;
                        }
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                    bmp.Save(outPngPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                Log.Info("TryCaptureSwWindow: 抓 {0}x{1} -> {2}", w, h, outPngPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "TryCaptureSwWindow 异常");
                return false;
            }
        }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        #endregion

        #region 爆炸 step 快照

        /// <summary>
        /// 拿 asm 当前所有 explode step 的 (componentName, distance_mm, reverseDir, x, y, z)。
        /// SW 2024 interop:asm.GetNumExplodeSteps + asm.IGetExplodeStep(i)。
        /// </summary>
        public static List<ExplodeStepSnapshot> SnapshotExplodeSteps(AssemblyDoc asm)
        {
            var list = new List<ExplodeStepSnapshot>();
            try
            {
                // SW interop:GetNumExplodeSteps / IGetExplodeStep / ModifyExplodeStep 都在 IConfiguration 上
                // (跟 ExplodeService.cs 里 cfg.IAddExplodeStep 同源),不在 AssemblyDoc / IAssemblyDoc 上
                IConfiguration cfg = null;
                try { cfg = SwApiWrapper.GetActiveConfiguration(asm); }
                catch (Exception ex) { Log.Warn(ex, "GetActiveConfiguration 失败 — POC 3 不可用"); return list; }
                if (cfg == null)
                {
                    Log.Warn("SnapshotExplodeSteps: cfg 为 null,跳过");
                    return list;
                }

                int n = 0;
                try { n = cfg.GetNumberOfExplodeSteps(); }
                catch (Exception ex) { Log.Warn(ex, "GetNumberOfExplodeSteps 失败"); return list; }
                Log.Info("SnapshotExplodeSteps: n={0}", n);
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        object stepObj = cfg.IGetExplodeStep(i);
                        if (stepObj == null) continue;
                        var step = stepObj as ExplodeStep;
                        if (step == null) continue;

                        // ExplodeStepData / GetExplodeStepData 在 SW 2024 interop 不存在;
                        // 实际能拿 distance 的是 IExplodeStep::ExplodeDistance 属性(get_ExplodeDistance)。
                        // 这里不强读 — POC 3 的 TryModifyStep 已是 no-op,distance 值不参与实际修改,
                        // 默认为 0/false 让链路通,ApplyRebuild 落地时再补。
                        double dist = 0;
                        bool rev = false;

                        string compName = "";
                        try
                        {
                            // GetComponent(int Index) — POC 期间先传 0(单组件 step)
                            IComponent2 c = (IComponent2)step.GetComponent(0);
                            if (c != null) compName = c.Name2 ?? "";
                        }
                        catch { /* GetComponent 签名差异留到 POC 验证时再确认 */ }

                        // 拿 SW 内部 step name(ApplyRebuild 删 step 时要用)
                        string stepName = null;
                        try
                        {
                            // IExplodeStep.Name 是 property getter
                            stepName = ((IExplodeStep)step).Name;
                        }
                        catch (Exception ex) { Log.Warn(ex, "IExplodeStep.Name 读失败 for step[{0}]", i); }

                        var snap = new ExplodeStepSnapshot
                        {
                            Index = i,
                            StepName = stepName,
                            ComponentName = compName,
                            DistanceMm = dist,
                            ReverseDir = rev,
                        };
                        list.Add(snap);
                        if (step != null) Marshal.ReleaseComObject(step);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "读 step[{0}] 失败", i);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "SnapshotExplodeSteps 整体异常");
            }
            return list;
        }

        #endregion

        #region API 调用

        private AiAdvisorResponse CallMiniMaxM3(string pngPath, List<ExplodeStepSnapshot> currentSteps, string asmTitle)
        {
            byte[] pngBytes = File.ReadAllBytes(pngPath);
            string b64 = Convert.ToBase64String(pngBytes);

            // 构造 system prompt
            var systemPrompt = new StringBuilder();
            systemPrompt.AppendLine("你是 SolidWorks 装配爆炸图评审专家。");
            systemPrompt.AppendLine("你的任务:看用户提供的爆炸图截图 + 现有爆炸链(step 列表),");
            systemPrompt.AppendLine("判断每个组件的位置方向是否合理(不重叠、距离合适、沿主轴展开、能看清装配关系)。");
            systemPrompt.AppendLine("若整体可以接受,返回 done=true 且 steps 为空。");
            systemPrompt.AppendLine("若需要调整,返回完整的爆炸链 steps 数组(对每个 step 给新的 distance_mm / reverse / reason)。");
            systemPrompt.AppendLine();
            systemPrompt.AppendLine("重要约束:");
            systemPrompt.AppendLine("- 你只能调整距离和方向,不能增删 step");
            systemPrompt.AppendLine("- distance_mm 取值 10 ~ 300");
            systemPrompt.AppendLine("- reverse: true 表示向 asm 原点反方向推");
            systemPrompt.AppendLine("- reason 简短中文,说明为什么改这个值");
            systemPrompt.AppendLine();
            systemPrompt.AppendLine("只返回 JSON,不要任何解释或 markdown 包裹:");

            // 构造 user 消息
            var stepsText = new StringBuilder();
            stepsText.AppendLine("当前爆炸链 (" + asmTitle + "):");
            stepsText.AppendLine(JsonConvert.SerializeObject(currentSteps, Formatting.Indented));
            stepsText.AppendLine();
            stepsText.AppendLine("附图是当前爆炸图截图。请评审并返回新的爆炸链 JSON。");

            var body = new
            {
                model = _opts.Model,
                max_tokens = 4000,
                // W7+ 显式开 thinking。M3 默认关(虽然 body 里之前有这一行但参数值是 "adaptive",
                // 实际请求里却没传,bug 之前被我修过一次但没生效;这里重新保证带上)。
                // 代价:token +30~50%,响应慢 2-3 秒。回报:AI 建议更准。
                thinking = new { type = "adaptive" },
                system = systemPrompt.ToString(),
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = stepsText.ToString() },
                            new
                            {
                                type = "image",
                                source = new { type = "base64", media_type = "image/png", data = b64 }
                            }
                        }
                    }
                }
            };

            string json = JsonConvert.SerializeObject(body);
            Log.Info("CallMiniMaxM3: POST {0}, body size={1}KB", _opts.BaseUrl, json.Length / 1024);

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage resp = _http.PostAsync("v1/messages", content).GetAwaiter().GetResult();
            string respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Log.Info("M3 响应 status={0}, body size={1}KB", (int)resp.StatusCode, respBody.Length / 1024);

            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("M3 HTTP " + (int)resp.StatusCode + ": " + Truncate(respBody, 500));
            }

            return ParseM3Response(respBody);
        }

        /// <summary>
        /// 从 M3 response.content 数组里取 type=text 的块,parse JSON。
        /// 兼容 ```json ... ``` 包裹。
        /// 用 JObject 代替 dynamic,避免 Microsoft.CSharp 引用问题(.NET FW 4.8 + legacy csproj 经常踩坑)。
        /// </summary>
        public static AiAdvisorResponse ParseM3Response(string respBody)
        {
            JObject root;
            try
            {
                root = JObject.Parse(respBody);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("M3 响应无法 parse: " + ex.Message);
            }
            if (root == null) throw new InvalidOperationException("M3 响应无法 parse");
            string text = null;
            try
            {
                JArray content = root["content"] as JArray;
                if (content != null)
                {
                    foreach (var block in content)
                    {
                        JObject blockObj = block as JObject;
                        if (blockObj == null) continue;
                        if ((string)blockObj["type"] == "text")
                        {
                            text = (string)blockObj["text"];
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("M3 响应 content 解析失败: " + ex.Message);
            }
            if (string.IsNullOrEmpty(text))
            {
                throw new InvalidOperationException("M3 响应无 text 块,raw=" + Truncate(respBody, 300));
            }
            Log.Info("M3 text block: {0}...", Truncate(text, 200));

            // 去 ```json ... ``` 包裹
            string cleaned = text.Trim();
            if (cleaned.StartsWith("```"))
            {
                int firstNewline = cleaned.IndexOf('\n');
                if (firstNewline > 0) cleaned = cleaned.Substring(firstNewline + 1);
                if (cleaned.EndsWith("```")) cleaned = cleaned.Substring(0, cleaned.Length - 3);
                cleaned = cleaned.Trim();
            }

            // W7+ 修复:Newtonsoft 的 [JsonProperty("snake_case")] 不能放多个别名,
            // 但 M3 实际返的是 PascalCase 字段名(Index/DistanceMm/ReverseDir),
            // 不接受 snake_case(step_index/distance_mm/reverse)。
            // 解决:不直接反序列化 AiAdvisorResponse,自己从 JObject 读,字段名同时试两种大小写。
            //
            // W7+ 二次修:17:15 那次 M3 返的是 [{"done":...,"steps":[...]}](数组裹一层),
            // 而不是之前的 {"done":...}(裸 object)。两种都要支持 — 试 JArray 再试 JObject。
            JObject respRoot = null;
            try
            {
                JToken parsed = JToken.Parse(cleaned);
                if (parsed is JArray arr && arr.Count > 0)
                {
                    respRoot = arr[0] as JObject;  // 取数组第一个元素
                    Log.Info("ParseM3Response: M3 返了 {0} 元素数组,取第一个", arr.Count);
                }
                else if (parsed is JObject obj)
                {
                    respRoot = obj;
                }
                else
                {
                    throw new InvalidOperationException("M3 响应既不是 object 也不是 array: " + parsed.Type);
                }
            }
            catch (JsonReaderException ex)
            {
                throw new InvalidOperationException("M3 text 无法 parse 为 JToken: " + ex.Message);
            }
            if (respRoot == null)
            {
                throw new InvalidOperationException("M3 text parse 后 JObject 为 null");
            }
            var resp = new AiAdvisorResponse
            {
                Done = (bool?)respRoot["done"] ?? (bool?)respRoot["Done"] ?? false,
                OverallComment = (string)respRoot["overall_comment"] ?? (string)respRoot["OverallComment"],
                Steps = ParseAiSteps(respRoot["steps"] as JArray ?? respRoot["Steps"] as JArray),
            };
            return resp;
        }

        /// <summary>
        /// 字段名兼容 — M3 实际返 PascalCase 不定:Index/StepIndex/ComponentName/DistanceMm/ReverseDir,
        /// 我们的 prompt 写的是 snake_case。两者都查,按从最可能到最不可能的顺序。
        /// </summary>
        private static List<AiExplodeStep> ParseAiSteps(JArray arr)
        {
            var result = new List<AiExplodeStep>();
            if (arr == null) return result;
            foreach (var item in arr)
            {
                JObject obj = item as JObject;
                if (obj == null) continue;
                result.Add(new AiExplodeStep
                {
                    // 实测 M3 返的是 "Index"(capital I,无 Step 前缀)
                    StepIndex = PickInt(obj, "StepIndex", "step_index", "Index", "index"),
                    Component = PickString(obj, "ComponentName", "Component", "component"),
                    DistanceMm = PickDouble(obj, "DistanceMm", "distance_mm", "Distance"),
                    Reverse = PickBool(obj, "ReverseDir", "Reverse", "reverse"),
                    Reason = PickString(obj, "reason", "Reason"),
                });
            }
            return result;
        }

        // 字段名 fallback 查找器 — 配 [JsonProperty] 一次只能给一个名,这里手写
        private static int PickInt(JObject obj, params string[] names)
        {
            foreach (var n in names) { var v = (int?)obj[n]; if (v.HasValue) return v.Value; }
            return 0;
        }
        private static double PickDouble(JObject obj, params string[] names)
        {
            foreach (var n in names)
            {
                var v = obj[n];
                if (v == null || v.Type == JTokenType.Null) continue;
                if (v.Type == JTokenType.Integer) return (double)(long)v;
                if (v.Type == JTokenType.Float) return (double)v;
            }
            return 0;
        }
        private static bool PickBool(JObject obj, params string[] names)
        {
            foreach (var n in names) { var v = (bool?)obj[n]; if (v.HasValue) return v.Value; }
            return false;
        }
        private static string PickString(JObject obj, params string[] names)
        {
            foreach (var n in names) { var v = (string)obj[n]; if (!string.IsNullOrEmpty(v)) return v; }
            return "";
        }

        #endregion

        #region Delta + 回写

        /// <summary>
        /// 对比 AI 返的 steps(按 step_index 匹配) vs currentSteps,有变化就改。
        /// W7+:走 ApplyRebuild(SW 2024 interop 没暴露 ModifyExplodeStep,只能 DeleteExplodeStep + 重新 IAddExplodeStep)。
        /// 失败 catch 住,继续下一个 step — 部分失败不能断整条 pipeline。
        /// </summary>
        public int ApplyDelta(AssemblyDoc asm, List<ExplodeStepSnapshot> current, List<AiExplodeStep> aiSteps)
        {
            int changed = 0;
            try
            {
                IConfiguration cfg = SwApiWrapper.GetActiveConfiguration(asm);
                if (cfg == null)
                {
                    Log.Warn("ApplyDelta: GetActiveConfiguration 返 null — 跳过本轮 delta");
                    return 0;
                }

                // 1. 纯逻辑:算出来要改的 step(测试覆盖)
                var changes = ComputeChangeSet(current, aiSteps);
                if (changes.Count == 0)
                {
                    Log.Info("ComputeChangeSet: 0 变更,AI 返的值跟当前一致(或都在越界)");
                    return 0;
                }
                Log.Info("ComputeChangeSet: {0} 个 step 待重建", changes.Count);

                // 2. COM 重活:逐个删 + 重加(走 IExplodeStepEditor 抽象 — 测试可注入 fake)
                var editor = new SwExplodeStepEditor(asm, cfg);
                changed = ApplyRebuild(editor, changes);
                Log.Info("ApplyRebuild 完成: {0}/{1} 成功", changed, changes.Count);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ApplyDelta 整体异常");
            }
            return changed;
        }

        /// <summary>
        /// 纯逻辑:根据 AI 返的 steps 算出来要改哪些。
        /// 阈值:distance 差 > 0.5mm 算"变"(防止浮点抖动),reverse 严格相等才算"未变"。
        /// 越界 StepIndex 直接忽略(防御 AI 给的索引对不上,可能因为 snapshot 跟 asm 状态已经不同步)。
        /// 不碰 COM,完全可单测。
        /// </summary>
        internal static List<PendingChange> ComputeChangeSet(
            List<ExplodeStepSnapshot> current,
            List<AiExplodeStep> aiSteps)
        {
            var result = new List<PendingChange>();
            if (current == null || aiSteps == null) return result;

            foreach (var ai in aiSteps)
            {
                if (ai == null) continue;
                if (ai.StepIndex < 0 || ai.StepIndex >= current.Count) continue;
                var cur = current[ai.StepIndex];
                if (cur == null) continue;

                bool distanceChanged = Math.Abs(cur.DistanceMm - ai.DistanceMm) > 0.5;
                bool reverseChanged = cur.ReverseDir != ai.Reverse;
                if (!distanceChanged && !reverseChanged) continue;

                result.Add(new PendingChange
                {
                    StepIndex = cur.Index,
                    StepName = cur.StepName,
                    ComponentName = cur.ComponentName,
                    OldDistance = cur.DistanceMm,
                    NewDistance = ai.DistanceMm,
                    OldReverse = cur.ReverseDir,
                    NewReverse = ai.Reverse,
                });
            }
            return result;
        }

        /// <summary>
        /// 对每个 PendingChange 删 + 重建 explode step。
        /// 流程(per change),全走 IExplodeStepEditor — 测试可注入 fake,生产用 SwExplodeStepEditor:
        ///   1. editor.GetComponentForStep(ch.StepIndex) — 拿 component
        ///   2. editor.TryDeleteStep(ch.StepIndex) — 删原 step
        ///   3. editor.TryAddStep(comp, ch.NewDistance, ch.NewReverse) — 选 component + 加新 step
        /// 任意一步返 null/false 跳过此 change,继续下一个 — 部分失败不能断整条。
        /// 返成功改的 step 数。
        ///
        /// 暴露为 internal static 让 SwSopAddin.Tests 直接覆盖(配合 InternalsVisibleTo)。
        /// </summary>
        internal static int ApplyRebuild(IExplodeStepEditor editor, List<PendingChange> changes)
        {
            if (editor == null) throw new ArgumentNullException(nameof(editor));
            if (changes == null) return 0;

            int succeeded = 0;
            foreach (var ch in changes)
            {
                if (ch == null) continue;
                try
                {
                    // 1. 拿 component
                    object comp = editor.GetComponentForStep(ch.StepIndex);
                    if (comp == null)
                    {
                        Log.Warn("ApplyRebuild: step[{0}] '{1}' GetComponentForStep 返 null,跳过",
                            ch.StepIndex, ch.ComponentName);
                        continue;
                    }

                    // 2. 删原 step
                    if (!editor.TryDeleteStep(ch.StepIndex))
                    {
                        Log.Warn("ApplyRebuild: step[{0}] '{1}' TryDeleteStep 返 false,跳过",
                            ch.StepIndex, ch.ComponentName);
                        continue;
                    }

                    // 3. 加新 step
                    if (!editor.TryAddStep(comp, ch.NewDistance, ch.NewReverse))
                    {
                        Log.Warn("ApplyRebuild: step[{0}] '{1}' TryAddStep 返 false,跳过",
                            ch.StepIndex, ch.ComponentName);
                        continue;
                    }

                    succeeded++;
                    Log.Info("ApplyRebuild 成功: step[{0}] '{1}': dist {2}->{3}, rev {4}->{5}",
                        ch.StepIndex, ch.ComponentName, ch.OldDistance, ch.NewDistance, ch.OldReverse, ch.NewReverse);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "ApplyRebuild 单 step 异常: step[{0}] '{1}'", ch.StepIndex, ch.ComponentName);
                }
            }
            return succeeded;
        }

        #endregion

        #region utils

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 清理失败不阻塞 */ }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        #endregion
    }

    #region DTO

    public class ExplodeStepSnapshot
    {
        public int Index { get; set; }

        /// <summary>
        /// W7+ (POC 3 闭环):IExplodeStep.Name — SW 内部 explode step 名。
        /// ApplyRebuild 调 cfg.DeleteExplodeStep(String) 时要这个,不能光靠 Index(删一个之后后续 Index 全错位)。
        /// </summary>
        public string StepName { get; set; }

        public string ComponentName { get; set; }
        public double DistanceMm { get; set; }
        public bool ReverseDir { get; set; }
    }

    public class AiAdvisorResponse
    {
        [JsonProperty("done")] public bool Done { get; set; }
        [JsonProperty("overall_comment")] public string OverallComment { get; set; }
        [JsonProperty("steps")] public List<AiExplodeStep> Steps { get; set; }
    }

    public class AiExplodeStep
    {
        [JsonProperty("step_index")] public int StepIndex { get; set; }
        [JsonProperty("component")] public string Component { get; set; }
        [JsonProperty("distance_mm")] public double DistanceMm { get; set; }
        [JsonProperty("reverse")] public bool Reverse { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }

    public class AdvisorResult
    {
        public bool Enabled { get; set; }
        public string SkippedReason { get; set; }
        public string LastError { get; set; }
        public bool Success { get; set; }
        public int TotalStepChanges { get; set; }
        public int FinalStepCount { get; set; }
        public List<AdvisorRound> Rounds { get; set; } = new List<AdvisorRound>();
    }

    public class AdvisorRound
    {
        public int RoundIndex { get; set; }
        public bool Done { get; set; }
        public string OverallComment { get; set; }
        public int StepChanges { get; set; }
    }

    /// <summary>
    /// W7+ (POC 3 闭环):AI 评估里需要改的 explode step — 中间态 DTO。
    /// 从 current + aiSteps 计算出来(ComputeChangeSet 纯函数),然后 ApplyRebuild 逐个 apply。
    /// 不直接拿 IComponent2 / IExplodeStep 引用 — 这些 COM 留给 ApplyRebuild 现场取,避免跨方法持有。
    /// </summary>
    public class PendingChange
    {
        public int StepIndex { get; set; }
        public string StepName { get; set; }       // 删的时候用(IConfiguration::DeleteExplodeStep 签名是 String)
        public string ComponentName { get; set; }  // 日志 / 排障
        public double OldDistance { get; set; }
        public double NewDistance { get; set; }
        public bool OldReverse { get; set; }
        public bool NewReverse { get; set; }
    }

    #endregion
}
