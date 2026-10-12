# AGENTS.md

本仓库的 agent 入口，只放导航与必要约束；具体内容写在对应文档里，避免重复。

## 这是什么

WPF 图形化的 dotnet 发布/打包工具：选项目 → 找发布配置 → 按配置或修改后的配置发布到默认目录 → 可选打 zip、可选生成装到 Program Files 的 MSI 安装包。功能与用法见 [README.md](README.md)。

## 什么情况下读哪个文档

| 情况 | 读 |
| --- | --- |
| 了解功能、目录约定、压缩包命名、构建运行 | [README.md](README.md) |
| 改发布机制、配置发现与读写、命令构造、排查发布异常 | [docs/设计说明.md](docs/设计说明.md) |

## 实现入口

- 数据模型：`src/DotnetPublishTool.Core/Models/`
- 发布配置发现 / 读取 / 写出：`src/DotnetPublishTool.Core/Services/PublishProfileStore.cs`
- 发布编排（核心，先看这里）：`src/DotnetPublishTool.Core/Services/PublishWorkflow.cs`
- 项目探查与类型识别：`src/DotnetPublishTool.Core/Services/ProjectInspector.cs`
- 命令行构造：`src/DotnetPublishTool.Core/Services/PublishCommandBuilder.cs`
- MSI 安装包：`src/DotnetPublishTool.Core/Services/MsiPackageBuilder.cs`（配合 MsiDatabase / MsiNative / CabinetBuilder）
- MSI 版本自动递增 / 写回项目：`src/DotnetPublishTool.Core/Services/MsiVersionService.cs`、`CsprojVersionEditor.cs`（唯一会改源文件的功能，默认关闭）
- 本工具自身的安装包：`tools/make-installer.ps1`（**必须在普通终端跑**，自带沙箱检测）
- 界面：`src/DotnetPublishTool.App/MainWindow.xaml`、`src/DotnetPublishTool.App/ViewModels/MainViewModel.cs`
- 端到端验证：`tools/DotnetPublishTool.Smoke`、`src/DotnetPublishTool.App/SelfTest.cs`

## 常用命令

```powershell
dotnet build DotnetPublishTool.slnx
dotnet run --project src/DotnetPublishTool.App
dotnet run --project tools/DotnetPublishTool.Smoke -- publish samples/SampleConsoleApp/SampleConsoleApp.csproj --out .smoke-out --profile FolderProfile
```

**界面改完要目视核对**：本应用的离屏截图开关是 `--selftest --render <png>`（沙箱里显示不了窗口）。
方法与坑见全局 skill `wpf-offscreen-render`，本仓库只需这一条：

```powershell
<App 的 exe> --selftest --render .ui.png --project samples/SampleConsoleApp/SampleConsoleApp.csproj --out .render-out
```

**MSI 升级行为要在管理员终端验**：`tools/verify-msi-upgrade.ps1`（现场打两个版本的包、临时产品名 + 临时目录，断言旧版被卸掉、新版文件被换；沙箱 / 非提权会话直接拒绝）。排查「升级没卸旧版」时加 `-ProbeVariants` 跑变体矩阵（改 Remove / REP 位置 / Attributes，逐种试）。

## 本机环境注意事项

- **解决方案级构建需要 `-m:1`**。在受限 shell / DSH 沙箱里，MSBuild 多节点并行通信会被拦，`dotnet build DotnetPublishTool.slnx` 会以「0 错误但生成失败」的形态失败；加 `-m:1` 即可（普通终端无此问题）。单项目构建不受影响。
- **本机访问 nuget.org 不可达**。需要新 NuGet 包的发布（`PublishSingleFile`、自包含等）会报 `NU1301`；普通的框架依赖发布不受影响。
- **新建 `.cs` / `.xaml` 后要确认是 CRLF**：`.gitattributes` 把这些类型声明为 `text eol=crlf`，而写文件的工具默认输出 LF —— 直接 `git add` 会报 `fatal: LF would be replaced by CRLF`，而且**整条 `git add` 会一个文件都不入暂存区**。`.md` 是 `eol=lf`，保持 LF。
- **本仓库的 `.ps1` 是 UTF-8 with BOM**：Windows PowerShell 5.1 读无 BOM 的 `.ps1` 会按 ANSI 解码，脚本里的中文会变乱码并报语法错误。编辑工具可能吃掉 BOM —— 改完 `tools/*.ps1` 后确认头三字节是 `EF BB BF`，换行保持 CRLF（`.gitattributes` 已声明 `*.ps1 text eol=crlf`）。
- **完整性标签（Mandatory Label / Low）**：机制、定性方法与"沙箱自测必须用独立目录"的通用规则见全局 `~/.dsh/AGENTS.md`，这里只记本仓库特有的事。
  - 修复脚本：`tools/fix-build-integrity.ps1`（**必须在普通终端跑**；脚本自带沙箱检测，会拒绝在沙箱内执行）。
  - 本仓库的 `bin`/`obj` **已升为 Medium**，因此沙箱内不能再直接构建。沙箱内验证走重定向输出：
    `dotnet build DotnetPublishTool.slnx -m:1 -p:UseArtifactsOutput=true -p:ArtifactsPath='<工作区>\.artifacts-verify'`；
    要跑真实发布，把示例项目**不带 bin/obj** 复制到工作区再发布。正式构建交给 VS / 普通终端。
  - 工具自身能自检：启动时日志打印 `[环境] 进程完整性级别：…`，输出目录不可写且级别偏低时直接给出原因与修复命令。
  - 自测产物一律放工作区内的专用目录，且均已 gitignore：`.selftest/`、`.smoke-out/`、`.zip-verify/`、`.render-out/`、`.artifacts-verify/`。
