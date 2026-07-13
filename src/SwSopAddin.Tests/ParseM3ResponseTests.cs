using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Services;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// AiExplodeAdvisor.ParseM3Response 字段名兼容测试。
    ///
    /// 背景:W7+ 修复之前,M3 实际返 PascalCase(DistanceMm/ReverseDir/Index),但 DTO 用 [JsonProperty("snake_case")],
    /// 导致 StepIndex/DistanceMm 解析成 0,ComputeChangeSet 看到 0 vs 0 没变化,AI 评估等于没跑。
    /// 修后 ParseM3Response 自己从 JObject 读,字段名同时试 PascalCase 和 snake_case。
    ///
    /// 这些测试锁住字段名兼容契约 — M3 改 schema 时第一时间发现。
    /// </summary>
    [TestClass]
    public class ParseM3ResponseTests
    {
        // ===== 字段名兼容:PascalCase(实测 M3 真返) =====

        [TestMethod]
        public void ParseM3Response_PascalCase_AllFieldsParsed()
        {
            // 实际 M3 返的格式(从 10:57:40 那次 log 抓)
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""{\""done\"": false, \""steps\"": [{\""Index\"": 0, \""StepName\"": \""链1\"", \""ComponentName\"": \""BAR GLOBES-1\"", \""DistanceMm\"": 150, \""ReverseDir\"": true, \""reason\"": \""test reason\"", \""StepType\"": 0}]}"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.IsNotNull(result);
            Assert.IsFalse(result.Done);
            Assert.AreEqual(1, result.Steps.Count);

            var step = result.Steps[0];
            Assert.AreEqual(0, step.StepIndex, "PascalCase 'Index' 必须解析到 StepIndex");
            Assert.AreEqual(150.0, step.DistanceMm, 0.001, "PascalCase 'DistanceMm' 必须解析到 DistanceMm");
            Assert.IsTrue(step.Reverse, "PascalCase 'ReverseDir' 必须解析到 Reverse");
            Assert.AreEqual("test reason", step.Reason);
        }

        [TestMethod]
        public void ParseM3Response_SnakeCase_AllFieldsParsed()
        {
            // system prompt 写的是 snake_case,如果 M3 听 prompt 就返这个
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""{\""done\"": true, \""steps\"": [{\""step_index\"": 2, \""component\"": \""X\"", \""distance_mm\"": 80, \""reverse\"": false, \""reason\"": \""ok\""}]}"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.IsTrue(result.Done);
            Assert.AreEqual(1, result.Steps.Count);
            var step = result.Steps[0];
            Assert.AreEqual(2, step.StepIndex);
            Assert.AreEqual(80.0, step.DistanceMm, 0.001);
            Assert.IsFalse(step.Reverse);
        }

        [TestMethod]
        public void ParseM3Response_MixedCase_FallsBackToAlternative()
        {
            // M3 可能某些字段返 PascalCase,某些返 snake_case
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""{\""done\"": false, \""steps\"": [{\""Index\"": 1, \""distance_mm\"": 100, \""ReverseDir\"": true, \""reason\"": \""mixed\""}]}"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.AreEqual(1, result.Steps.Count);
            var step = result.Steps[0];
            Assert.AreEqual(1, step.StepIndex, "Index(PC) 兜底");
            Assert.AreEqual(100.0, step.DistanceMm, 0.001, "distance_mm(snake) 兜底");
            Assert.IsTrue(step.Reverse, "ReverseDir(PC) 兜底");
        }

        [TestMethod]
        public void ParseM3Response_AllFieldsMissing_DefaultsAreZero()
        {
            // 防御:极端 case,所有字段都没给
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""{\""done\"": false, \""steps\"": [{}]}"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.AreEqual(1, result.Steps.Count);
            var step = result.Steps[0];
            Assert.AreEqual(0, step.StepIndex, "全缺时 StepIndex 必须默认 0,不能 NRE");
            Assert.AreEqual(0.0, step.DistanceMm);
            Assert.IsFalse(step.Reverse);
            Assert.AreEqual("", step.Reason);
            Assert.AreEqual("", step.Component);
        }

        [TestMethod]
        public void ParseM3Response_MultipleSteps_AllParsed()
        {
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""{\""done\"": false, \""steps\"": [{\""Index\"": 0, \""DistanceMm\"": 100, \""ReverseDir\"": false}, {\""Index\"": 1, \""DistanceMm\"": 200, \""ReverseDir\"": true}]}"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.AreEqual(2, result.Steps.Count);
            Assert.AreEqual(0, result.Steps[0].StepIndex);
            Assert.AreEqual(100.0, result.Steps[0].DistanceMm, 0.001);
            Assert.AreEqual(1, result.Steps[1].StepIndex);
            Assert.AreEqual(200.0, result.Steps[1].DistanceMm, 0.001);
            Assert.IsTrue(result.Steps[1].Reverse);
        }

        [TestMethod]
        public void ParseM3Response_DistanceMmType_CoercedFromInt()
        {
            // M3 返整数 100(没小数点),Newtonsoft 强转 (double?)100 必须 OK
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""{\""done\"": false, \""steps\"": [{\""Index\"": 0, \""DistanceMm\"": 100, \""ReverseDir\"": false}]}"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.AreEqual(100.0, result.Steps[0].DistanceMm, 0.001,
                "整数 100 必须能 coerce 到 double 100.0(M3 偶尔忘小数点)");
        }

        [TestMethod]
        public void ParseM3Response_EmptySteps_EmptyList()
        {
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""{\""done\"": true, \""steps\"": []}"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.IsTrue(result.Done);
            Assert.IsNotNull(result.Steps);
            Assert.AreEqual(0, result.Steps.Count, "空 steps 数组必须解析为空列表,不能 null");
        }

        [TestMethod]
        public void ParseM3Response_OverallComment_BothCasingsWork()
        {
            // done/overall_comment 也兼容两种大小写
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""{\""Done\"": false, \""OverallComment\"": \""PC comment\"", \""steps\"": []}"" },
                    { ""type"": ""text"", ""text"": ""{anything ignored since first match wins}"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.AreEqual("PC comment", result.OverallComment);
        }

        [TestMethod]
        public void ParseM3Response_NoTextBlock_Throws()
        {
            string respBody = @"{
                ""content"": [
                    { ""type"": ""image"" }
                ]
            }";

            // 没有 text 块应该 throw(用户要看到错误而不是默默返空)
            try
            {
                AiExplodeAdvisor.ParseM3Response(respBody);
                Assert.Fail("没有 text 块应该抛 InvalidOperationException");
            }
            catch (System.InvalidOperationException)
            {
                // 预期
            }
        }

        [TestMethod]
        public void ParseM3Response_JsonInCodeBlock_Stripped()
        {
            // AI 偶尔用 ```json ... ``` 包裹,要剥掉
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""```json\n{\""done\"": false, \""steps\"": [{\""Index\"": 0, \""DistanceMm\"": 50, \""ReverseDir\"": false}]}\n```"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.AreEqual(1, result.Steps.Count);
            Assert.AreEqual(50.0, result.Steps[0].DistanceMm, 0.001);
        }

        [TestMethod]
        public void ParseM3Response_TextIsArrayOfOneObject_ParsesFirstElement()
        {
            // W7+ 17:15 跑出的实际格式:
            //   M3 这次把响应包了一层 [ {done,steps} ](数组裹一个 object)
            //   之前代码假设是裸 object,弹窗就报"AI 分析错误"
            //   这次 fix 兼容两种 — array 拿第一个元素
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""[{\""done\"": false, \""steps\"": [{\""Index\"": 0, \""DistanceMm\"": 120, \""ReverseDir\"": true, \""ComponentName\"": \""BAR GLOBES-1\"", \""reason\"": \""sample\""}]}]"" }
                ]
            }";

            var result = AiExplodeAdvisor.ParseM3Response(respBody);

            Assert.IsFalse(result.Done);
            Assert.AreEqual(1, result.Steps.Count);
            Assert.AreEqual(0, result.Steps[0].StepIndex);
            Assert.AreEqual(120.0, result.Steps[0].DistanceMm, 0.001);
            Assert.IsTrue(result.Steps[0].Reverse);
            Assert.AreEqual("BAR GLOBES-1", result.Steps[0].Component);
            Assert.AreEqual("sample", result.Steps[0].Reason);
        }

        [TestMethod]
        public void ParseM3Response_TextIsEmptyArray_Throws()
        {
            // 数组是 [] — 拿不到第一元素,必须 throw(不能让 null 流下去 NRE)
            string respBody = @"{
                ""content"": [
                    { ""type"": ""text"", ""text"": ""[]"" }
                ]
            }";

            try
            {
                AiExplodeAdvisor.ParseM3Response(respBody);
                Assert.Fail("空数组应该抛 InvalidOperationException,不能返 null");
            }
            catch (InvalidOperationException)
            {
                // 预期
            }
        }
    }
}
