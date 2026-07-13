using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Infrastructure;
using SwSopAddin.Services;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// ExplodeService 纯逻辑测试。
    ///
    /// 范围限定:只测不碰 COM 的方法(ShouldSkip / GetComponentBaseName 的 null 路径)。
    /// Create() / IAddExplodeStep / AutoExplode / TranslateComponent 这条主路径直接调 SW COM,
    /// 跑测试需要真 SW,留给集成测试或在真机手测。
    ///
    /// 暴露:ExplodeService.ShouldSkip / GetComponentBaseName 已改为 internal,
    /// Services 项目的 AssemblyInfo 加了 [InternalsVisibleTo("SwSopAddin.Tests")]。
    /// </summary>
    [TestClass]
    public class ExplodeServiceTests
    {
        private ExplodeService _svc;

        [TestInitialize]
        public void Setup()
        {
            _svc = new ExplodeService();
        }

        // ===== ShouldSkip 行为契约 =====

        [TestMethod]
        public void ShouldSkip_NullBaseName_ReturnsFalse()
        {
            // 虚拟组件可能没文件名 — 不应该误过滤掉
            var cfg = new ConfigStore();
            Assert.IsFalse(_svc.ShouldSkip(null, null, cfg));
            Assert.IsFalse(_svc.ShouldSkip(null, "", cfg));
        }

        [TestMethod]
        public void ShouldSkip_NullPrefixArray_ReturnsFalse()
        {
            // 配置里没指定过滤前缀 → 不过滤
            var cfg = new ConfigStore();
            cfg.Explode.SkipNamePrefixes = null;
            Assert.IsFalse(_svc.ShouldSkip(null, "FRAME_Part1", cfg));
        }

        [TestMethod]
        public void ShouldSkip_PrefixMatches_ExactCase_ReturnsTrue()
        {
            var cfg = new ConfigStore();
            cfg.Explode.SkipNamePrefixes = new[] { "FRAME_", "BASE_" };  // 显式给前缀(默认是 [])
            Assert.IsTrue(_svc.ShouldSkip(null, "FRAME_Part1", cfg));
            Assert.IsTrue(_svc.ShouldSkip(null, "BASE_Mount", cfg));
        }

        [TestMethod]
        public void ShouldSkip_PrefixMatches_CaseInsensitive_ReturnsTrue()
        {
            // case 不敏感靠 OrdinalIgnoreCase
            var cfg = new ConfigStore();
            cfg.Explode.SkipNamePrefixes = new[] { "FRAME_" };
            Assert.IsTrue(_svc.ShouldSkip(null, "frame_part1", cfg), "小写应被前缀匹配");
            Assert.IsTrue(_svc.ShouldSkip(null, "Frame_Part1", cfg), "混合大小写应被匹配");
        }

        [TestMethod]
        public void ShouldSkip_PrefixDoesNotMatch_ReturnsFalse()
        {
            var cfg = new ConfigStore();
            Assert.IsFalse(_svc.ShouldSkip(null, "WIDGET_123", cfg));
            Assert.IsFalse(_svc.ShouldSkip(null, "Shaft", cfg));
        }

        [TestMethod]
        public void ShouldSkip_EmptyPrefixInArray_Ignored()
        {
            // prefix = "" 会让 baseName.StartsWith("") 永远 true — 必须跳过空 prefix
            // 防有人配 SkipNamePrefixes = new[] { "FRAME_", "" } 误伤所有组件
            var cfg = new ConfigStore();
            cfg.Explode.SkipNamePrefixes = new[] { "", "FRAME_" };
            Assert.IsTrue(_svc.ShouldSkip(null, "FRAME_Part", cfg), "FRAME_ 仍应工作");
            Assert.IsFalse(_svc.ShouldSkip(null, "Shaft", cfg), "空 prefix 不应误过滤");
        }

        [TestMethod]
        public void ShouldSkip_MultiplePrefixes_FirstMatchWins()
        {
            // 用户可能配多个 prefix,任意一个匹配就 skip
            var cfg = new ConfigStore();
            cfg.Explode.SkipNamePrefixes = new[] { "FRAME_", "BASE_", "PIN_" };
            Assert.IsTrue(_svc.ShouldSkip(null, "FRAME_Part1", cfg));
            Assert.IsTrue(_svc.ShouldSkip(null, "BASE_Mount", cfg));
            Assert.IsTrue(_svc.ShouldSkip(null, "PIN_Hinge", cfg));
            Assert.IsFalse(_svc.ShouldSkip(null, "Shaft", cfg));
        }

        // ===== GetComponentBaseName 边界 =====

        [TestMethod]
        public void GetComponentBaseName_NullComponent_ReturnsNull()
        {
            // null Component → IGetModelDoc() 抛 NRE → catch → 返 null
            // 锁住"我不会因为 NRE 炸出测试"
            var result = ExplodeService.GetComponentBaseName(null);
            Assert.IsNull(result);
        }
    }
}
