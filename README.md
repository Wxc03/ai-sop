# SwSopAddin — SolidWorks 2024 装配 SOP 自动生成插件

详细技术方案见 `C:\Users\mozhi\Desktop\SW生成SOP\SW2024_SOP插件技术方案_V2.md`。

---

## 当前阶段:**W1 第 1 天 — 项目骨架冒烟测试**

只实现了能让 SW 加载、菜单显示的最小 `ISwAddin`。所有按钮点击只弹"触发成功"提示框,真正业务逻辑从 W2 开始接入。

骨架仅含 1 个项目 `SwSopAddin.Host`(其余 6 个项目 + 测试项目在冒烟通过后再加,避免一次性引入未验证的依赖链)。

---

## 一次性环境准备

1. **VS 2022 Community**(以管理员身份装):
   ```
   winget install Microsoft.VisualStudio.2022.Community --override "--add Microsoft.VisualStudio.Workload.ManagedDesktop --includeRecommended --passive --norestart"
   ```
2. **Git**(可选,W1 末再装也行):
   ```
   winget install Git.Git
   ```

---

## 构建并在 SW 中验证(冒烟测试流程)

1. VS 2022 打开 `SwSopAddin.sln`
2. 顶部工具栏选 `Debug` / `Any CPU` → 菜单 `生成` → `生成解决方案`(或 Ctrl+Shift+B)
3. 编译成功后 `bin\Debug\SwSopAddin.Host.dll` 应该出现
4. **以管理员身份**双击 `src\SwSopAddin.Host\Register.bat`(只需做一次)
5. 启动 SolidWorks 2024
6. **预期结果**:
   - SW 启动时弹出"[SOP 生成器] 已加载"提示框
   - SW 菜单栏 / 工具栏出现 `SOP 生成器` 菜单组
   - 点 `一键生成 SOP` / `分步执行` / `配置...` / `关于...` 每个按钮都应弹对应提示框

任一项不符合预期 → 冒烟未通过,见下方"排障"。

---

## 排障速查

| 现象 | 原因 / 处理 |
|---|---|
| 编译报 "找不到 SolidWorks.Interop.sldworks" | `.csproj` 里的 HintPath 是 `D:\SW\SOLIDWORKS\api\redist\`,确认你 SW 装在这里;否则改 HintPath |
| Register.bat 报"拒绝访问" | 没用管理员身份运行 — 右键 → 以管理员身份运行 |
| Register.bat 报"找不到 RegAsm.exe" | .NET FW 4.8 未装(已检测应该是装好的);路径在 `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\` |
| SW 启动没弹提示框、菜单没出现 | 在 SW 中:`工具` → `插件` → 查 `SOP 生成器` 是否在列表里;不在列表说明注册没成功;在列表但未勾选则勾选 |
| SW 启动弹"无法加载插件" | 检查 `bin\Debug\` 路径里 DLL 是否被移动 — regasm 用的是 /codebase 锁定的绝对路径,DLL 不能挪 |

---

## 卸载

**以管理员身份**双击 `src\SwSopAddin.Host\Unregister.bat`,然后重启 SW。

---

## 目录结构(当前)

```
SwSopAddin/
├── SwSopAddin.sln
├── README.md
├── .gitignore
└── src/
    └── SwSopAddin.Host/
        ├── SwSopAddin.Host.csproj
        ├── SwSopAddinPlugin.cs       ← ISwAddin 实现
        ├── Register.bat              ← regasm 注册脚本
        ├── Unregister.bat
        └── Properties/
            └── AssemblyInfo.cs
```

冒烟通过后会扩展为完整 7 项目 + 2 测试项目结构,见技术方案 §5.2。
