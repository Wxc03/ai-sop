using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("SwSopAddin.Layout")]
[assembly: AssemblyDescription("SOP 生成器 - 智能布局优化(V2 才用;W1.3.a 占位空项目)")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("SwSopAddin")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("F2B3C4D5-E6F7-4890-AB12-3456789ABCDE")]

// W11:让 SwSopAddin.Tests 测纯几何层(SopLayoutPlanner 等 internal 类)。
// 沿用 Services 项目的 InternalsVisibleTo("SwSopAddin.Tests") 范式。
[assembly: InternalsVisibleTo("SwSopAddin.Tests")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
