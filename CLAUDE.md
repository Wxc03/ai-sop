# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project: SwSopAddin

SolidWorks 2024 AddIn (C# .NET Framework 4.8) that auto-generates SOP (Standard Operating Procedure) assembly drawings. End-to-end 7-step pipeline: M2 explode → M1.5 AI explode review (MiniMax-M3 vision) → M2.5 new drawing → M4 iso view → M5 balloons → M6 BOM → W5 layout → PDF export.

Full technical design lives at `C:\Users\mozhi\Desktop\SW生成SOP\SW2024_SOP插件技术方案_V2.md` (outside repo). Cross-session *why* rationale lives in `C:\Users\mozhi\.claude\projects\D--Source-SwSopAddin\memory\` — **read those before re-deriving any "why" question** about existing code.

> **Note**: `README.md` is stale (W1 scaffold, 1 project). The solution is now 7 projects + Tests (8 total), full 7-step pipeline. Trust this file and code over `README.md` for current state.

## Build & Run

Build with **MSBuild only — NOT `dotnet build`**. Legacy csproj + PackageReference combination is known to fail under `dotnet build` (NLog/Newtonsoft "not found" errors despite being in `~/.nuget/packages/`). VS's MSBuild works fine.

```bash
# via VS: Ctrl+Shift+B
# via CLI: locate MSBuild via vswhere (see .claude/hooks/auto-build.sh for the path-lookup chain)
```

### Auto-build hooks (already wired)

`.claude/hooks/auto-build.sh` runs MSBuild on every `Edit|Write` of `.cs/.csproj/.sln/.resx/.config` files. `.claude/hooks/final-check.sh` re-runs MSBuild before the session ends. Both auto-locate MSBuild via `vswhere`. You don't need to invoke them manually — they fire on hook events. If you want to force a clean build outside of hook events, run the same MSBuild command the hooks use.

### Install / uninstall the AddIn in SolidWorks

`src/SwSopAddin.Host/Register.bat` (must be **Administrator**) uses `RegAsm.exe /codebase` to lock the absolute path of `bin\Debug\SwSopAddin.Host.dll` — don't move the DLL after registration. `Unregister.bat` is the reverse (also Admin). The host's `[ComRegisterFunction]` / `[ComUnregisterFunction]` in `SwSopAddinPlugin.cs` is a separate code path used when VS itself is run as Admin and does regasm automatically — it is **not** what `Register.bat` invokes.

### Dev constraints (non-obvious)

- **C# `LangVersion=9.0`** across all 8 csproj — no C# 10+ features (no records, no `init` setters, no top-level statements, no file-scoped namespaces). Match existing style.
- **Legacy csproj + direct `PackageReference`** in every project — the transitive-PackageReference auto-flow doesn't work, so each `*.csproj` using NLog/Newtonsoft must list them explicitly.
- **SW Interop `HintPath` is hardcoded** to `D:\SW\SOLIDWORKS\api\redist\` in 6 csproj (`Host`, `Adapter`, `Orchestration`, `Services`, `Layout`, `Tests`). On another machine, update all six.
- **`InternalsVisibleTo("SwSopAddin.Tests")`** in `SwSopAddin.Services/Properties/AssemblyInfo.cs` — pure helpers that should be unit-testable stay `internal` and use this pattern.

## Tests

`src/SwSopAddin.Tests/` — MSTest 3.6.4, hand-rolled mocks in `Mocks/MockServices.cs` (no Moq dependency). Coverage today: `SopWorkflow` early-exit + `SopResult` defaults; `ExplodeService.ShouldSkip` / `GetComponentBaseName`; `AiAdvisorOptions.EffectiveApiKey` / `ApiKeySource`; `AiExplodeAdvisor.ComputeChangeSet` / `ApplyRebuild` (uses a `RecordingEditor : IExplodeStepEditor` fake); `RunStep` dispatch + out-of-range; `ParseM3Response` JSON tolerance. **Full 7-step COM pipeline tests now exist** (`SopWorkflowTests.cs`) — the W9 `IDocumentValidator` refactor (see below) made Step 0 mockable, so `MockDocumentValidator` drives WrongType / null-asm / validator-throws / full-7-step cases. ~77 test methods, all passing.

Run with **VS Test Explorer** (preferred — handles MSTest discovery), or `vstest.console.exe src\SwSopAddin.Tests\bin\Debug\SwSopAddin.Tests.dll` for CLI (note: `vstest.console.exe` is not on PATH; use a VS Developer Command Prompt, or pass the full path under `Common7\IDE\Extensions\TestPlatform\`).

## Architecture

8-project solution with strict layer model. Dependency direction: `Host → Orchestration → Services/Layout/UI → Adapter → SW COM`. Infrastructure has zero SW dependencies and is referenced by everyone.

| Project | Role | Knows about |
|---|---|---|
| `SwSopAddin.Host` | `ISwAddin` impl, menu registration, command callbacks. GUID is hard-coded `B3F5C7A1-8E2D-4A9B-9C3F-1D8E5A7B6C9D` — **never change it** (SW won't find the AddIn). | All (composition root) |
| `SwSopAddin.Orchestration` | `SopWorkflow.RunMvp` — the 7-step pipeline. Owns `RollbackManager` (RAII undo stack, `Track`/`Commit`/`Dispose` → automatic rollback). Knows nothing about SW interop directly. | Services, Layout, Adapter |
| `SwSopAddin.Services` | One file per "M" step: `ExplodeService` (M2), `ViewService` (M4), `BalloonService` (M5), `BomService` (M6), `DrawingService` (M2.5). Plus `AiExplodeAdvisor` (M7) — calls M3 vision API in a 3-round loop, modifies explode steps. Each service has an `I*Service` interface for testability. | Adapter, SW interop |
| `SwSopAddin.Layout` | W5 智能布局: bounding box collection → collision detection → avoidance → out-of-bounds scaling. Pipeline: `BoundingBoxCollector` → `CollisionDetector` → `AvoidanceResolver` → `OutOfBoundsScaler` → `LayoutApplier` (writes back). | Services types (View, BomTableAnnotation) |
| `SwSopAddin.Adapter` | `SwApiWrapper` — the **only** layer allowed to touch COM RCWs. All other layers go through this. Includes `TryGetActiveAssembly`, `GetActiveConfiguration`. Also `IDocumentValidator` + `SwDocumentValidator` (W9) — abstracts Step 0 document validation so `SopWorkflow` can be unit-tested with a mock. | SW interop |
| `SwSopAddin.Infrastructure` | `ConfigStore` (JSON at `%AppData%\SwSopAddin\config.json`, Newtonsoft), `AppPaths` (log/config dirs), `Logging` (NLog wrapper, code-configured, no .config file). | nothing |
| `SwSopAddin.UI` | `ConfigForm` (settings editor) + `StepChoiceForm` (7-button per-step debug picker). | Infrastructure |
| `SwSopAddin.Tests` | MSTest 3.6.4 tests, hand-rolled mocks, references Interop as `Private=true` for vstest discovery. | Orchestration, Services, Layout, Infrastructure |

### The 7-step pipeline (`SopWorkflow.RunMvp`)

`SopWorkflow` exposes two entry points sharing private `RunStepN_*` helpers: `RunMvp(sw, config)` (full pipeline, wrapped in `RollbackManager`) and `RunStep(sw, config, stepNumber)` (single step 1-7, no rollback, used by `OnStepByStep` → `StepChoiceForm`).

1. **Step 0** Document validation: `SwApiWrapper.TryGetActiveAssembly` — early-exit with Chinese user message if not an asm.
2. **Step 1** M2 explode: `IAddExplodeStep` per component. **3-fallback path** baked in — `IAddExplodeStep` returns null on multi-component assemblies (BenchVice/PressTool pattern instances), so `ExplodeService` falls back to (a) `asm.AutoExplode()` SW heuristic then (b) `asm.TranslateComponent()` to manufacture real steps. **Light-weight resolution** (`ResolveAllLightWeightComponents(false)`) is called first because light-weight components break `IAddExplodeStep`.
3. **Step 1.5** (M7) AI evaluation: `AiExplodeAdvisor.RunIterations`. Only runs if `AiAdvisor.Enabled=true`. Each round: SW HWND → base64 PNG → POST to `https://api.minimaxi.com/anthropic/v1/messages` with model `MiniMax-M3` → AI returns `{done, overall_comment, steps[]}` → delta via `ComputeChangeSet` (pure) → `ApplyRebuild` does `cfg.DeleteExplodeStep` + `IAddExplodeStep` per changed step. Max 3 rounds. POC 1 (screenshot) compiles but runtime not yet verified by user; POC 3 (rebuild) compiles + unit-tested for `ComputeChangeSet`; runtime COM path not yet verified on real asm.
4. **Step 2** M2.5: new drawing from `D:\Templates\SOP_A3.drwdot`.
5. **Step 3** M4: insert iso view. **~10-path fallback** in `ViewService.InsertExplodedIso` (P1-P8 plus P6.5 and P7b) because P1-P5 (English view names `*Isometric` etc.) return null in some SW 2024 environments. **The known-working path is P6 with Chinese view name `*等轴测`.** P7/P7b adds ortho views via `Create1stAngleViews2`. **Always pre-`ShowExploded2` before view creation and `SetDisplayMode(2)` (SHADED) on the iso view afterward.** Note: `RunStep` only accepts `stepNumber` 1-7; the `1.5` AI step is reachable only via `RunMvp`.
6. **Step 4** M5: `AutoBalloon5` on the iso view. **Requires `asm.ShowExploded2(true, explodeViewName)` first** or it finds 0 components. `Style=10` (PolylineOut); `Layout=2` (Circle). **`AutoBalloon5` is a `DrawingDoc`-level call with no view param — it only balloons the *activated/selected* view, so `BalloonService` MUST `drw.ActivateView(viewName)` + `Extension.SelectByID2(viewName, "DRAWINGVIEW", ...)` first (W10 fix). Without that, W7+'s AI-eval/P7b left a different view active → 0 balloons.** Contrast M6 BOM, which uses view-level `IView.InsertBomTable4` and never regressed.
7. **Step 5** M6: `InsertBomTable4` on the iso view with template `D:\SW\SOLIDWORKS\lang\chinese-simplified\bom-standard.sldbomtbt`, anchor (0.234, 0.268) top-left, `UseAnchorPoint=false`. **Requires `ShowExploded2` first** like M5.
8. **Step 6** W5 layout: `LayoutService.ApplyLayout` — F14 collision avoidance + F15 out-of-bounds scaling. F16 pagination is not implemented (pending task).
9. **Step 7** PDF export: `drw.Extension.SaveAs` with `ExportPdfData` and `swPDFExportEmbedFonts=1`. Also saves `.SLDDRW` backup.

All steps are wrapped in `RollbackManager` which closes the drawing on failure. BOM/view/Balloon COM objects are tracked for `Marshal.ReleaseComObject` rollback.

## Key gotchas (non-obvious, don't re-derive)

- **Chinese SW environment**: view names are `*等轴测` / `*前视`, sheet names are `图纸1`. `IsRealModelViewName` must check both `Sheet` and `图纸` prefixes.
- **`CreateDrawViewFromModelView2/3` returns null without `ShowExploded2` first** on the asm.
- **P7b (`Create1stAngleViews2`) resets the asm display state** — must re-`ShowExploded2` after P7b or M5 finds 0 balloons.
- **SW 2024 interop gaps** (confirmed via reflection): no `IConfiguration::ModifyExplodeStep`, no `View.Scale` setter, no `AssemblyDoc::AddExplodeView`, no `DrawingDoc::GetSheetWidth/Height`, no `BalloonAnnotation` class. Workarounds live in the `w6-fix` memory entry.
- **`dotnet build` ≠ `MSBuild`** for this project. The NLog/Newtonsoft PackageReference setup only works under MSBuild.
- **The AddIn GUID is permanent.** Changing it makes SW silently fail to load the AddIn.
- **`AiAdvisorOptions.EffectiveApiKey`** prefers `ANTHROPIC_API_KEY` env var, falls back to plaintext `ApiKey` in `config.json`. Source is logged at advisor construction (`apiKey 来源 = env:ANTHROPIC_API_KEY` / `config.json:ApiKey`). Production should `setx ANTHROPIC_API_KEY sk-cp-...` and clear `config.json`'s `ApiKey`.
- **`AiAdvisor.ApiKey` default is empty; `Enabled` defaults to `false`.** If you flip `Enabled=true` without filling `ApiKey`, the advisor logs "apiKey 未配置" and exits silently — easy to miss.
- **`AiExplodeAdvisor.ApplyRebuild`** is parameterized on `IExplodeStepEditor` (`SwExplodeStepEditor` in production, `RecordingEditor` in tests). The interface uses `object` (not `IComponent2`) for the "component handle" so tests can pass plain objects without depending on COM RCWs.

## Testability patterns (when adding new code)

- Pure helpers in `SwSopAddin.Services` should be `internal` + covered by `InternalsVisibleTo` — follow the `ExplodeService.ShouldSkip` / `GetComponentBaseName` pattern.
- Anything that needs to swap out COM behavior for tests: define an interface (like `IExplodeStepEditor`), have the production class wrap SW RCW calls, inject the interface into the orchestrator. `SopWorkflow`'s constructor takes all 6 `I*Service` interfaces — add new ones there.
- Step 0 document validation goes through the injected `IDocumentValidator` (W9), not a static call — `SwDocumentValidator` in production, `MockDocumentValidator` in tests. This is the pattern to copy for the last SW touch-points: wrap the static/COM call behind an interface and inject it into `SopWorkflow`. `SopWorkflow` L76/L80/L95 keep **defensive null checks** so a mock returning a null asm/drw still walks all 7 steps.

## User-facing config

`%AppData%\SwSopAddin\config.json` (overwritten by `ConfigStore.Save` whenever the UI Config form closes). All numeric defaults are in `ConfigStore` constructors — search for `= ` to find them. AI advisor section:

```json
"AiAdvisor": {
  "Enabled": true,
  "BaseUrl": "https://api.minimaxi.com/anthropic",
  "ApiKey": "sk-cp-...",
  "Model": "MiniMax-M3",
  "MaxRounds": 3
}
```

## Logging

NLog writes to `%AppData%\SwSopAddin\logs\sop-YYYY-MM-DD.log` (rolling by date, 10 MB, 14 archives). Use `Logging.ForType(typeof(Foo))` per class, not `LogManager.GetCurrentClassLogger()`. Verbose debug is enabled — when debugging, run `bash .claude/hooks/ai-log-dump.sh` for a grepped view of the latest log (AI / M3 / screenshot / step changes), or `grep -E "AI 评估|ModifyExplodeStep|GetExplodedViewNames" <logfile>` for targeted searches. `env-var-sanity-check.sh` proves the bash → child process → `Environment.GetEnvironmentVariable` chain works on this box (run it any time SW-launched AddIn doesn't see env vars set in bash).

## Where to start reading

For a fresh Claude instance landing in this repo, the reading order is:

1. `src/SwSopAddin.Host/SwSopAddinPlugin.cs` — `ISwAddin` impl, menu wiring, `OnGenerate` and `OnStepByStep` are the two entry points (one-shot / per-step).
2. `src/SwSopAddin.Orchestration/SopWorkflow.cs` — `RunMvp` runs the 7-step pipeline; `RunStep(sw, config, stepNumber)` runs a single step. Both share `RunStepN_*` private helpers.
3. `src/SwSopAddin.Adapter/SwApiWrapper.cs` — the **only** place COM is touched directly; everything else goes through here.
4. The relevant `*Service.cs` for whatever step you're touching (e.g. `ExplodeService.cs` for M2, `ViewService.cs` for M4).
5. `src/SwSopAddin.Infrastructure/ConfigStore.cs` for user-facing config schema; `AppPaths.cs` for log/config directory locations.
6. `src/SwSopAddin.UI/StepChoiceForm.cs` for the per-step debug UI; `ConfigForm.cs` for the config UI.
7. `C:\Users\mozhi\.claude\projects\D--Source-SwSopAddin\memory\w3-m4-m6-root-cause.md` and `w6-fix-m2-m4-m5-m6-wiring.md` for *why* the code looks the way it does.

## End-to-end SOP generation checklist (for the user)

**Prerequisites (one-time)**:
1. SolidWorks 2024 + AddIn registered (run `Register.bat` as Admin if you moved the DLL, changed `.csproj`, or are on a new machine).
2. If using AI advisor: `export ANTHROPIC_API_KEY=sk-cp-...` (production); `%AppData%\SwSopAddin\config.json`'s `AiAdvisor.ApiKey` is fallback. **Prefer env var** (plaintext gets logged).
3. Set `AiAdvisor.Enabled=true` in `config.json` (default `false`; plugin won't phone home without it).

**Per run**:
1. SW opens a `.SLDASM` (must be asm, not part or drawing).
2. Menu → `SOP 生成器` → `一键生成 SOP` (or `分步执行` → pick a step 1-7 for debugging).
3. Popup shows the result summary.

**If the popup isn't ideal, check NLog** at `%AppData%\SwSopAddin\logs\sop-YYYY-MM-DD.log` (or run `bash .claude/hooks/ai-log-dump.sh` for AI-related greps).

**Common issues** (see the full table in older versions of this file, or grep log for the symptom):

| Symptom | Look in log for | Fix |
|---|---|---|
| 1 explode step (expected 10+) | `Phase 1` `processed/failed/totalComps` | Default `SkipNamePrefixes` is `[]`; if still 1 step, check asm actual component count (pattern / light-weight instances may need `ResolveAllLightWeightComponents`) |
| 0 balloons | `ApplyAutoBalloon` "0 balloons" | explode view name wrong or components filtered by `ShouldSkip` |
| BOM 0 / not inserted | `BOM table inserted` | BOM template path — confirm `D:\SW\SOLIDWORKS\lang\chinese-simplified\bom-standard.sldbomtbt` exists |
| AI "apiKey 未配置" | `apiKey 来源 = ...` | env var not set AND `config.json` `ApiKey` is empty |
| AI "发送请求时出错 / SSL" | `SecurityProtocol` line | Should be fixed (`ServicePointManager.SecurityProtocol = Tls12`); if it recurs check .NET 4.8 runtime |
| AI returned but `ApplyRebuild` 0 changes | `ComputeChangeSet: N 个 step 待重建` | M3 returned field names don't match DTO; `ParseM3Response` is case-tolerant, but check `M3 text block:` in log for actual content |

**Known incomplete** (do not try to "fix" these without user agreement):
- F16 cross-sheet pagination: view beyond sheet is F15-scaled to `MinViewScale` (default 0.1), then `MinScaleClamped++` recorded, but not actually split to a second sheet.
- POC 1 PrintWindow screenshot: log shows success and the file is written, **but user hasn't visually confirmed the image is non-blank** (SW background window may render black).
- API key default reads from `config.json`; production should use `ANTHROPIC_API_KEY` env var (code already supports this — just clear `config.json`'s `ApiKey`).
