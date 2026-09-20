# 灵工桌宠 · 安装器 / 卸载器（源码 + 构建 + 测试说明）

本目录提供「灵工桌宠」的 **安装程序 exe** 与 **卸载程序 exe** 的完整 C# 源码、构建脚本与验收测试脚本。
全部用 **Windows 自带 `csc.exe`（.NET Framework 4.x / C# 5 语法）** 编译，**不需要** dotnet SDK / node / nuget / 管理员权限。

---

## 1. 产物

| 产物 | 路径 | 说明 |
| --- | --- | --- |
| 安装程序 | `<工程根>\dist\灵工桌宠-安装程序.exe` | 单文件 winexe，`payload.zip` 以 `/resource:payload.zip,payload.zip` 内嵌 |
| 卸载程序 | `<工程根>\dist\app\卸载-灵工桌宠.exe` | 同时被塞进 `payload.zip`，安装后位于安装目录根下 |
| 安装负载 | `<工程根>\installer\payload.zip` | 内容 = 负载目录（`dist\app`，缺则用项目根 `app\`）下全部文件（主程序 + `assets\**` + 卸载器 + `使用说明.txt`） |
| 图标 | `<工程根>\installer\icon.ico` | 构建脚本用 System.Drawing 现画（深海军蓝底 + 金色舰装/锚徽记，6 个尺寸） |

源码与脚本（本目录）：

| 文件 | 作用 |
| --- | --- |
| `DeskPetSetup.cs` | 安装器源码（WinForms 纯代码 UI，无 .resx） |
| `Uninstaller.cs` | 卸载器源码（自搬迁到 `%TEMP%` 后清理） |
| `build-setup.ps1` | 一键构建：图标 → staging → payload.zip → 编译两个 exe → 自检 |
| `test-installer.ps1` | 验收测试（29 项断言，含 dry-run / 静默装 / 静默卸 / 注册表 / 快捷方式 / 数据清理） |
| `test-security.ps1` | **卸载安全模型验收**（57 项断言：共用目录不误删 / 重解析点不穿透 / 保护不可绕过 / 数据目录身份校验 / 正常路径回归） |
| `gui-check.ps1` | 图形模式补充验证（GUI 可启动不强杀、快捷方式真实创建、卸载器自搬迁、确认前不删任何东西） |

---

## 2. 构建方法

```powershell
# 一键构建（含自检）
powershell -NoProfile -ExecutionPolicy Bypass -File <工程根>\installer\build-setup.ps1

# 可选参数
#   -ProjectRoot '<项目根>'   指定项目根目录（默认取本脚本上一级目录，改名/移动后无需修改）
#   -SkipSelfTest            跳过构建末尾的 dry-run 自检
#   -ForceIcon               强制重新生成 icon.ico（默认已存在则复用）
#   -KeepStaging             保留 %TEMP%\alp-build-<pid> 工作目录便于排查
```

等价的**手工编译命令**（脚本内部就是这么做的，源码与 payload 先复制到 ASCII 临时目录再编译，规避 csc 对中文路径的挑剔）：

```bat
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set W=%TEMP%\alp-build

:: 卸载器
"%CSC%" /nologo /codepage:65001 /optimize+ /platform:anycpu /target:winexe ^
        /out:"%W%\Uninstaller.exe" /win32icon:"%W%\icon.ico" ^
        /r:System.Windows.Forms.dll /r:System.Drawing.dll "%W%\Uninstaller.cs"

:: 安装器
"%CSC%" /nologo /codepage:65001 /optimize+ /platform:anycpu /target:winexe ^
        /out:"%W%\Setup.exe" /win32icon:"%W%\icon.ico" ^
        /resource:"%W%\payload.zip",payload.zip ^
        /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
        /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
        "%W%\DeskPetSetup.cs"
```

> 关键点：源码是 UTF-8（无 BOM），**必须** `/codepage:65001`，否则中文文案全部乱码。
> 只使用 .NET Framework 自带程序集（WinForms / Drawing / IO.Compression），无任何 NuGet 依赖。

构建脚本的负载组装规则（重要）：

1. `dist\app\AzurLaneDeskPet.exe` 若是有效 PE（`MZ` 头且 > 4KB）→ 视为**真正的主程序**，直接作为负载；
2. 否则（不存在 / 只是残缺文件）→ 现场编译一个极小的 WinForms **占位主程序**，并补 `assets\ship-placeholder.png` 与 `使用说明.txt`，保证在没有主程序时也能构建 + 测试全流程；
3. 无论哪种情况，`assets\`（缺失才补）与 `使用说明.txt`（缺失才补）都会保证存在，最后 `robocopy` 进 staging，
   并在打包前剔除 staging 里上一轮残留的旧卸载器，再把本轮编译出的卸载器放进去。

---

## 3. 命令行参数

### 安装器 `灵工桌宠-安装程序.exe`

| 参数 | 说明 |
| --- | --- |
| `/S`、`--silent` | 无界面静默安装（任何情况下都不弹窗） |
| `/D=<目录>`、`--dir=<目录>` | 指定安装目录；带引号不带引号都兼容，含空格的路径请加引号 |
| `--no-shortcuts` | 不创建任何快捷方式 |
| `--no-desktop-shortcut` | 只建开始菜单快捷方式，不建桌面 |
| `--no-launch` | 安装完成后不自动启动主程序（默认启动） |
| `--dry-run` | 只报告不写盘（不建目录、不写注册表、不建快捷方式） |
| `--report=<文件>` | 把结果写成 UTF-8 文本报告（含各步骤与退出码说明） |

退出码：`0` 成功 / `2` 用户取消（GUI 覆盖升级选「否」）/ `3` 失败（原因见报告与日志）。
日志：`%TEMP%\AzurLaneDeskPet-setup.log`。

### 卸载器 `卸载-灵工桌宠.exe`

| 参数 | 说明 |
| --- | --- |
| `/S`、`--silent` | 静默卸载 |
| `--dir=<目录>` | 指定安装目录（不给则用自身所在目录） |
| `--purge-data` | 删除用户数据（**默认行为**，加此参数保持幂等） |
| `--keep-data` | 保留 `%APPDATA%\AzurLaneDeskPet` |
| `--dry-run` | 只报告不删任何东西 |
| `--report=<文件>` | 写 UTF-8 报告 |
| `--from-temp` | 内部使用：标记「我是临时副本」，避免无限自搬迁 |

退出码：`0` 成功 / `2` 用户取消 / `3` 失败。日志：`%TEMP%\AzurLaneDeskPet-uninstall.log`。

---

## 4. 行为说明（与主程序的契约）

* 主程序：安装目录根下的 `AzurLaneDeskPet.exe`，旁边有 `assets\`、`卸载-灵工桌宠.exe`、`使用说明.txt`、`install.json`。
* 默认安装目录：`%LOCALAPPDATA%\Programs\AzurLaneDeskPet`（免管理员权限），GUI 可点「浏览…」或直接改文本框换任意目录。
* 已存在同名程序 → 提示是否覆盖升级；升级只替换程序文件，**用户数据目录不动**。
* 清单 `install.json`（UTF-8）：`product` / `version` / `publisher` / `installDir` / `dataDir` / `shortcuts[]` /
  `registryKey` / `uninstaller` / `mainExe` / `files` / `marker` / `preexisting` / `estimatedSizeKb` / `installedAt`。
  自写的极简 JSON 写入器，字符串转义（`"` `\` 控制字符 `\uXXXX`）完整。
* **安装文件清单 `files.json`**（UTF-8）：记录本次安装写下的每个相对路径（文件 + 目录），是卸载的**唯一白名单**，
  另有标记文件 `.azurlandeskpet-install` 用于识别「这确实是本产品的安装目录」。详见第 8 节。
* 注册表（HKCU，免管理员）：`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AzurLaneDeskPet`
  写入 `DisplayName` / `DisplayVersion` / `Publisher` / `InstallLocation` / `DisplayIcon` /
  `UninstallString` / `QuietUninstallString` / `EstimatedSize`(DWORD,KB) / `NoModify=1` / `NoRepair=1`。
* 快捷方式：开始菜单 `%APPDATA%\Microsoft\Windows\Start Menu\Programs\灵工桌宠.lnk`（必建）、
  桌面（复选框，默认勾选）。用 `WScript.Shell` COM 创建，失败自动降级到 `powershell.exe` 的同一 COM 接口。
* 卸载流程：自复制到 `%TEMP%\AzurLaneDeskPet-uninstall-<随机>.exe` → `CreateProcess(DETACHED_PROCESS)`
  以 `--from-temp --dir="<安装目录>"` 重启 → 原进程退出 → 临时副本执行：
  1. 温和关闭 / `taskkill /IM AzurLaneDeskPet.exe /F /T` 结束主程序；
  2. **只删 `files.json` 清单内的文件与目录**（逐条校验：在安装目录之内、祖先与自身都不是重解析点）；
     不在清单里的文件一律保留。文件删完后只删「清单内且已空」的目录，安装目录本身也只在已空时才删；
  3. 删除 `%APPDATA%\AzurLaneDeskPet` 与 `install.json` / `config.json` 里的自定义 `dataDir`
     （正则提取，无第三方 JSON 库）——**必须先通过数据目录身份校验**（`.azurlandeskpet-data` 标记或
     `config.json` 含 `configVersion`），且只删已知条目；其它条目保留并列进报告；
  4. 删除 `install.json` 记录的全部快捷方式，并顺带删开始菜单、`%USERPROFILE%\Desktop`、`%PUBLIC%\Desktop` 的默认 `.lnk`；
  5. 删除 `HKCU\...\Uninstall\AzurLaneDeskPet` 与 `HKCU\Software\AzurLaneDeskPet`；
  6. 清理 `%TEMP%` 下**确定属于本产品**的日志/临时文件（`AzurLaneDeskPet-*`、`AzurLaneDeskPet-payload-*`；
     `alp-*` 仅在已空时才删），并**自删除** `%TEMP%` 临时副本与安装目录内的副本
     （删不掉则用逐条 `del` 的延迟脚本）；
  7. 结果弹窗报「共删除 N 个文件 / M 个目录」、列出**已保留**与**安全校验拒绝**的项目，带「查看日志」按钮；
     静默模式只写报告与日志，绝不弹窗。

---

## 5. UI 说明

* 底色 `#0E1B33 → #16305C` 垂直渐变，金色 `#E8C86A` 描边/标题，正文白色 `#F2F6FF`；日志区深底 + 等宽字体。
* 标题用 **宋体** 写「**灵工巧物 天祈智临**」，副标题为产品名与版本。
* 纯代码绘制：`GradientPanel`（渐变底）、`FramedPanel`（金色取景框 + 标题镶嵌）、`GoldButton`（自绘金按钮）、
  `GoldenTextBox`（金框输入框），无 `.resx`、无设计器。
* 高 DPI：`Application.EnableVisualStyles()` + `SetProcessDPIAware()`（user32 P/Invoke）。

---

## 6. 测试记录

环境：Windows 10/11 x64（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，C# 5），
PowerShell 5.1，`%TEMP% = %TEMP%`。
所有测试命令均为一次性执行、无人工点击；报告与临时目录最后全部清理。

### 6.1 构建

```
> powershell -NoProfile -ExecutionPolicy Bypass -File <工程根>\installer\build-setup.ps1
工作目录：%TEMP%\alp-build-51000
[0/5] 准备图标...            复用已有 icon.ico（26097 bytes）
[1/5] 组装安装负载 staging... 使用已有 dist\app 作为负载（AzurLaneDeskPet.exe 218,112 bytes）
                            staging 文件数：57
[2/5] 编译卸载器...          完成：<工程根>\dist\app\卸载-灵工桌宠.exe（66,048 bytes）
[3/5] 打包 payload.zip...    payload.zip 8,507,228 bytes，57 个条目
[4/5] 编译安装器...          完成：<工程根>\dist\灵工桌宠-安装程序.exe（8,569,344 bytes）
[5/5] 自检（dry-run，不写盘）... dry-run 退出码 = 0
产物检查：
  OK  <工程根>\dist\灵工桌宠-安装程序.exe  (8,569,344 bytes)
  OK  <工程根>\dist\app\卸载-灵工桌宠.exe  (66,048 bytes)
  OK  <工程根>\installer\payload.zip           (8,507,228 bytes)
  OK  <工程根>\installer\icon.ico               (26,097 bytes)
```

结论：**编译通过，两个 exe 均为独立单文件**（只依赖 .NET Framework 4.x 系统程序集，无需同目录 dll）。

### 6.2 验收测试（29 项断言）

```
> powershell -NoProfile -ExecutionPolicy Bypass -File <工程根>\installer\test-installer.ps1
通过 29 / 29，失败 0
```

| 用例 | 命令 | 关键校验 | 结果 |
| --- | --- | --- | --- |
| T1 | 文件系统检查 | 安装器 / 卸载器 exe 存在且为 PE（`MZ`） | PASS |
| T3 | `灵工桌宠-安装程序.exe /S /D=%TEMP%\alp-test --dry-run --no-launch --no-shortcuts --report=%TEMP%\alp-dryrun.txt` | 退出码 **0**，`%TEMP%\alp-test` **未创建**，报告含 `RESULT=OK(dry-run)` | PASS |
| T2 | `灵工桌宠-安装程序.exe /S /D=%TEMP%\alp-test --no-launch --no-shortcuts --report=%TEMP%\alp-install.txt` | 退出码 **0**；`AzurLaneDeskPet.exe`、`install.json`、`卸载-灵工桌宠.exe`、`使用说明.txt`、`assets\` 齐全；install.json 7 个必填字段全在；报告含 `exit=0` 且中文不乱码 | PASS |
| T2 | 注册表读取 | `HKCU\...\Uninstall\AzurLaneDeskPet` 的 `DisplayName=灵工桌宠`、`DisplayVersion=1.0.0`、`NoModify=1`、`NoRepair=1`、`UninstallString` 指向卸载器 | PASS |
| T4 | `%TEMP%\alp-test\卸载-灵工桌宠.exe /S --dir=%TEMP%\alp-test --purge-data --report=%TEMP%\alp-uninst.txt` | 退出码 **0**；安装目录、`%APPDATA%\AzurLaneDeskPet`、自定义 `dataDir`（`%TEMP%\alp-custom-azurlane-data`）全部消失；开始菜单 / 桌面 `.lnk` 消失；两个注册表键消失；报告含 `exit=0`、含删除统计；卸载器临时副本已自删除（`%TEMP%\AzurLaneDeskPet-uninstall-*.exe` 剩余 0） | PASS |
| T4 | 安全校验 | 另建的 `%TEMP%\alp-danger`（不含 AzurLane）**未被误删** | PASS |
| T5 | `Start-Process 安装器.exe` → `Start-Sleep 3` → `Stop-Process` | GUI 可启动、3 秒内保持运行（不阻塞、不自动退出）、可被强杀、无残留窗口 | PASS |

补充：把自定义 `dataDir` 换成一个「不含 AzurLane」的目录时，卸载器会在日志里打印
`[!] 自定义数据目录被安全校验拒绝，不会删除 …`，该目录保持原样。

### 6.3 图形模式补充验证

```
> powershell -NoProfile -ExecutionPolicy Bypass -File <工程根>\installer\gui-check.ps1
失败项：0
```

| 用例 | 校验 | 结果 |
| --- | --- | --- |
| G1 | 安装器 GUI 启动 3 秒仍在运行（`MainWindowTitle=灵工桌宠 安装程序  v1.0.0`），强杀后本测试产生的进程 0 残留 | PASS |
| G2 | 静默安装（**默认建快捷方式**）：开始菜单 + 桌面 `.lnk` 均创建，`TargetPath` / `WorkingDirectory` / `IconLocation` 正确，`install.json` 里 `shortcuts` 有记录 | PASS |
| G3 | 卸载器 GUI 启动：原进程在 4 秒内**已退出**，`%TEMP%` 下出现临时副本进程并停在确认弹窗；**未点「是」之前**安装目录与注册表项原封不动；强杀后无残留 | PASS |
| S | 收尾静默卸载：退出码 0，安装目录 / 快捷方式 / 注册表全部清除 | PASS |

### 6.4 真实负载端到端

```
> 安装: 灵工桌宠-安装程序.exe /S /D=%TEMP%\alp-real --no-launch --no-shortcuts --report=...
安装退出码=0   文件数=58  目录数=13
install.json: estimatedSizeKb=8775, dataDir=%APPDATA%\AzurLaneDeskPet, mainExe/uninstaller 路径正确
> 启动: %TEMP%\alp-real\AzurLaneDeskPet.exe
4 秒后仍在运行=True，窗口标题=[灵工桌宠 · v1.0.0]（托盘常驻，符合桌宠形态）
> 静默卸载清场：退出码 0，%TEMP%\alp-real 已删除
```

### 6.5 中文编码验证

* 源码 UTF-8 无 BOM + `csc /codepage:65001` 编译，编译日志无警告。
* 报告文件用 `Get-Content -Encoding UTF8` 读取，命中 `灵工桌宠` 字样（T2/T4 各一项断言）。
* 注册表 `DisplayName` 读回为「灵工桌宠」（非乱码）。
* GUI 截图逐像素核对：宋体金色标题「灵工巧物 天祈智临」、金框面板、按钮文字、日志中文全部正常显示。

### 6.6 安全重写后的回归（本次）

| 套件 | 命令 | 结果 |
| --- | --- | --- |
| 验收（原 29 项，新增数据目录标记与安全策略断言） | `installer\test-installer.ps1` | **通过 31 / 31** |
| 图形模式 | `installer\gui-check.ps1` | **失败项 0** |
| 安全模型 | `installer\test-security.ps1` | **通过 57 / 57** |

命令（项目路径含空格与中文，注意加引号）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<工程根>\installer\build-setup.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "<工程根>\installer\test-installer.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "<工程根>\installer\gui-check.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "<工程根>\installer\test-security.ps1"
```

> 三个脚本的项目根都默认取「脚本上一级目录」，所以项目文件夹改名/移动后无需修改脚本。
### 6.7 清理（历史记录）

测试结束后：临时目录（`alp-test` / `alp-gui-test` / `alp-real` / `alp-danger` / `alp-custom-azurlane-data`）、
报告文件、`AzurLaneDeskPet-payload-*` 解包目录、构建工作目录 `alp-build-*` 全部删除；
无残留进程、注册表项已清空。`installer\` 下只保留源码、脚本、`icon.ico`、`payload.zip`。

---

## 7. 构建/实现过程中的决策记录

1. **csc 与中文路径**：`csc.exe` 对命令行里的中文路径（如 `<工程根>\...`）不可靠，
   且 `/resource:` 后接 PowerShell 表达式拼接会被解析成多个参数。因此脚本统一把源码、图标、payload
   复制到 ASCII 临时目录 `%TEMP%\alp-build-<pid>` 编译，再把产物拷回目标中文路径；`/resource:` 参数
   先用变量拼好再传到 `&` 调用（脚本里的 `$resArg`）。
2. **PowerShell 脚本编码**：脚本含中文，Windows PowerShell 5.1 对**无 BOM** 的 UTF-8 会按 ANSI 解析导致中文乱码与解析错误，
   故 `build-setup.ps1` / `test-installer.ps1` / `gui-check.ps1` 一律保存为 **UTF-8 with BOM + CRLF**。
3. **不覆盖主代理产物**：`dist\app\AzurLaneDeskPet.exe` 只有在「不存在或不是有效 PE（≤4KB）」时才会写占位程序；
   `assets\` 与 `使用说明.txt` 同理，只在缺失时补。主代理交付真实主程序后，构建脚本自动改为打包真实负载（本记录已验证）。
4. **卸载器自搬迁用 `CreateProcess(DETACHED_PROCESS)`**：先用 `Process.Start(UseShellExecute=false)` 时，
   在部分环境下**原进程会滞留在内存里不退出**（实测：临时副本已起来，原进程仍存活且带窗口），
   导致安装目录里的卸载器文件被自己锁住而删不掉。改成 Win32 `CreateProcess`（`DETACHED_PROCESS |
   CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT`，不继承句柄）+ 350ms 让子进程站稳后返回 Main，
   原进程即正常退出，安装目录可被完整清空。
5. **删除策略**：先逐文件删（去只读属性）→ 由深到浅删空目录 → 仍不行就用 `SHFileOperation(FO_DELETE,
   NOCONFIRMATION|NOPROGRESS|NOERRORUI)` 强删；最终仍残留则写 `cmd` 延迟删除批处理（`rd /s /q` + 自删批处理），
   避免「卸载器删自己」的死锁。
6. **快捷方式**：主用 `WScript.Shell` COM（`Type.GetTypeFromProgID`），失败降级到调用
   `powershell.exe -File` 执行同一 COM 调用，两者都失败只记日志不阻断安装。
7. **`/D=` 参数解析**：兼容 Inno 风格（值后面可能跟裸路径片段）与本工具风格（`--dir="…"`）。
   扫描后续 argv 拼接含空格路径时，**遇到下一个以 `-`/`/` 开头的参数立即停止**——
   早期版本会把 `--no-launch --no-shortcuts --report=…` 一并吞进目录名，已修复（回归测试 T2 覆盖）。
8. **静默模式绝不弹窗**：静默分支不创建任何 Form，失败只写报告 + 日志；GUI 分支整体 `try/catch`，
   异常时弹「安装失败：<原因>，日志：<路径>」并返回退出码 3。
9. **临时文件清理与报告共存**：卸载器清理 `%TEMP%` 时会跳过「本次报告文件」与「本次日志」，
   避免把用户/自动化正在读取的报告删掉。
10. **`使用说明.txt`**：契约要求安装目录根下有该文件，故构建脚本在 `dist\app\使用说明.txt` 缺失时补写一份基础说明
    （启动 / 文件位置 / 卸载 / 静默卸载参数 / 常见问题），若主程序自带则以主程序的为准。

---

## 8. 安全模型（防误删 / 防穿透 / 防绕过）

> 背景：安全审查发现旧版卸载器存在两类高风险行为 —— (a) 安全校验拒绝后仍会写 `rd /s /q` 的延迟删除脚本，保护形同虚设；
> (b) 删除时用「整目录递归删除」，且会穿过 junction / 符号链接，可能清空用户把程序装进去的共用文件夹或外部素材库。
> 本节说明重写后的模型，对应实现见 `DeskPetSetup.cs` / `Uninstaller.cs`。

### 8.1 安装清单机制（卸载白名单）

安装时逐个记录**本次安装写下的每一个相对路径**（文件与目录），落到安装目录下的 `files.json`：

```json
{
  "product": "AzurLaneDeskPet",
  "version": "1.0.3",
  "installDir": "D:\\Tools\\AzurLaneDeskPet",
  "marker": ".azurlandeskpet-install",
  "preexisting": 12,
  "created": "2026-09-19T21:12:44",
  "files": ["AzurLaneDeskPet.exe", "卸载-灵工桌宠.exe", "使用说明.txt", "install.json",
            "assets\\builtin\\ui\\dialog-frame.png", "..."],
  "dirs":  ["assets", "assets\\builtin", "assets\\builtin\\ui", "..."]
}
```

* 路径一律**反斜杠、相对安装目录、不以 `\` 开头**；JSON 转义走安装器自带的 `Js()`（`"` `\` 控制字符 `\uXXXX`）。
* 同时写**标记文件** `.azurlandeskpet-install`（内容 `AzurLaneDeskPet install marker v<版本>`）。
* `install.json` 追加两个向后兼容字段：`"files": "files.json"`、`"marker": ".azurlandeskpet-install"`（老卸载器读到不认识也不报错）。
* 安装前目录**非空且没有标记**时判定为「共用文件夹」：图形模式弹提示（"安装不会动它们，卸载也只会删掉本产品自己的文件"），
  静默模式把 `preexisting` 条目数写进报告与 `files.json`。
* 安装目录本身是**重解析点**或位于系统关键目录/Windows 目录内 → 安装前直接拒绝（退出码 3，报告写明原因）。

### 8.2 卸载删除规则

只删「清单里的东西」，并且每条都要通过三重校验：

| 校验 | 规则 |
| --- | --- |
| 越界校验 | `Path.GetFullPath` 后必须严格位于安装目录之下（前缀 + `\` 边界比较），`..` 逃逸 / 绝对路径注入一律拒绝并记入「安全校验拒绝」 |
| 祖先重解析点校验 | 从安装目录到目标的**任一祖先部件**是重解析点 → 拒绝（防止清单里写 `linked\x.txt` 而 `linked` 是 junction 时删到外部数据） |
| 目标自身校验 | 目标本身是重解析点 → 拒绝（不跟随、不删除） |

* 文件：逐个删除（先清只读位）；被占用则进入「安全延迟删除」。
* 目录：**只删清单里列出且已空**的目录（`Directory.Delete(d, false)`，由深到浅）；安装目录本身也**只在已空时**才删。
* 目录非空 → 不删、写报告（列出保留条目名），**不再调用** `SHFileOperation` / `Directory.Delete(dir, true)`。
* `Walk()` 遇到重解析点直接跳过并报告，不递归进入。
* **保守模式**（没有 `files.json` 的旧安装）：只允许删顶层 `AzurLaneDeskPet.exe`、`卸载-灵工桌宠.exe`、`使用说明.txt`、
  `install.json`、`files.json`、`.azurlandeskpet-install`；`assets\` 仅当含安装标记 / `assets.json` / `builtin\ui\dialog-frame.png` /
  `builtin\素材清单.md` 时才按自家目录处理，内部同样逐条校验；其它一律保留并报告。

### 8.3 数据目录校验（`%APPDATA%\AzurLaneDeskPet` 或 `dataDir`）

* 删除前必须通过**身份校验**：存在 `.azurlandeskpet-data` 标记，**或** `config.json` 含 `configVersion` 字段，**或** 目录内含本产品 `files.json`。
  三者都没有 → **拒绝删除**，只报告路径并提示用户手动确认。
* 通过校验后也只删**已知条目**：`config.json`、`.azurlandeskpet-data`、`logs\`、`characters\`、本产品的 `files.json`；
  其它顶层条目保留并报告。同样逐文件删、跳过重解析点、最后只删空目录。

### 8.4 延迟删除规则（`%TEMP%\AzurLaneDeskPet-cleanup-*.cmd`）

* **只有**在「安全校验通过 + 存在 `files.json` 清单 + 残留项全部是清单内文件」时才写脚本；
  安全校验被拒绝、保守模式、安装目录是重解析点 → **一律不安排**（这是上一版最严重的漏洞）。
* 脚本内容只允许：逐条 `del /f /q "<具体文件>"`，最后对**空目录**用**不带 `/s`** 的 `rd "<目录>"`。
* **禁止**出现 `rd /s`、`del /s`、`rmdir /s`（回归用例 S3 会断言 `%TEMP%` 下不存在任何 `*-rmdir-*.cmd`）。
* 卸载器自身（`%TEMP%` 副本 + 安装目录内的副本）用单独的自删除脚本处理，同样是逐条 `del`。

### 8.5 报告与退出码

报告新增两节，把「没删的东西」和「为什么没删」列清楚：

```
---- 已保留（非本产品文件） ----
   · C:\Tools\AzurLaneDeskPet\我的资料.txt（非本产品文件，保留）
   · C:\Tools\AzurLaneDeskPet\other\（非本产品目录，保留）
---- 安全校验拒绝 ----
   ✗ C:\Tools\AzurLaneDeskPet\linked\x.txt（路径含重解析点（junction / 符号链接），已跳过不穿透）
```

退出码语义不变：**0 成功 / 2 用户取消 / 3 失败**；安全校验拒绝但清理流程正常完成仍算成功（退出码 0），
但报告与 GUI 结果弹窗会显著提示「有 N 项被保留 / 被拒绝，请人工确认」。

---

## 9. 安全修复测试记录

测试脚本：`installer\test-security.ps1`（57 项断言）。命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<工程根>\installer\build-setup.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "<工程根>\installer\test-security.ps1"
```

最终结果：**通过 57 / 57，失败 0**。（中途曾有 2 项失败，原因是主程序 src\ 的期望角色清单与 ssets\builtin\characters\ 实际素材不一致，主代理补齐后 S5 已转绿，详见 9.6。）

### 9.1 S1 共用文件夹不误删（17 项断言，全 PASS）

```powershell
# 造共用目录：%TEMP%\alp-shared（内含 我的资料.txt 与 other\keep.dat）
安装: 灵工桌宠-安装程序.exe /S /D=%TEMP%\alp-shared --no-launch --no-shortcuts --report=%TEMP%\alp-sec-s1-install.txt
       → exit=0；files.json / install.json / .azurlandeskpet-install 均生成；
         files.json 含 "preexisting": 2，路径形如 "assets\\builtin\\..."（反斜杠相对路径）
         安装后 我的资料.txt、other\keep.dat 仍在（安装阶段不动无关文件）
卸载: %TEMP%\alp-shared\卸载-灵工桌宠.exe /S --dir=%TEMP%\alp-shared --purge-data --report=%TEMP%\alp-sec-s1-uninstall.txt
       → exit=0
断言：产品文件（AzurLaneDeskPet.exe / install.json / files.json / .azurlandeskpet-install）全部删除 ✅
      我的资料.txt 仍在 ✅    other\keep.dat 仍在 ✅    %TEMP%\alp-shared 目录本身未被删除 ✅
      报告含「---- 已保留（非本产品文件） ----」并列出这两个文件 ✅
      %TEMP% 下未生成任何 AzurLaneDeskPet-cleanup-*.cmd ✅
```

### 9.2 S2 重解析点不穿透（13 项断言，全 PASS）

```powershell
# %TEMP%\alp-outside 放哨兵 外部素材.txt；安装到 %TEMP%\alp-junction 后
# mklink /J "%TEMP%\alp-junction\linked" "%TEMP%\alp-outside"
# 并人为把 "linked\\外部素材.txt" 注入 files.json（模拟最坏情况：清单点名了 junction 内的文件）
卸载: ...\卸载-灵工桌宠.exe /S --dir=%TEMP%\alp-junction --purge-data --report=%TEMP%\alp-sec-s2-uninstall.txt
       → exit=0
断言：外部哨兵 外部素材.txt 仍在 ✅    %TEMP%\alp-outside 仍在 ✅    junction 本身未被删除 ✅
      报告写明「跳过重解析点（不跟随、不删除）」「…已跳过不穿透」「共跳过 N 个重解析点相关条目」✅
      「---- 安全校验拒绝 ----」小节非空 ✅    安装目录内产品文件已删除 ✅
清理：rmdir "%TEMP%\alp-junction\linked"（不带 /s）→ junction 摘除，外部目录内容不受影响 ✅
```

### 9.3 S3 保护不可绕过（9 项断言，全 PASS）

```powershell
# 在 %TEMP% 根放诱饵 AzurLaneDeskPet.exe + 伪造 install.json / files.json，然后直接对 %TEMP% 卸载
卸载: <dist\app\卸载-灵工桌宠.exe> /S --dir=%TEMP% --purge-data --report=%TEMP%\alp-sec-s3-uninstall.txt
       → exit=0（拒绝但流程正常结束）
断言：报告明确写出「安全校验拒绝」，且小节内有 ✗ 条目 ✅
      %TEMP% 下未生成任何 AzurLaneDeskPet-cleanup-*.cmd（延迟删除被禁止）✅
      %TEMP% 下未生成任何 AzurLaneDeskPet-rmdir-*.cmd（旧版漏洞回归）✅
      被保护目录内容未变（诱饵 exe 仍在）✅   用户数据目录未被删除 ✅
安装器侧：mklink /J 的目标作为 /D= → exit=3，报告写明「该目录是重解析点」✅
```

### 9.4 S3c 数据目录身份校验（2 项断言，全 PASS）

```powershell
# 伪造 %TEMP%\alp-data-nomarker（只有 config.json {"volume":0.5}，无标记、无 configVersion）
# 把 install.json 的 dataDir 指向它后卸载
断言：该目录未被删除、仍存在 ✅    报告写明「数据目录身份校验未通过」✅
```

### 9.5 S4 正常路径回归（11 项断言，全 PASS）

```powershell
安装: /S /D=%TEMP%\alp-real --no-launch --no-shortcuts      → exit=0，files.json "preexisting": 0
数据: 手工造 %APPDATA%\AzurLaneDeskPet（.azurlandeskpet-data + config.json 带 configVersion + logs\pet.log + 无关文件 我的笔记.md）
快捷方式: 真实创建开始菜单 / 桌面 .lnk
卸载: %TEMP%\alp-real\卸载-灵工桌宠.exe /S --dir=%TEMP%\alp-real --purge-data --report=%TEMP%\alp-sec-s4-uninstall.txt
      → exit=0，报告 CLEAN=OK、未能删除 0 项
断言：安装目录已删除 ✅    开始菜单 / 桌面快捷方式已删除 ✅    注册表卸载项已删除 ✅
      数据目录的已知条目（logs / config.json / .azurlandeskpet-data）已清除 ✅
      数据目录内无关文件 我的笔记.md 被保留并在报告中列出 ✅    卸载器临时副本已自删 ✅
```

### 9.6 S5 主程序自检（`--selftest`）——全 PASS

```powershell
安装到 %TEMP%\alp-st → <安装目录>\AzurLaneDeskPet.exe --selftest --report=%TEMP%\alp-st.txt
       → exit=0，报告结论行「结论：全部通过 ✔」
之后再静默卸载清场 → 退出码 0，%TEMP%\alp-st 已删除
```

* 安装器侧断言（安装 exit=0 / 自检可执行 / 报告生成 / 卸载清场）与主程序自检断言**全部通过**。
* 过程中的一段插曲（记录在案，供后续排查）：早期主程序自检曾报
  `[FAIL] 模板「chuyue」/「prinz_eugen」台词文件 → 0 条`（`assets\builtin\lines\` 当时缺这两个角色的台词文件）
  与 `[FAIL] 内置角色就是这 5 位 → 实际 7 个`（`src\` 期望清单与实际素材不一致）。
  这些都属主程序 `src` / `assets` 两侧的内容问题：**本安装器只是忠实打包 `dist\app`（= `assets\builtin\**` 的镜像），不缺件**，
  可对照 `dist\app\assets\builtin` 与 `assets\builtin` 校验。并行任务的素材补齐后，S5 已转为「全部通过」。
### 9.7 S6 清理

```powershell
断言：%TEMP% 下无 alp-*/AzurLane* 残留 ✅（含 alp-shared / alp-outside / alp-junction / alp-junction2 /
      alp-junction2-target / alp-data-nomarker / alp-real / alp-st / alp-sec-*.txt / AzurLaneDeskPet-payload-* /
      AzurLaneDeskPet-cleanup-*.cmd）
      junction 全部用 rmdir（不带 /s）摘除，外部目录内容未受影响
```

### 9.8 回归：原有 29 项验收 + 图形模式验证

安全重写后重跑原有脚本（`test-installer.ps1` / `gui-check.ps1`）确认没有回归，见第 6 节记录。

---

## 10. 安全修复的决策记录（补充）

1. **SafetyCheck 的口径调整**：旧版把「`%TEMP%` / `%APPDATA%` 等关键目录的直接子目录」一律判为危险，
   导致 `%TEMP%\xxx`、`%USERPROFILE%\Desktop` 这类合法安装位置被拒（表现为卸载「什么都没删」）。
   新版只保护**关键目录本身**、`C:\Windows`（整棵）、`C:\Users` / `C:\ProgramData` 的直接子目录；
   子目录是否属于本产品，改由「安装标记 + `files.json` 清单 + 逐条越界/重解析点校验」判断 —— 校验更精确，且不放过真正的危险路径。
2. **防穿透不能只查目标本身**：`File.Exists()` / `Directory.Delete()` 都会跟随 junction，
   因此必须检查**从安装目录到目标的每一个祖先部件**（`HasReparseAncestor`），否则清单里一条 `linked\x.txt` 就能删到外部数据。
3. **`%TEMP%` 只清理确定属于自己的东西**：旧版按 `alp-*` 通配整树删，会误删用户/测试的同名目录（安全测试中当场抓到）。
   新版只删 `AzurLaneDeskPet-*` 文件与 `AzurLaneDeskPet-payload-*` 目录；`alp-*` 仅在**已空**时才删。
4. **卸载器自身的删除**：安装目录内的卸载器副本是运行中的映像，Windows 上必然删不掉。
   旧实现因此在安装目录残留 `卸载-灵工桌宠.exe`，并把它当作「非本产品文件」保留下来，导致目录永远删不掉。
   新版改为：尝试直删 → 失败则用退出后自删除脚本逐条 `del`，并在删除空目录之前处理。
5. **计数与报告一致性**：删除计数只在真正删掉时累加；「已保留」在安装目录与数据目录两条路径上都会汇总，
   避免出现「什么都没删但报告说删了 118 个文件」这类误导。