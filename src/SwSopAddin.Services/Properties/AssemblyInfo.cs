using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("SwSopAddin.Services")]
[assembly: AssemblyDescription("SOP 生成器 - 业务服务层(W1.3.a 占位;W2 起放 M2-M7 实现:ExplodeService/DrawingService/...)")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("SwSopAddin")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("D2B3C4D5-E6F7-4890-AB12-3456789ABCDE")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

// 测试项目可见 internal — 暴露 ShouldSkip / GetComponentBaseName 等纯逻辑给单元测试。
// 注意:internal 成员仍只在本程序集 + 测试程序集内可见,不影响外部调用方。
[assembly: InternalsVisibleTo("SwSopAddin.Tests")]
