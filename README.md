# DotnetPublishTool

一个 Windows 桌面（WPF）小工具：**选择 dotnet 项目 → 找到它的发布配置 → 按配置或修改后的配置发布到默认目录 → 可选打成 zip**。

核心约束：**全程不改动源项目里的任何文件**。界面上改过的发布配置会另存到输出目录的副本，发布时把这份副本交给 MSBuild。

## 功能

- 选择 `.csproj` / `.vbproj` / `.fsproj`，或 `.sln` / `.slnx`（从解决方案里列出项目再选）。
- **记住最近选择的项目 / 解决方案**（去重、最多 20 条、最近的在前），下拉即可复选，可一键「清除记录」；
  启动时自动恢复最近一个仍然存在的项目。列表显示为「文件名 —— 末尾两级目录」，悬停可看完整路径。
- 自动发现项目 `Properties/PublishProfiles/*.pubxml`；一个都没有时给出一份可用的默认 Folder 配置。
- 「常用设置」可视化编辑：生成配置、目标框架、运行时标识符、**运行时依赖（框架依赖 / 自包含）**、单文件、ReadyToRun、裁剪、环境名、版本覆盖；
  下方实时显示「发布方式：框架依赖 · win-x64」这样的摘要，不用自己从复选框反推。
- 「其他属性」表格保留并可直接编辑配置里的其余 MSBuild 属性（Platform、PublishProtocol、_TargetId 等）。
- 「配置预览」实时显示本次将要写出的 `.pubxml` 内容。
- 发布到默认目录，可选发布前清空发布目录、可选 `--no-restore`。
- 发布成功后可选生成 zip；命名支持项目名 / 项目名_版本号 / 项目名_日期 / 自定义。
- 运行日志实时显示 dotnet 原始输出，可取消正在进行的发布。

## 目录约定

默认输出根目录是 **文档下的 `DotnetPublishTool`**，可在界面上修改：

```text
<文档>\DotnetPublishTool\
  <项目名>\
    profiles\<配置名>.pubxml          # 本次生效的配置副本（源文件不动）
    publish\                           # 发布产物
    <项目名>[-_版本号|_日期].zip         # 可选的压缩包
```

例：`C:\Users\<你>\Documents\DotnetPublishTool\SampleConsoleApp\publish`。

### 输出目录不可写时

发布前工具会**真实写一次**来探测输出根目录是否可写。若不可写（受控文件夹访问、受限账户、沙箱/低完整性进程等），
会依次尝试 `%LOCALAPPDATA%\DotnetPublishTool` → `%USERPROFILE%\DotnetPublishTool` → `%TEMP%\DotnetPublishTool`，
自动改用第一个可写目录，并弹窗 + 写日志明确告知；不会再抛出 `UnauthorizedAccessException`。
想固定用「文档」目录，只要确认当前账户对该目录有写权限即可。

同理，工具也会检查项目目录是否可写（`dotnet publish` 要在其下写 `obj/bin`），不可写时提前在日志里警示。

设置与「最近项目」记录默认存放在 `%APPDATA%\DotnetPublishTool\settings.json`；
该位置不可写时自动退到 `%TEMP%\DotnetPublishTool\settings.json`，避免记录悄悄丢失。

## 压缩包命名

默认口径是「项目名_日期」。

| 命名方式 | 结果示例 |
| --- | --- |
| 项目名 | `SampleConsoleApp.zip` |
| 项目名_版本号 | `SampleConsoleApp_1.4.2.zip` |
| 项目名_日期（默认） | `SampleConsoleApp_20261006_171712.zip` |
| 自定义 | `<自定义名>.zip` |

- 日期格式默认 `yyyyMMdd_HHmmss`，精确到秒（同一天多次发布不会互相覆盖），可改。
  文件名不能含 `:`，所以日期与时间之间用 `_` 分隔。
- 版本号取项目自身的 `Version`；解析不到时用 `unknown`，也可在「版本覆盖」里指定。
- 压缩包内**默认不包含**顶层文件夹（解压出来就是文件本身）；勾选「压缩包内包含以项目名命名的顶层文件夹」后才会多一层。

## 环境要求

- Windows
- .NET SDK 10（本仓库目标框架 `net10.0` 与 `net10.0-windows`）

## 构建与运行

```powershell
dotnet build DotnetPublishTool.slnx
dotnet run --project src/DotnetPublishTool.App
```

发布本工具自身（顺便当作一次真实验证）：

```powershell
dotnet publish src/DotnetPublishTool.App/DotnetPublishTool.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/app
```

## 辅助工具

`tools/DotnetPublishTool.Smoke` 是不带界面的端到端冒烟工具，也用于人工验证：

```powershell
# 解析项目并列出发现的发布配置
dotnet run --project tools/DotnetPublishTool.Smoke -- list samples/SampleConsoleApp/SampleConsoleApp.csproj

# 按配置发布 + 打包
dotnet run --project tools/DotnetPublishTool.Smoke -- publish samples/SampleConsoleApp/SampleConsoleApp.csproj --out .smoke-out --profile FolderProfile --zip-mode ProjectNameVersion
```

图形程序本身也带无界面自检（会真实解析 XAML 并跑一次发布）：

```powershell
src/DotnetPublishTool.App/bin/Debug/net10.0-windows/DotnetPublishTool.exe --selftest --project samples/SampleConsoleApp/SampleConsoleApp.csproj --out .selftest --profile FolderProfile --zip
```

## 目录结构

```text
src/DotnetPublishTool.Core/    发布逻辑主体（无界面依赖，可单独测试）
src/DotnetPublishTool.App/     WPF 界面（MVVM，无第三方依赖）
tools/DotnetPublishTool.Smoke/ 端到端冒烟工具
samples/SampleConsoleApp/      带两份发布配置的示例项目
docs/设计说明.md                机制、关键决定与限制
```

## 已知限制

- 只支持 SDK 风格项目；旧式（非 SDK）`.csproj` 无法用 `dotnet publish`，界面会直接拒绝。
- 只做文件夹（`PublishProtocol=FileSystem`）发布；Web Deploy / MSDeploy 等需要目标服务器的协议不适用，发布会给出提示。
- `PublishSingleFile`、自包含（`SelfContained=true`）等需要额外运行时包；本机访问不到 NuGet 时会还原失败，工具会原样显示 dotnet 的错误信息。

## 排障：写「文档」目录被拒绝

**现象**

```text
System.UnauthorizedAccessException: Access to the path
'C:\Users\<你>\Documents\DotnetPublishTool\<项目名>\profiles' is denied.
```

**根因不是 ACL，而是 Windows 强制完整性级别**：带 `Mandatory Label\Low Mandatory Level` 标签的 exe，启动后进程就是 **Low 完整性**，写「文档」「%APPDATA%」这类 Medium 对象会被系统拒绝 —— **与从哪里启动无关**（双击、Visual Studio、普通终端都一样）。完整机制与通用规则见全局 `~/.dsh/AGENTS.md`。

用 DSH 沙箱构建会污染：工作区（含 `bin`/`obj`）被打上可继承的 Low 标签，编译产物继承之。查证：

```powershell
icacls .\src\DotnetPublishTool.App\bin\Debug\net10.0-windows\DotnetPublishTool.exe | Select-String Mandatory
```

**修复**（必须在**普通 PowerShell** 里跑；沙箱内 `icacls` 会返回 exit 5，脚本会主动拦住并提示）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\fix-build-integrity.ps1           # 修复
powershell -ExecutionPolicy Bypass -File .\tools\fix-build-integrity.ps1 -Verify   # 只查看
```

脚本把仓库内所有 `bin`/`obj`（以及 `artifacts`）的完整性标签升回 Medium；已构建的 exe 一并升级，**直接用现有 exe 重新启动即可**。

> 副作用：这些目录变成 Medium 后，**低完整性的沙箱就写不进去了** —— 之后的构建请在 Visual Studio 或普通终端里做。

**运行时自检**：工具启动时会在日志打印 `[环境] 进程完整性级别：…`；级别偏低导致输出目录不可写时，弹窗与日志会直接说明原因并给出上面的修复命令。
