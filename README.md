# DotnetPublishTool

一个 Windows 桌面（WPF）小工具：**选择 dotnet 项目 → 找到它的发布配置 → 按配置或修改后的配置发布到默认目录 → 可选打成 zip**。

核心约束：**默认全程不改动源项目里的任何文件**。界面上改过的发布配置会另存到输出目录的副本，发布时把这份副本交给 MSBuild。唯一的例外是 MSI 面板里显式勾选的「并把新版本写回项目 csproj」（见 [MSI 安装包](#msi-安装包)），默认关闭，且写之前会把原文件备份到输出目录。

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
- 发布成功后可选生成 **MSI 安装包**（装到 `Program Files`、出现在「程序和功能」里、可 `msiexec` 静默安装/卸载）。
- 运行日志实时显示 dotnet 原始输出，可取消正在进行的发布。

## 目录约定

默认输出根目录是 **文档下的 `DotnetPublishTool`**，可在界面上修改：

```text
<文档>\DotnetPublishTool\
  <项目名>\
    profiles\<配置名>.pubxml          # 本次生效的配置副本（源文件不动）
    publish\                           # 发布产物
    <项目名>[-_版本号|_日期].zip         # 可选的压缩包
    <产品名>_<版本号>.msi               # 可选的 MSI 安装包
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
- 版本号取项目里**最大的那个版本键**：`Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion`（只写了后三个、没写 `Version` 的项目也能取到应用真正在用的版本）；解析不到时用 `unknown`，也可在「版本覆盖」里指定。
- 压缩包内**默认不包含**顶层文件夹（解压出来就是文件本身）；勾选「压缩包内包含以项目名命名的顶层文件夹」后才会多一层。

## MSI 安装包

勾选「发布成功后生成 MSI 安装包」后，工具会把发布目录打成一个安装包：终端用户双击（或 `msiexec /i`）即可装到 **`%ProgramFiles%\<产品名>`**，并在「程序和功能」里出现卸载项；勾选时会创建开始菜单快捷方式（指向发布产物里的主程序，卸载时一并移除）。

```powershell
msiexec /i SampleConsoleApp_1.4.2.msi /qn     # 静默安装（需要管理员/UAC：装到 Program Files）
msiexec /x SampleConsoleApp_1.4.2.msi /qn     # 静默卸载
msiexec /a SampleConsoleApp_1.4.2.msi /qn TARGETDIR=D:\image\   # 管理安装：铺出网络/组策略部署用的源映像
```

| 字段 | 说明 |
| --- | --- |
| 产品名 | 「程序和功能」、开始菜单与安装目录名；留空用项目名。也是 MSI 的 `ProductName` |
| 发布者 | MSI 的 `Manufacturer`；留空用产品名 |
| 命名方式 | 产品名 / 产品名_版本号（默认）/ 自定义，扩展名为 `.msi` |
| 安装版本 | 取「版本覆盖」或项目版本，按 MSI 规则规整成 `x.y.z`（不合法时用 `1.0.0`） |
| 创建开始菜单快捷方式 | 关掉则只安装文件 |
| 打包时自动递增版本 | 每次生成 MSI 前把版本递增一位：1.4.2 → 1.4.3、2.1.2.0 → 2.1.3.0（1.4 → 1.5；1.4.65534 → 1.5.0）。基准取「版本覆盖」或项目版本（四个版本键里最大的那个），也会跟本机记录的上次版本取较大者 |
| 并把新版本写回项目 csproj | 勾选时把新版本写回项目 `<Version>`（**本工具唯一会改动源文件的地方**，写前把原文件备份到输出目录的 `csproj-backup`，保留最近 5 份）；关掉则新版本只作用于本次打包 |

**升级**：升级代码由「发布者 + 产品名」稳定推导（也可用 `UpgradeCode` 固定）。只要这两项不变，装新版本时安装包会先卸掉旧版本，不会在「程序和功能」里留下多条。

**自动递增版本**：勾上以后「一次发布只用一个版本号」——产物、zip 名、安装包、写回项目的都是它。

**项目版本怎么取**：只看**项目文件里显式声明**的版本键 —— `Version`（或 `VersionPrefix` + `VersionSuffix`）、`InformationalVersion`、`FileVersion`、`AssemblyVersion`，取其中**数值最大**的那个（并列时优先 `Version`）。

- SDK 由 `<Version>` 自动推导出来的那些值**不算独立来源**：手动把 `<Version>` 改小/改回去，点「重新扫描」就立刻生效，不会被推导值顶住。
- 只写了后三个键的项目（例如三个都是 `2.1.2.0`、没写 `Version`）拿到的是 2.1.2.0，而不是 SDK 默认的 1.0.0。
- 版本写在 `Directory.Build.props` 之类项目文件之外时，退回 MSBuild 评估出的 `Version`。

**递增规则**：基准优先级「MSI 显式版本 → 版本覆盖 → 项目版本」。勾选写回时**项目文件就是唯一真相** —— 手动把版本改小、改回去都立刻生效；只有不写回时才跟本机记录取较大者（否则每次都从同一个项目版本 +1，会反复产出同号安装包）。递增保留段数（`1.4→1.5`、`1.4.2→1.4.3`、`2.1.2.0→2.1.3.0`，尾段清零），而且**一定落在 MSI 看得到的前三段**里——否则安装包版本不变、升级会失效。MSI 只认 `x.y.z`，超上限（前两段 ≤255、第三段 ≤65534）自动进位，实在加不动（大段已经 255）或解析不出版本时直接报错，不会产出一个错版本。

- 写回项目：**只改值等于基准版本的那些版本键**（例如三个键都是 2.1.2.0 就三个一起改成 2.1.3.0；被刻意钉住的 `AssemblyVersion` 不会被误改），一个都不匹配时才改/插 `<Version>`；**只有这一处会动源文件**，写前把原 csproj 备份到 `<输出目录>/<项目名>/csproj-backup/`。
- 不写回：新版本记在 `%APPDATA%\DotnetPublishTool\msi-versions.json`（不可写时退到 `%TEMP%\DotnetPublishTool\`），下次接着往上加。
- 只有安装包真的生成成功才推进版本——MSI 失败时版本不变，重跑还是同一个号。
- **新版本会一起交给编译**：勾选「版本覆盖」或自动递增时，`Version` / `SAssemblyVersion` / `FileVersion` / `InformationalVersion` 四个版本键一起设成新版本。只给 `Version` 是压不住项目里显式写的后三个的 —— 那样会出现「安装包是 2.1.3、应用自己还报 2.1.2」的不一致。写回 csproj 仍然只在安装包成功之后做，但**编译用的是新版本**。
- 发布成功后界面会**自动重新读取项目版本**（项目那一行、以及 zip / 安装包 / 本次版本的预览都会跟着更新），不用手动点「重新扫描」；重新读取只刷新项目信息，不会冲掉你在界面上改过的常用设置与「其他属性」。

**实现**：不依赖 WiX，也不联网 —— 直接用系统自带的 `msi.dll` 写 Windows Installer 数据库，用系统自带的 `makecab.exe` 压 CAB（作为内嵌流写进安装包，因此安装包只有一个文件）。

### 本工具自身的安装包

`tools/make-installer.ps1` 会先 `dotnet publish` 发布界面，再用上面同一套能力生成 `DotnetPublishTool_<版本>.msi`（默认输出到 `artifacts/installer`）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\make-installer.ps1                          # 版本取项目评估值（当前 1.3.0）
powershell -ExecutionPolicy Bypass -File .\tools\make-installer.ps1 -Version 1.3.0 -Manufacturer "某某公司"
```

**必须在普通（非沙箱）终端运行**：沙箱里 `dotnet publish` 写不了项目的 `obj`，产物还会继承 Low 完整性标签（脚本自带检测，会直接拒绝）。

**版本号就是「能不能升级」**：唯一来源是 [DotnetPublishTool.App.csproj](src/DotnetPublishTool.App/DotnetPublishTool.App.csproj) 里的 ``Version``（当前 `1.3.0`），CI 与 `tools/make-installer.ps1` 都读它。升级代码由「发布者 + 产品名」稳定推导（要一直保持不变）；**产品码每次打包新生成**，所以同一个版本号重打出来的包也能装：安装时会先卸旧、再装新（等价 WiX 的 `AllowSameVersionUpgrades`，升级范围上限含本版本、防降级那条不含本版本）。只有**同一个 .msi 文件连装两次**会走维护模式，那种情况在「程序和功能」里点「修复」即可。发新版就改 ``Version`` 这一处，然后打 `v<版本>` tag；tag 与 ``Version`` 不一致时 CI 会直接失败。

**目标机要求**：框架依赖的产物要求目标机已装 **.NET 10 桌面运行时**（要自带运行时用 `-SelfContained`，体积大，且本机访问不到 NuGet 时会在还原阶段报 `NU1301`）。安装包不做代码签名，SmartScreen 会提示「未知发布者」。

### MSI 的已知限制

- **没有安装向导**：安装包不包含对话框（`InstallUISequence` 里只有 `ExecuteAction`），双击即按默认设置安装到 `Program Files`，不会出现「下一步」。
- 只做**每机器安装**（`ALLUSERS=1`），需要管理员权限；不支持装到用户目录。
- 空目录不会被创建（只按文件建目录）。
- 框架依赖的发布要求目标机器已装对应的 .NET 桌面运行时；要连运行时一起装，用「自包含」发布后再打 MSI。
- **同一个版本号重新打包发出去，装的时候不会更新文件**：产品码由「升级代码 + 版本」推导，版本没变就是同一个产品 —— 再装只会走 MSI 的「维护模式」，既不铺文件也不卸旧版。要更新就必须先升 `<Version>` 再打包。
- **升级时文件会不会被覆盖，取决于 File 表里的 `Version` 列**：这一列空着时 MSI 会把文件当成「无版本文件」，改用文件日期比较 —— 已装过的文件日期更新时就不覆盖（v1.1.0 的「装完还是旧程序」就是这个原因，已修）。
- 升级行为（卸旧版 + 换文件）可以用 `tools/verify-msi-upgrade.ps1` 验：它会现场产出两个版本的包、用临时产品名装到临时目录、断言「旧版被卸掉、新版文件到位、程序和功能只剩一条」，跑完自动清理。**必须在管理员终端里跑**（每机器安装/卸载需要管理员权限）。
- 加 `-ProbeVariants` 时脚本会跑**变体矩阵**：逐个改新版包 `Upgrade` 表的 `Attributes`（3 / 258 / 512 / 257 / 259），验证「带 `OnlyDetect(0x2)` 就只检测不卸载」这条因果 —— 这是排查「升级没卸旧版」最快的办法，每种变体各留一份日志。
- **这些缺陷都在生成器里，不在某个项目的打包流程里**：修好一次，之后给任何项目打的包都带修复；**修复之前打的包（本仓库 v1.0.0 / v1.1.0，以及你用本工具打过的其它项目）都要用修好的版本重打一次**。另外三件属于「每个项目自己的约束」：发出去的包每次递增版本号（同版本重装虽然能装上，但用户没法从版本号区分两次发布；打包时工具也会提醒）、载荷文件最好带版本资源、产品名/发布者不要改（升级码由它们推导）。
- **桌面快捷方式**：默认与开始菜单一起创建（`Shortcut` 表两条；桌面那条挂在 `DesktopFolder` 上）。每机器安装时 `DesktopFolder` 解析成**公共桌面**（所有用户都看到），每用户安装时是当前用户桌面；卸载时一并移除。想要纯净安装用 `--no-msi-desktop-shortcut`（`tools/make-installer.ps1 -NoDesktopShortcut`）。
- **每次打包都会自检**：卸旧版那行不能带 `OnlyDetect`、防降级那行必须在、`File.Version` 要与源文件对得上、`Error` / `LaunchCondition` 表不能空；不合格直接报错、不产出安装包。
- **装到 Program Files 的写路径提醒**：产物里的 `NLog.config` 若用 ``${basedir}`` 或相对路径当**写路径**（`fileName` / `archiveFileName` / `internalLogFile`），打包时会警告 —— 普通用户对安装目录只有读权限，日志会全部丢失（NLog 默认静默失败；开了 `throwExceptions` 会直接启动不起来）。建议写路径改用 ``${specialfolder:folder=LocalApplicationData}`` 或 ``${environment:variable=LOCALAPPDATA}``，装到用户可写目录。LaserMotionCardMap 2.1.6 装完没日志就是这个原因（2026-10-14）。
- **升级卸旧版靠 ``Upgrade`` 表的 ``Attributes``**：`0x2` 是 `OnlyDetect`（只检测、**不卸载**），`0x100`/`0x200` 是版本区间端点是否含。现在「卸旧版」那行是 `0x100|0x200`（=768，上限含本版本，同版本重装才卸得掉），防降级那行只有 `0x2`（下限不含本版本）。写错成含 `OnlyDetect` 时：检测正常、安装成功、旧版本却原样留着（v1.1.0 的「升级不卸旧版」就是这个原因，2026-10-13 定位）。

## 环境要求

- Windows
- .NET SDK 10（本仓库目标框架 `net10.0` 与 `net10.0-windows`）

## 构建与运行

```powershell
dotnet build DotnetPublishTool.slnx

> 应用图标（窗口、任务栏、安装包快捷方式都用它）由 `tools/make-app-icon.ps1` 生成：脚本自己画图 + 组装 16/20/24/32/40/48/64/128/256 九个尺寸的 `src/DotnetPublishTool.App/app.ico`。想换样式改脚本里的图形段，跑一次即可（`-Preview` 会顺带出一张各尺寸并排的预览图）。
dotnet run --project src/DotnetPublishTool.App
```

## 发布本仓库（把工具打成分发件）

发布 = 用 Release 配置把 WPF 界面打成一个能发给别人的产物。两种口径，**都必须在普通终端（非 DSH 沙箱）里跑**：沙箱内项目 `obj` 是 Medium，写不进去；就算写进去，产物也会继承 Low 完整性标签（见文末排障）。

**① 只发文件夹 / 自己压 zip**（拿到 `artifacts/app` 后自己处理；也可以用本工具对 `src/DotnetPublishTool.App` 打一次包，顺便当一次真实验证）：

```powershell
dotnet publish src/DotnetPublishTool.App/DotnetPublishTool.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/app
```

**② 发 MSI 安装包**（装到 `Program Files`、出现在「程序和功能」、带开始菜单快捷方式）——见上面的 [本工具自身的安装包](#本工具自身的安装包)，版本号规则与目标机要求也在那一节。

### 用 GitHub Actions 出包（推荐）

推 `v<版本>` tag 就在 CI 上出包并自动建 Release：

```powershell
# 1) 先改 src/DotnetPublishTool.App/DotnetPublishTool.App.csproj 里的 <Version>
# 2) 提交后打 tag —— tag 必须与 <Version> 完全一致，否则 CI 直接失败
git tag v1.3.0
git push origin main --tags
```

CI（`.github/workflows/release.yml`，windows-latest + .NET 10 SDK）对两套口径各出一份，文件名带版本与变体：

```text
DotnetPublishTool_1.3.0_win-x64_framework-dependent.msi / .zip   # 体积小；目标机需 .NET 10 桌面运行时
DotnetPublishTool_1.3.0_win-x64_self-contained.msi / .zip        # 自带运行时；体积大
```

- **Release 正文按规范提交生成**：CI 调 `tools/make-release-notes.ps1`，把上一个 tag 到本 tag 之间的提交按 `feat` / `fix` / `docs`… 分组渲染（提交正文里的「- 说明」会作为子条目带出来），并附下载清单与 compare 链接；Release 已存在时正文也会被刷新。本地预览：`pwsh tools/make-release-notes.ps1 -Tag v1.1.2`。
- 只想要包、不想建 Release：在 Actions 里手动 `Run workflow`（产物在该次运行的 Artifacts 里，保留 30 天）；勾上 `make_release` 才会建 Release。
- 手动 / 离线出包仍然用上面的 `tools/make-installer.ps1`。

## 辅助工具

`tools/DotnetPublishTool.Smoke` 是不带界面的端到端冒烟工具，也用于人工验证：

```powershell
# 解析项目并列出发现的发布配置
dotnet run --project tools/DotnetPublishTool.Smoke -- list samples/SampleConsoleApp/SampleConsoleApp.csproj

# 按配置发布 + 打包
dotnet run --project tools/DotnetPublishTool.Smoke -- publish samples/SampleConsoleApp/SampleConsoleApp.csproj --out .smoke-out --profile FolderProfile --zip-mode ProjectNameVersion

# 发布并生成 MSI 安装包（产品名/发布者可指定）
dotnet run --project tools/DotnetPublishTool.Smoke -- publish samples/SampleConsoleApp/SampleConsoleApp.csproj --out .smoke-out --msi --msi-product "示例程序"

# 只把已有的发布目录做成 MSI
dotnet run --project tools/DotnetPublishTool.Smoke -- pack-msi .smoke-out/SampleConsoleApp/publish --out .smoke-out/SampleConsoleApp
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
- MSI 生成依赖 Windows 自带的 `msi.dll` 与 `makecab.exe`，只能在 Windows 上跑；安装包不做代码签名，SmartScreen 会提示未知发布者。

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
