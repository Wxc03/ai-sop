using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// AiAdvisorOptions.EffectiveApiKey 行为契约。
    ///
    /// 关键:EffectiveApiKey 优先级是 env:ANTHROPIC_API_KEY > config.json:ApiKey > 空串。
    /// 这是 W6+ 的安全/卫生项,留给以后的"明文 ApiKey 废弃"路径,先锁住优先级。
    /// </summary>
    [TestClass]
    public class AiAdvisorOptionsTests
    {
        private const string EnvVarName = "ANTHROPIC_API_KEY";
        private string _originalEnvValue;

        [TestInitialize]
        public void Setup()
        {
            // 备份环境变量,测试结束后恢复 — 别污染宿主 shell
            _originalEnvValue = Environment.GetEnvironmentVariable(EnvVarName);
            Environment.SetEnvironmentVariable(EnvVarName, null);
        }

        [TestCleanup]
        public void Teardown()
        {
            Environment.SetEnvironmentVariable(EnvVarName, _originalEnvValue);
        }

        [TestMethod]
        public void EffectiveApiKey_EnvSet_ReturnsEnvValue()
        {
            Environment.SetEnvironmentVariable(EnvVarName, "sk-env-12345");
            var opts = new AiAdvisorOptions { ApiKey = "sk-config-67890" };

            Assert.AreEqual("sk-env-12345", opts.EffectiveApiKey,
                "env 变量存在时必须优先用 env 值,不用 config.json 的明文");
        }

        [TestMethod]
        public void EffectiveApiKey_EnvEmpty_ConfigSet_ReturnsConfigValue()
        {
            // 显式把 env 设为空字符串,确保不会回退到宿主 shell 残留
            Environment.SetEnvironmentVariable(EnvVarName, "");
            var opts = new AiAdvisorOptions { ApiKey = "sk-config-67890" };

            Assert.AreEqual("sk-config-67890", opts.EffectiveApiKey,
                "env 变量空时 fallback 到 config.json 的 ApiKey");
        }

        [TestMethod]
        public void EffectiveApiKey_EnvUnset_ConfigSet_ReturnsConfigValue()
        {
            // 显式 unset(不传值)
            Environment.SetEnvironmentVariable(EnvVarName, null);
            var opts = new AiAdvisorOptions { ApiKey = "sk-config-67890" };

            Assert.AreEqual("sk-config-67890", opts.EffectiveApiKey);
        }

        [TestMethod]
        public void EffectiveApiKey_BothEmpty_ReturnsEmpty()
        {
            Environment.SetEnvironmentVariable(EnvVarName, "");
            var opts = new AiAdvisorOptions { ApiKey = "" };

            Assert.AreEqual("", opts.EffectiveApiKey,
                "两边都没值,EffectiveApiKey 必须返空(下游 RunIterations 走 'apiKey 未配置' 跳过路径)");
        }

        [TestMethod]
        public void EffectiveApiKey_EnvSet_ConfigEmpty_ReturnsEnvValue()
        {
            // 用户已切到 env 模式,config 里明文已清空
            Environment.SetEnvironmentVariable(EnvVarName, "sk-env-only");
            var opts = new AiAdvisorOptions { ApiKey = "" };

            Assert.AreEqual("sk-env-only", opts.EffectiveApiKey);
        }

        // ===== ApiKeySource 标签测试 — 诊断用 =====

        [TestMethod]
        public void ApiKeySource_EnvSet_LabelsEnv()
        {
            Environment.SetEnvironmentVariable(EnvVarName, "sk-env-12345");
            var opts = new AiAdvisorOptions { ApiKey = "sk-config-67890" };

            Assert.AreEqual("env:ANTHROPIC_API_KEY", opts.ApiKeySource,
                "诊断标签必须明确告诉用户 key 来自环境变量,方便排障");
        }

        [TestMethod]
        public void ApiKeySource_EnvEmpty_ConfigSet_LabelsConfig()
        {
            Environment.SetEnvironmentVariable(EnvVarName, "");
            var opts = new AiAdvisorOptions { ApiKey = "sk-config-67890" };

            Assert.AreEqual("config.json:ApiKey", opts.ApiKeySource,
                "标签必须告诉用户 key 来自 config.json(明文),提醒有泄露风险");
        }

        [TestMethod]
        public void ApiKeySource_BothEmpty_LabelsNone()
        {
            Environment.SetEnvironmentVariable(EnvVarName, "");
            var opts = new AiAdvisorOptions { ApiKey = "" };

            Assert.AreEqual("(none)", opts.ApiKeySource);
        }

        // ===== 默认值(无参数 ctor)契约 =====

        [TestMethod]
        public void AiAdvisorOptions_DefaultConstructor_SafeValues()
        {
            var opts = new AiAdvisorOptions();
            // 默认值是文档化的"安全"配置:
            //   Enabled=false(用户必须显式打开,不会偷偷联网)
            //   ApiKey=空(需要用户主动配)
            Assert.IsFalse(opts.Enabled, "默认 Enabled=false,避免插件加载就偷偷打 API");
            Assert.AreEqual("", opts.ApiKey);
            Assert.AreEqual("MiniMax-M3", opts.Model);
            Assert.AreEqual(3, opts.MaxRounds);
            Assert.IsTrue(opts.BaseUrl.StartsWith("https://"), "BaseUrl 必须 https,不是 http");
        }
    }
}
