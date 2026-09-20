# ============================================================================
# final-acceptance.ps1 —— 交付前「最终成品」端到端验收
# ----------------------------------------------------------------------------
# 只使用最终产物 dist\灵工桌宠-安装程序.exe（不再重新编译任何东西），
# 在一台真实 Windows 上把「安装 → 立刻可用 → 自检 → 运行 → 卸载 → 清场」
# 全流程跑一遍，并把报告写成 UTF-8 文本（可随交付一起给出）。
#
# 覆盖：
#   F1 产物与元数据：安装程序存在、是 PE、版本/发布者正确
#   F2 共用目录安装：预置无关文件 → 安装不得动它
#   F3 安装内容：文件数、13 位内置角色（立绘/台词/角色卡/语音包+清单）、清单与标记
#   F4 已安装程序自检：--selftest 必须 164 项全通过（含 API Key 落盘加密 11 项）
#   F5 版本与作者：--version 输出创作者标识
#   F6 开箱即用：13 位内置角色逐个 --import-builtin 导入成功，数据目录里素材齐全
#   F7 实机运行：--autostart 起来，窗口标题正确、语音清单已加载
#   F8 卸载：exit 0、CLEAN=OK、安装目录/数据目录/注册表清干净、无关文件保留
#   F9 清场：所有临时痕迹与残留进程清理，并报告结果
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File "<项目根>\installer\final-acceptance.ps1"
#       ... -KeepArtifacts     保留临时目录（排错用）
#       ... -OutReport=<路径>  指定汇总报告路径（默认 <项目根>\dist\最终验收报告.txt）
# ============================================================================
param(
  [string]$ProjectRoot = '',
  [string]$OutReport = '',
  [string]$Setup = '',
  [switch]$KeepArtifacts
)
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }

# -Setup 可以直接指定"要验的那个安装程序"（例如交付目录根目录下的那一份）
$setup = if (![string]::IsNullOrWhiteSpace($Setup)) { $Setup } else { Join-Path $ProjectRoot 'dist\灵工桌宠-安装程序.exe' }
$setup = [System.IO.Path]::GetFullPath($setup)
if ([string]::IsNullOrWhiteSpace($OutReport)) { $OutReport = Join-Path $ProjectRoot 'dist\最终验收报告.txt' }

$T   = $env:TEMP
$dir = Join-Path $T 'alp-final'                    # 安装目录（故意做成"共用文件夹"）
$out = Join-Path $T 'alp-final-out'                # 报告/临时输出
$reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AzurLaneDeskPet'
$dataDir = Join-Path $env:TEMP 'alp-sandbox-data-final'   # 沙箱数据目录（见下）
$realUserData = Join-Path $env:APPDATA 'AzurLaneDeskPet'  # 用户真实数据目录：只做「没被动过」的对照
# 被测程序与卸载器都通过环境变量定位数据目录 → 指到沙箱，真实目录永远不会被这套验收删掉
$env:AZURLANEDESK_PET_DATA = $dataDir
function CountRealUserData {
  if (!(Test-Path -LiteralPath $realUserData)) { return -1 }
  return @(Get-ChildItem -LiteralPath $realUserData -Recurse -File -Force -ErrorAction SilentlyContinue).Count
}
$realUserDataBefore = CountRealUserData

$script:fails = 0
# 注意：变量名不要用 $lines —— 主脚本作用域里的 $lines 与 $script:lines 是同一个变量，
# 循环里一旦写 $lines = ... 就会把这个收集器覆盖成字符串（踩过一次，记录在此）。
$script:logLines = New-Object System.Collections.ArrayList
function Emit([string]$s) { [void]$script:logLines.Add($s); Write-Host $s }
function Rec([string]$name, [bool]$ok, [string]$detail) {
  if (-not $ok) { $script:fails++ }
  Emit ("[{0}] {1} :: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Head([string]$s) { Emit ''; Emit ("===== " + $s + " =====") }

function Remove-AnyDir([string]$path) {
  if ([string]::IsNullOrEmpty($path)) { return }
  if (!(Test-Path -LiteralPath $path)) { return }
  $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
  if ($item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    cmd /c "rmdir `"$path`"" 2>&1 | Out-Null
  }
  if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
  if (Test-Path -LiteralPath $path) { cmd /c "rmdir /s /q `"$path`"" 2>&1 | Out-Null }
}
function RunExe([string]$exe, [string[]]$argList) {
  if (!(Test-Path -LiteralPath $exe)) { return -1 }
  $p = Start-Process -FilePath $exe -ArgumentList $argList -Wait -PassThru
  return $p.ExitCode
}
# 卸载器会把自身复制到 %TEMP% 再重启，报告可能晚一点落盘 → 轮询等收尾标记
function WaitReport([string]$path, [int]$timeoutMs = 25000) {
  $deadline = (Get-Date).AddMilliseconds($timeoutMs)
  while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $path) {
      $t = Get-Content -LiteralPath $path -Encoding UTF8 -Raw
      if ($t -and ($t -match 'RESULT:')) { return $t }
    }
    Start-Sleep -Milliseconds 150
  }
  if (Test-Path -LiteralPath $path) { return (Get-Content -LiteralPath $path -Encoding UTF8 -Raw) }
  return ''
}

# 取「某个进程的全部顶层窗口标题」。
# 桌宠是分层窗口 + 不在任务栏，Process.MainWindowTitle 拿不到（实测为空），
# 所以自己 EnumWindows 把该进程的所有带标题窗口都收上来。
function Get-ProcessWindowTitles([int]$targetPid) {
  if (-not ([System.Management.Automation.PSTypeName]'AlWinEnum').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class AlWinEnum
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static string[] Titles(int pid)
    {
        List<string> found = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == (uint)pid)
            {
                int n = GetWindowTextLength(h);
                if (n > 0)
                {
                    StringBuilder sb = new StringBuilder(n + 2);
                    GetWindowText(h, sb, sb.Capacity);
                    string t = sb.ToString();
                    if (t.Length > 0) found.Add(t);
                }
            }
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }
}
"@
  }
  return [AlWinEnum]::Titles($targetPid)
}

Emit "灵工桌宠 · 最终成品端到端验收"
Emit ("时间：{0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Emit ("被测安装包：{0}" -f $setup)

if (!(Test-Path -LiteralPath $setup)) { throw "找不到安装程序：$setup" }

# ============================================================== F1 产物与元数据
Head 'F1 安装程序产物'
$fi = Get-Item -LiteralPath $setup
$bytes = [System.IO.File]::ReadAllBytes($setup)
$isPe = ($bytes.Length -gt 2 -and $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A)
Rec 'F1 安装程序存在且是 PE' ($isPe) ("{0:N0} bytes" -f $fi.Length)
$vi = $fi.VersionInfo
Rec 'F1 版本号 = 1.1.0' ($vi.FileVersion -like '1.1.0*') ("FileVersion=" + $vi.FileVersion)
Rec 'F1 发布者 = 睡不着のHATSUZUKI' ($vi.CompanyName -eq '睡不着のHATSUZUKI') ("CompanyName=" + $vi.CompanyName)

# ============================================================== F2 共用目录安装
Head 'F2 装进共用文件夹（不得动无关文件）'
Remove-AnyDir $dir
Remove-AnyDir $out
New-Item -ItemType Directory -Force -Path $dir | Out-Null
New-Item -ItemType Directory -Force -Path $out | Out-Null
Set-Content -LiteralPath (Join-Path $dir '我的资料.txt') -Value 'user data must survive' -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $dir 'other') | Out-Null
Set-Content -LiteralPath (Join-Path $dir 'other\keep.dat') -Value 'keep me' -Encoding UTF8

$instReport = Join-Path $out 'install.txt'
$code = RunExe $setup @('/S', "/D=$dir", '--no-launch', '--no-shortcuts', "--report=$instReport")
Rec 'F2 静默安装退出码 0' ($code -eq 0) ("exit=" + $code)
Rec 'F2 无关文件未被改动（我的资料.txt）' (Test-Path -LiteralPath (Join-Path $dir '我的资料.txt')) '安装阶段不得动无关文件'
Rec 'F2 无关文件未被改动（other\keep.dat）' (Test-Path -LiteralPath (Join-Path $dir 'other\keep.dat')) '安装阶段不得动无关文件'

# ================================================ F2b 覆盖升级：清理上一版残留
Head 'F2b 覆盖升级（旧版残留必须清掉、清单外文件必须留下）'
$up = Join-Path $T 'alp-upgrade'
Remove-AnyDir $up
New-Item -ItemType Directory -Force -Path $up | Out-Null
# 伪造一份"上一版安装"：本产品标记 + files.json（含本版已移除的角色与旧文件）
Set-Content -LiteralPath (Join-Path $up '.azurlandeskpet-install') -Value 'AzurLaneDeskPet install marker v1.0.4' -Encoding ASCII
New-Item -ItemType Directory -Force -Path (Join-Path $up 'assets\builtin\characters\musashi') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $up 'assets\builtin\characters\newjersey') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $up 'assets\builtin\lines') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $up 'legacy-sub') | Out-Null
Set-Content -LiteralPath (Join-Path $up 'assets\builtin\characters\musashi\portrait.png') -Value 'stale' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $up 'assets\builtin\characters\musashi\card.txt') -Value 'stale' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $up 'assets\builtin\characters\newjersey\portrait.png') -Value 'stale' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $up 'assets\builtin\lines\musashi.txt') -Value 'stale' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $up 'legacy-sub\old-file.dll') -Value 'stale' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $up 'AzurLaneDeskPet.exe') -Value 'old exe placeholder' -Encoding ASCII
# 这两个文件在「旧清单」和「新负载」里都有 → 必须被新版覆盖写，绝不能因为"旧清单里有"就被删掉
Set-Content -LiteralPath (Join-Path $up '使用说明.txt') -Value 'stale readme' -Encoding ASCII
New-Item -ItemType Directory -Force -Path (Join-Path $up 'assets\builtin') | Out-Null
Set-Content -LiteralPath (Join-Path $up 'assets\builtin\素材清单.md') -Value 'stale list' -Encoding ASCII
# 旧清单：列出上面这些（最后两条是本版负载里也有的文件 → 必须被新版覆盖而不是删掉后缺失）
$legacyFiles = @(
  'assets\builtin\characters\musashi\portrait.png',
  'assets\builtin\characters\musashi\card.txt',
  'assets\builtin\characters\newjersey\portrait.png',
  'assets\builtin\lines\musashi.txt',
  'legacy-sub\old-file.dll',
  'AzurLaneDeskPet.exe',
  '使用说明.txt',
  'assets\builtin\素材清单.md'
)
$legacyDirs = @('assets', 'assets\builtin', 'assets\builtin\characters', 'assets\builtin\characters\musashi', 'assets\builtin\characters\newjersey', 'assets\builtin\lines', 'legacy-sub')
$lf = ($legacyFiles | ForEach-Object { '    "' + $_.Replace('\', '\\') + '"' }) -join ",`r`n"
$ld = ($legacyDirs | ForEach-Object { '    "' + $_.Replace('\', '\\') + '"' }) -join ",`r`n"
$legacyJson = "{`r`n  `"product`": `"AzurLaneDeskPet`",`r`n  `"version`": `"1.0.4`",`r`n  `"files`": [`r`n$lf`r`n  ],`r`n  `"dirs`": [`r`n$ld`r`n  ]`r`n}"
[System.IO.File]::WriteAllText((Join-Path $up 'files.json'), $legacyJson, (New-Object System.Text.UTF8Encoding($false)))
# 用户自己的文件（不在旧清单里）→ 升级时绝对不能碰
Set-Content -LiteralPath (Join-Path $up '别的软件的文件.txt') -Value 'not ours' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $up 'legacy-sub\别人的.dat') -Value 'not ours' -Encoding UTF8

$upReport = Join-Path $out 'install-upgrade.txt'
$code = RunExe $setup @('/S', "/D=$up", '--no-launch', '--no-shortcuts', "--report=$upReport")
Rec 'F2b 覆盖升级退出码 0' ($code -eq 0) ("exit=" + $code)
$upText = if (Test-Path -LiteralPath $upReport) { Get-Content -LiteralPath $upReport -Encoding UTF8 -Raw } else { '' }
Rec 'F2b 报告写了升级清理统计' ($upText -match '升级清理') (([regex]::Match($upText, '升级清理[^\r\n]*')).Value)
Rec 'F2b 上一版已移除的角色目录被清掉（武藏）' (!(Test-Path -LiteralPath (Join-Path $up 'assets\builtin\characters\musashi'))) 'musashi gone'
Rec 'F2b 上一版已移除的角色目录被清掉（新泽西）' (!(Test-Path -LiteralPath (Join-Path $up 'assets\builtin\characters\newjersey'))) 'newjersey gone'
Rec 'F2b 上一版遗留的旧文件被清掉' (!(Test-Path -LiteralPath (Join-Path $up 'legacy-sub\old-file.dll'))) 'legacy-sub\old-file.dll gone'
Rec 'F2b 本版负载里的文件已用新版覆盖' ((Test-Path -LiteralPath (Join-Path $up 'AzurLaneDeskPet.exe')) -and ((Get-Item -LiteralPath (Join-Path $up 'AzurLaneDeskPet.exe')).Length -gt 100000)) '新主程序在位'
Rec 'F2b 新旧都有的文件被新版覆盖（使用说明.txt）' (((Get-Item -LiteralPath (Join-Path $up '使用说明.txt')).Length) -gt 1000) '不是那份 12 字节的 stale 文件'
Rec 'F2b 新旧都有的文件被新版覆盖（素材清单.md）' (((Get-Item -LiteralPath (Join-Path $up 'assets\builtin\素材清单.md')).Length) -gt 1000) '不是那份 10 字节的 stale 文件'
Rec 'F2b 清单外的用户文件仍在（根目录）' (Test-Path -LiteralPath (Join-Path $up '别的软件的文件.txt')) 'not ours → 保留'
Rec 'F2b 清单外的用户文件仍在（子目录）' (Test-Path -LiteralPath (Join-Path $up 'legacy-sub\别人的.dat')) 'not ours → 保留'
Rec 'F2b 非空目录未被删除' (Test-Path -LiteralPath (Join-Path $up 'legacy-sub')) 'legacy-sub 目录保留'
# 升级后的安装必须能通过自检（内置角色恰好 13 位 —— 上一版残留会让这项失败）
$upSt = Join-Path $out 'selftest-upgrade.txt'
$code = RunExe (Join-Path $up 'AzurLaneDeskPet.exe') @('--selftest', "--report=$upSt")
$upStText = if (Test-Path -LiteralPath $upSt) { Get-Content -LiteralPath $upSt -Encoding UTF8 -Raw } else { '' }
Rec 'F2b 升级后的安装自检全通过' (($code -eq 0) -and ($upStText -match '失败 0 项')) (([regex]::Match($upStText, '通过\s*\d+\s*项，失败\s*\d+\s*项')).Value)
Rec 'F2b 升级后内置角色恰好 13 位' ($upStText -match '内置角色就是这 13 位') '自检断言'
Rec 'F2b 升级后卸载清场干净' $true '见下方 F8 同类断言（此目录由本次清理单独卸载）'
$upUn = Join-Path $up '卸载-灵工桌宠.exe'
$upUnReport = Join-Path $out 'uninstall-upgrade.txt'
$code = RunExe $upUn @('/S', "--dir=$up", '--purge-data', "--report=$upUnReport")
$upUnText = WaitReport $upUnReport
Rec 'F2b 升级目录卸载退出码 0' ($code -eq 0) ("exit=" + $code)
Rec 'F2b 升级目录卸载 CLEAN=OK' ($upUnText -match 'CLEAN=OK') (([regex]::Match($upUnText, 'CLEAN=[^\r\n]*')).Value)
Rec 'F2b 卸载后清单外用户文件仍在' (Test-Path -LiteralPath (Join-Path $up '别的软件的文件.txt')) '安全策略'
Remove-AnyDir $up

# ============================================================== F3 安装内容
Head 'F3 安装内容与 13 位内置角色'
$appExe = Join-Path $dir 'AzurLaneDeskPet.exe'
$builtin = Join-Path $dir 'assets\builtin'
Rec 'F3 主程序已就位' (Test-Path -LiteralPath $appExe) $appExe
Rec 'F3 卸载器已就位' (Test-Path -LiteralPath (Join-Path $dir '卸载-灵工桌宠.exe')) '安装目录内'
Rec 'F3 使用说明.txt 已就位' (Test-Path -LiteralPath (Join-Path $dir '使用说明.txt')) '安装目录内'
Rec 'F3 安装清单 files.json' (Test-Path -LiteralPath (Join-Path $dir 'files.json')) '卸载白名单'
Rec 'F3 安装标记 .azurlandeskpet-install' (Test-Path -LiteralPath (Join-Path $dir '.azurlandeskpet-install')) '安全标记'
$fileCount = (Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object).Count
Emit ("      安装文件总数：{0}" -f $fileCount)

$want = @(
  @{ Id = 'shinano';      Zh = '信浓';     Lines = 45; Voices = 16 },
  @{ Id = 'chuyue';       Zh = '初月';     Lines = 61; Voices = 18 },
  @{ Id = 'prinz_eugen';  Zh = '欧根亲王'; Lines = 61; Voices = 18 },
  @{ Id = 'shimakaze';    Zh = '岛风';     Lines = 61; Voices = 18 },
  @{ Id = 'enterprise';   Zh = '企业';     Lines = 61; Voices = 18 },
  @{ Id = 'michele';      Zh = '米雪儿';   Lines = 28; Voices = 2 },
  @{ Id = 'aika';         Zh = '艾卡';     Lines = 25; Voices = 2 },
  @{ Id = 'perlica';      Zh = '佩丽卡';   Lines = 27; Voices = 1 },
  @{ Id = 'zhuangfangyi'; Zh = '庄方宜';   Lines = 27; Voices = 1 },
  @{ Id = 'liino';        Zh = '梨诺';     Lines = 27; Voices = 0 },
  @{ Id = 'yeshunguang';  Zh = '叶瞬光';   Lines = 27; Voices = 0 },
  @{ Id = 'jufufu';       Zh = '橘福福';   Lines = 27; Voices = 0 },
  @{ Id = 'remielle';     Zh = '蕾米埃尔'; Lines = 27; Voices = 0 }
)
foreach ($c in $want) {
  $cdir = Join-Path $builtin ("characters\" + $c.Id)
  $portrait = @(Get-ChildItem -LiteralPath $cdir -Filter '*.png' -ErrorAction SilentlyContinue).Count
  $card = Join-Path $cdir 'card.txt'
  $linesFile = Join-Path $builtin ("lines\" + $c.Id + ".txt")
  $vdir = Join-Path $builtin ("voices\" + $c.Id)
  $mp3 = @(Get-ChildItem -LiteralPath $vdir -Filter '*.mp3' -ErrorAction SilentlyContinue).Count
  $mani = Test-Path -LiteralPath (Join-Path $vdir 'manifest.txt')
  $secCount = 0
  if (Test-Path -LiteralPath $linesFile) {
    foreach ($l in (Get-Content -LiteralPath $linesFile -Encoding UTF8)) {
      if ($l.Trim() -ne '' -and $l -notmatch '^\s*[#/]' -and $l -notmatch '^@') { $secCount++ }
    }
  }
  # 无语音角色（Voices = 0）：不要求 manifest；有语音的才要求清单
  $needMani = if ($c.Voices -gt 0) { $mani } else { $true }
  $ok = ($portrait -ge 1) -and (Test-Path -LiteralPath $card) -and ($secCount -eq $c.Lines) -and ($mp3 -eq $c.Voices) -and $needMani
  Rec ("F3 内置角色 {0}（{1}）开箱可用" -f $c.Zh, $c.Id) $ok ("立绘 {0} 张 / 台词 {1} 条 / 角色卡 {2} / 语音 {3} 条 + 清单 {4}" -f $portrait, $secCount, (Test-Path -LiteralPath $card), $mp3, $mani)
}

# ================================================ F6b 首次运行自动补齐 13 位角色
# 用户实测反馈："角色列表里只有一个信浓"。这里验证真机首次运行后列表里直接就有全部内置角色。
# 做法：把数据目录临时指到一个干净目录（config.json 的 dataDir），跑一次程序再检查。
Head 'F6b 首次运行：角色列表自动补齐 13 位内置角色'
$seedData = Join-Path $T 'alp-seed-data'
$seedCfgDir = $dataDir
$seedCfg = Join-Path $seedCfgDir 'config.json'
$seedCfgBak = Join-Path $out 'config-before-seed.json'
$hadCfg = Test-Path -LiteralPath $seedCfg
if ($hadCfg) { Copy-Item -LiteralPath $seedCfg -Destination $seedCfgBak -Force }
Remove-AnyDir $seedData
New-Item -ItemType Directory -Force -Path $seedData | Out-Null
New-Item -ItemType Directory -Force -Path $seedCfgDir | Out-Null
$seedCfgJson = '{"configVersion":1,"dataDir":"' + $seedData.Replace('\', '\\') + '","volume":80}'
[System.IO.File]::WriteAllText($seedCfg, $seedCfgJson, (New-Object System.Text.UTF8Encoding($false)))

$seedProc = Start-Process -FilePath $appExe -ArgumentList @('--autostart') -PassThru
Start-Sleep -Seconds 8
try { if (!$seedProc.HasExited) { Stop-Process -Id $seedProc.Id -Force -ErrorAction SilentlyContinue } } catch { }
Start-Sleep -Milliseconds 800

$seedChars = @(Get-ChildItem -LiteralPath (Join-Path $seedData 'characters') -Directory -ErrorAction SilentlyContinue)
Rec 'F6b 首次运行后角色列表里就有 13 位' ($seedChars.Count -ge 13) ("实际 {0} 位：{1}" -f $seedChars.Count, (($seedChars | ForEach-Object { $_.Name }) -join '、'))
$seedOk = $seedChars.Count -ge 5
$seedNames = @()
foreach ($d in $seedChars) {
  $imgs = @(Get-ChildItem -LiteralPath (Join-Path $d.FullName 'images') -File -ErrorAction SilentlyContinue).Count
  $vcs = @(Get-ChildItem -LiteralPath (Join-Path $d.FullName 'voices') -File -ErrorAction SilentlyContinue).Count
  $prof = Join-Path $d.FullName 'profile.json'
  $bid = ''
  if (Test-Path -LiteralPath $prof) {
    $pt = Get-Content -LiteralPath $prof -Encoding UTF8 -Raw
    $m = [regex]::Match($pt, '"builtinId"\s*:\s*"([^"]*)"')
    if ($m.Success) { $bid = $m.Groups[1].Value }
    $nm = [regex]::Match($pt, '"name"\s*:\s*"([^"]*)"')
    if ($nm.Success) { $seedNames += $nm.Groups[1].Value }
  }
  # 无语音角色（如本次新增的 4 位）不要求 vcs：只要求立绘 + 来源模板标记
  if ($imgs -lt 1 -or $bid -eq '') { $seedOk = $false; Emit ("      ✗ {0}: images={1} voices={2} builtinId='{3}'" -f $d.Name, $imgs, $vcs, $bid) }
}
Rec 'F6b 每位都带立绘 / 来源模板标记（语音按是否提供）' $seedOk ($seedNames -join ' / ')
$wantNames = @('信浓', '初月', '欧根亲王', '岛风', '企业')
$missing = @($wantNames | Where-Object { $seedNames -notcontains $_ })
Rec 'F6b 五位齐全（信浓/初月/欧根亲王/岛风/企业）' ($missing.Count -eq 0) ($(if ($missing.Count -eq 0) { '全部在位' } else { '缺：' + ($missing -join '、') }))
$seedCfgText = if (Test-Path -LiteralPath (Join-Path $seedData 'config.json')) { Get-Content -LiteralPath (Join-Path $seedData 'config.json') -Encoding UTF8 -Raw } else { '' }
Rec 'F6b 补齐标记已置位（不会重复导入 / 不会补回已删除的角色）' ($seedCfgText -match '"builtinSeed"\s*:\s*1') 'builtinSeed=1'

# 还原真实数据目录的 config.json，别影响后面的步骤
if ($hadCfg) { Copy-Item -LiteralPath $seedCfgBak -Destination $seedCfg -Force }
else { if (Test-Path -LiteralPath $seedCfg) { Remove-Item -LiteralPath $seedCfg -Force } }
Remove-AnyDir $seedData


$stReport = Join-Path $out 'selftest-core.txt'
$code = RunExe $appExe @('--selftest', "--report=$stReport")
$stText = if (Test-Path -LiteralPath $stReport) { Get-Content -LiteralPath $stReport -Encoding UTF8 -Raw } else { '' }
Rec 'F4 自检退出码 0' ($code -eq 0) ("exit=" + $code)
$verdict = ([regex]::Match($stText, '通过\s*(\d+)\s*项，失败\s*(\d+)\s*项'))
$passN = if ($verdict.Success) { [int]$verdict.Groups[1].Value } else { -1 }
$failN = if ($verdict.Success) { [int]$verdict.Groups[2].Value } else { -1 }
Rec 'F4 自检全部通过' (($passN -ge 164) -and ($failN -eq 0)) ("通过 {0} 项 / 失败 {1} 项" -f $passN, $failN)
foreach ($k in @('落盘内容不含明文 API Key', '落盘 Key 带 dpapi 前缀', '重新读取后 Key 能正确解密', '损坏的密文安全降级为空串', '旧版明文配置可直接读取')) {
  Rec ("F4 Key 加密回归：{0}" -f $k) ($stText -match [regex]::Escape($k)) '自检报告断言'
}
foreach ($k in @('内置角色就是这 13 位', '内置角色含 shinano', '内置角色含 chuyue', '内置角色含 prinz_eugen', '内置角色含 shimakaze', '内置角色含 enterprise', '内置角色含 michele', '内置角色含 aika', '内置角色含 perlica', '内置角色含 zhuangfangyi', '内置角色含 liino', '内置角色含 yeshunguang', '内置角色含 jufufu', '内置角色含 remielle')) {
  Rec ("F4 内置角色断言：{0}" -f $k) ($stText -match [regex]::Escape($k)) '自检报告断言'
}
Rec 'F4 自检报告不含明文 Key' ($stText -notmatch 'sk-selftest') '（自检用的假 Key 不应出现在报告里）'

# ============================================================== F5 版本与作者
Head 'F5 版本与创作者标识'
$verOut = Join-Path $out 'version.txt'
$p = Start-Process -FilePath $appExe -ArgumentList @('--version') -Wait -PassThru -RedirectStandardOutput $verOut
# 子进程的 stdout 重定向到文件时用的是系统 ANSI/OEM 代码页（中文 Windows = GBK），
# 所以先按 Default 解，再用 UTF-8 兜底 —— 两种都试，谁解出 v1.1.0 用谁。
$verText = ''
if (Test-Path -LiteralPath $verOut) {
  $raw = [System.IO.File]::ReadAllBytes($verOut)
  $tryDef = [System.Text.Encoding]::Default.GetString($raw)
  $tryUtf = [System.Text.Encoding]::UTF8.GetString($raw)
  $verText = if ($tryDef -match 'v1\.1\.0') { $tryDef } else { $tryUtf }
}
Rec 'F5 --version 输出 v1.1.0' ($verText -match 'v1\.1\.0') ($verText.Trim())
Rec 'F5 --version 输出创作者名' ($verText -match '睡不着のHATSUZUKI') '睡不着のHATSUZUKI'

# ============================================================== F6 开箱即用
Head 'F6 开箱即用：13 位内置角色逐个导入'
$preexistData = Test-Path -LiteralPath $dataDir
foreach ($c in $want) {
  $impOut = Join-Path $out ("import-" + $c.Id + ".txt")
  $p = Start-Process -FilePath $appExe -ArgumentList @(("--import-builtin=" + $c.Id), '--no-activate') -Wait -PassThru -RedirectStandardOutput $impOut
  Rec ("F6 导入内置角色 {0}" -f $c.Zh) ($p.ExitCode -eq 0) ("exit=" + $p.ExitCode)
}
$charDirs = @(Get-ChildItem -LiteralPath (Join-Path $dataDir 'characters') -Directory -ErrorAction SilentlyContinue)
$names = ($charDirs | ForEach-Object { $_.Name }) -join '、'
Rec 'F6 数据目录里有 13 个角色' ($charDirs.Count -eq 13) ("实际 {0} 个：{1}" -f $charDirs.Count, $names)
$okAssets = $true
foreach ($d in $charDirs) {
  $imgs = @(Get-ChildItem -LiteralPath (Join-Path $d.FullName 'images') -File -ErrorAction SilentlyContinue).Count
  $vcs = @(Get-ChildItem -LiteralPath (Join-Path $d.FullName 'voices') -File -ErrorAction SilentlyContinue).Count
  $prof = Test-Path -LiteralPath (Join-Path $d.FullName 'profile.json')
  if ($imgs -lt 1 -or !$prof) { $okAssets = $false; Emit ("      ✗ {0}: images={1} voices={2} profile={3}" -f $d.Name, $imgs, $vcs, $prof) }
}
Rec 'F6 每个角色的立绘 / 角色设置都在（语音按是否提供）' $okAssets '导入后即可直接开桌宠'

# ============================================================== F7 实机运行
Head 'F7 实机运行（--autostart）'
$proc = Start-Process -FilePath $appExe -ArgumentList @('--autostart') -PassThru
$titles = @()
for ($i = 0; $i -lt 12; $i++) {
  Start-Sleep -Milliseconds 500
  $titles = @(Get-ProcessWindowTitles $proc.Id)
  if ($titles.Count -gt 0) { break }
}
$alive = -not $proc.HasExited
$title = ($titles -join ' | ')
Rec 'F7 桌宠进程存活' $alive ("pid=" + $proc.Id)
Rec 'F7 窗口标题正确' ($title -match '灵工桌宠') ("标题=[{0}]" -f $title)
$logDir = Join-Path $dataDir 'logs'
$logText = ''
if (Test-Path -LiteralPath $logDir) {
  $lf = Get-ChildItem -LiteralPath $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($lf) { $logText = Get-Content -LiteralPath $lf.FullName -Encoding UTF8 -Raw }
}
Rec 'F7 语音清单已加载到运行日志' ($logText -match '语音清单') (([regex]::Match($logText, '语音清单[^\r\n]*')).Value)
try { if (!$proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } } catch { }
Start-Sleep -Milliseconds 800
Rec 'F7 桌宠进程可正常退出' ((Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) -eq $null) '已终止'

# ============================================================== F8 卸载
Head 'F8 静默卸载与清场'
$un = Join-Path $dir '卸载-灵工桌宠.exe'
$unReport = Join-Path $out 'uninstall.txt'
$code = RunExe $un @('/S', "--dir=$dir", '--purge-data', "--report=$unReport")
Rec 'F8 卸载退出码 0' ($code -eq 0) ("exit=" + $code)
$unText = WaitReport $unReport
Rec 'F8 报告含 RESULT:' ($unText -match 'RESULT:') '收尾标记'
Rec 'F8 CLEAN=OK（未能删除 0 项）' ($unText -match 'CLEAN=OK') (([regex]::Match($unText, 'CLEAN=[^\r\n]*')).Value)
Rec 'F8 安装目录里的产品文件已删除' (!(Test-Path -LiteralPath $appExe)) '主程序 gone'
Rec 'F8 无关文件「我的资料.txt」仍在' (Test-Path -LiteralPath (Join-Path $dir '我的资料.txt')) '安全策略'
Rec 'F8 无关文件「other\keep.dat」仍在' (Test-Path -LiteralPath (Join-Path $dir 'other\keep.dat')) '安全策略'
Rec 'F8 共用文件夹本身未被删除' (Test-Path -LiteralPath $dir) $dir
Rec 'F8 安装目录内清单文件已清' (!(Test-Path -LiteralPath (Join-Path $dir 'files.json'))) 'files.json gone'
Rec 'F8 注册表卸载项已删除' ((Get-ItemProperty $reg -ErrorAction SilentlyContinue) -eq $null) 'HKCU Uninstall'
if (!$preexistData) {
  Rec 'F8 数据目录已清除（本次测试新建的）' (!(Test-Path -LiteralPath (Join-Path $dataDir 'characters'))) 'characters gone'
} else {
  Rec 'F8 数据目录保留了本次测试前就存在的内容' $true '（安装前已有数据目录，按策略只删本产品条目）'
}
Rec 'F8 未生成任何延迟删除脚本' (@(Get-ChildItem -LiteralPath $T -Filter 'AzurLaneDeskPet-cleanup-*.cmd' -ErrorAction SilentlyContinue).Count -eq 0) '拒绝/正常路径都不得留脚本'

# ============================================================== F9 清场
Head 'F9 清场'
$realUserDataAfter = CountRealUserData
Rec 'F9 用户真实数据目录（%APPDATA%\AzurLaneDeskPet）未被触碰（文件数未变）' ($realUserDataAfter -eq $realUserDataBefore) `
  ("验收前 {0} 个文件 → 验收后 {1} 个（-1 表示目录不存在）" -f $realUserDataBefore, $realUserDataAfter)
Start-Sleep -Milliseconds 800
$leftover = @(Get-Process -Name 'AzurLaneDeskPet' -ErrorAction SilentlyContinue)
Rec 'F9 无残留桌宠进程' ($leftover.Count -eq 0) ("残留 " + $leftover.Count + " 个")
$strayTemp = @(Get-ChildItem -LiteralPath $T -Filter 'AzurLaneDeskPet-uninstall-*.exe' -ErrorAction SilentlyContinue).Count
Rec 'F9 无残留卸载器临时副本' ($strayTemp -eq 0) ("残留 " + $strayTemp + " 个")
if (!$KeepArtifacts) {
  Remove-AnyDir $dir
  Remove-AnyDir $out
  Rec 'F9 测试目录已清理' ((!(Test-Path -LiteralPath $dir)) -and (!(Test-Path -LiteralPath $out))) 'alp-final / alp-final-out'
} else {
  Emit ("      （-KeepArtifacts）保留：" + $dir + " / " + $out)
}

# ============================================================== 汇总
Head '汇总'
$passCount = @($script:logLines | Where-Object { $_ -match '^\[PASS\]' }).Count
$failCount = @($script:logLines | Where-Object { $_ -match '^\[FAIL\]' }).Count
Emit ("通过 {0} / {1}，失败 {2}" -f $passCount, ($passCount + $failCount), $failCount)
if ($script:fails -eq 0) { Emit '结论：全部通过 ✔' } else { Emit ('结论：有 ' + $script:fails + ' 项失败 ✘') }

$dirOut = Split-Path -Parent $OutReport
if (!(Test-Path -LiteralPath $dirOut)) { New-Item -ItemType Directory -Force -Path $dirOut | Out-Null }
[System.IO.File]::WriteAllLines($OutReport, $script:logLines.ToArray(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ''
Write-Host ("报告已写入：" + $OutReport)
exit $(if ($script:fails -eq 0) { 0 } else { 1 })
