# ============================================================================
# test-security.ps1 —— 卸载安全模型验收测试（防误删 / 防穿透 / 防绕过）
# ----------------------------------------------------------------------------
# 覆盖 6 组用例：
#   S1 共用文件夹不误删：安装到已有无关文件的目录 → 卸载后无关文件仍在、目录仍存在
#   S2 重解析点不穿透：安装目录内的 junction 指向外部目录，卸载器不得删到外部哨兵文件
#   S3 保护不可绕过：对 %TEMP% 本身触发 SafetyCheck 拒绝 → 不得生成任何延迟删除 cmd
#   S4 正常路径回归：常规安装 → 卸载后安装目录/数据目录/快捷方式/注册表全清、CLEAN=OK
#   S5 主程序自检：<安装目录>\AzurLaneDeskPet.exe --selftest --report=...
#   S6 清理：所有临时目录、junction、报告、残留进程全部清除
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File "<项目根>\installer\test-security.ps1"
# ============================================================================
param(
  [string]$ProjectRoot = '',
  [switch]$KeepArtifacts
)
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }

$setup  = Join-Path $ProjectRoot 'dist\灵工桌宠-安装程序.exe'
$T      = $env:TEMP
$regKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AzurLaneDeskPet'
$dataDir = Join-Path $env:TEMP 'alp-sandbox-data-sec'   # 沙箱数据目录（见下）
$realUserData = Join-Path $env:APPDATA 'AzurLaneDeskPet'  # 用户真实数据目录：只做「没被动过」的对照
# 被测的卸载器通过环境变量定位数据目录，指到沙箱里 → 真实目录永远不会被这套测试删掉
$env:AZURLANEDESK_PET_DATA = $dataDir
function CountRealUserData {
  if (!(Test-Path -LiteralPath $realUserData)) { return -1 }
  return @(Get-ChildItem -LiteralPath $realUserData -Recurse -File -Force -ErrorAction SilentlyContinue).Count
}
$realUserDataBefore = CountRealUserData
$sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\灵工桌宠.lnk'
$dk = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '灵工桌宠.lnk'

$script:fails = 0
$script:results = New-Object System.Collections.ArrayList
function Rec([string]$name, [bool]$ok, [string]$detail) {
  [void]$script:results.Add([pscustomobject]@{ Test = $name; OK = $ok; Detail = $detail })
  if (-not $ok) { $script:fails++ }
  Write-Host ("[{0}] {1} :: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Run([string]$exe, [string[]]$argList) {
  if (!(Test-Path -LiteralPath $exe)) { Write-Host "  （找不到可执行文件：$exe）"; return -1 }
  $p = Start-Process -FilePath $exe -ArgumentList $argList -Wait -PassThru
  return $p.ExitCode
}
function ReportText([string]$path) {
  if (Test-Path -LiteralPath $path) { return (Get-Content -LiteralPath $path -Encoding UTF8 -Raw) }
  return ''
}
# 等待卸载器把报告写完再断言。
# 背景：卸载器会把自身复制到 %TEMP% 再重启（阶段二）执行真正的清理与收尾；
# 调用方进程返回时报告可能还没落盘（尤其在负载变大后），固定 Start-Sleep 会造成偶发假失败。
# 这里轮询等待报告出现收尾标记（RESULT:），最多 20 秒。
function WaitReport([string]$path, [int]$timeoutMs = 20000) {
  $deadline = (Get-Date).AddMilliseconds($timeoutMs)
  while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $path) {
      $t = Get-Content -LiteralPath $path -Encoding UTF8 -Raw
      if ($t -and ($t -match 'RESULT:')) { return $t }
    }
    Start-Sleep -Milliseconds 150
  }
  return (ReportText $path)   # 超时也把已有内容返回，交给断言去判定失败
}
# 安全删除目录：遇到 junction / symlink 先用 rmdir（不带 /s）摘掉链接本身，再删目录
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

if (!(Test-Path -LiteralPath $setup)) { throw "找不到安装程序：$setup（先跑 build-setup.ps1）" }

# ---------------------------------------------------------------- S1 共用文件夹
Write-Host "`n===== S1 共用文件夹不误删 ====="
$shared = Join-Path $T 'alp-shared'
if (Test-Path -LiteralPath $shared) { Remove-Item -LiteralPath $shared -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $shared 'other') | Out-Null
Set-Content -LiteralPath (Join-Path $shared '我的资料.txt') -Value 'user data must survive' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $shared 'other\keep.dat') -Value 'keep me' -Encoding UTF8

$repIn = Join-Path $T 'alp-sec-s1-install.txt'
$code = Run $setup @('/S', "/D=$shared", '--no-launch', '--no-shortcuts', "--report=$repIn")
Rec 'S1 静默安装到共用文件夹退出码 0' ($code -eq 0) ("exit=$code")
$fle = Join-Path $shared 'files.json'
$ins = Join-Path $shared 'install.json'
$mkr = Join-Path $shared '.azurlandeskpet-install'
Rec 'S1 files.json 已生成' (Test-Path -LiteralPath $fle) $fle
Rec 'S1 install.json 已生成' (Test-Path -LiteralPath $ins) $ins
Rec 'S1 标记文件已生成' (Test-Path -LiteralPath $mkr) $mkr
$insText = ReportText $ins
Rec 'S1 install.json 含 files/marker/preexisting 字段' `
  (($insText -match '"files"') -and ($insText -match '"marker"') -and ($insText -match '"preexisting"')) '字段校验'
$fleText = ReportText $fle
Rec 'S1 files.json 记录了 files 与 dirs' (($fleText -match '"files"\s*:\s*\[') -and ($fleText -match '"dirs"\s*:\s*\[')) '清单字段'
Rec 'S1 files.json 记录了安装前已存在条目（preexisting>0）' ($fleText -match '"preexisting"\s*:\s*[1-9]') 'preexisting'
Rec 'S1 files.json 用反斜杠相对路径' ($fleText -match '"assets\\\\builtin' -or $fleText -match '"assets\\builtin') '路径分隔符'
Rec 'S1 安装后无关文件仍在' ((Test-Path -LiteralPath (Join-Path $shared '我的资料.txt')) -and (Test-Path -LiteralPath (Join-Path $shared 'other\keep.dat'))) '安装阶段不得动无关文件'

$repUn = Join-Path $T 'alp-sec-s1-uninstall.txt'
$un = Join-Path $shared '卸载-灵工桌宠.exe'
$code = Run $un @('/S', "--dir=$shared", '--purge-data', "--report=$repUn")
$unText = WaitReport $repUn
Rec 'S1 静默卸载退出码 0' ($code -eq 0) ("exit=$code")
Rec 'S1 产品文件已删除（exe/install.json/files.json/标记）' `
  (-not (Test-Path -LiteralPath (Join-Path $shared 'AzurLaneDeskPet.exe')) -and
   -not (Test-Path -LiteralPath $ins) -and -not (Test-Path -LiteralPath $fle) -and
   -not (Test-Path -LiteralPath $mkr)) '产品文件清理'
Rec 'S1 无关文件「我的资料.txt」仍在' (Test-Path -LiteralPath (Join-Path $shared '我的资料.txt')) '保留断言'
Rec 'S1 无关文件「other\keep.dat」仍在' (Test-Path -LiteralPath (Join-Path $shared 'other\keep.dat')) '保留断言'
Rec 'S1 安装目录本身未被删除' (Test-Path -LiteralPath $shared) $shared
Rec 'S1 报告含「已保留（非本产品文件）」小节' ($unText -match '已保留（非本产品文件）') '报告小节'
Rec 'S1 报告列出被保留的无关文件' (($unText -match '我的资料\.txt') -and ($unText -match 'keep\.dat')) '保留清单内容'
Rec 'S1 未生成任何延迟删除 cmd' (@(Get-ChildItem -LiteralPath $T -Filter 'AzurLaneDeskPet-cleanup-*.cmd' -ErrorAction SilentlyContinue).Count -eq 0) '延迟删除断言'

# ---------------------------------------------------------------- S2 重解析点不穿透
Write-Host "`n===== S2 重解析点（junction）不穿透 ====="
$outside = Join-Path $T 'alp-outside'
$jdir    = Join-Path $T 'alp-junction'
$jlink   = Join-Path $jdir 'linked'
if (Test-Path -LiteralPath $outside) { Remove-Item -LiteralPath $outside -Recurse -Force }
if (Test-Path -LiteralPath $jdir) { Remove-Item -LiteralPath $jdir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outside | Out-Null
Set-Content -LiteralPath (Join-Path $outside '外部素材.txt') -Value 'external data must survive' -Encoding UTF8

$code = Run $setup @('/S', "/D=$jdir", '--no-launch', '--no-shortcuts', "--report=$($T)\alp-sec-s2-install.txt")
Rec 'S2 安装到 junction 宿主目录退出码 0' ($code -eq 0) ("exit=$code")
$mk = cmd /c "mklink /J `"$jlink`" `"$outside`"" 2>&1
Rec 'S2 junction 创建成功' ((Test-Path -LiteralPath $jlink) -and (Test-Path -LiteralPath (Join-Path $jlink '外部素材.txt'))) ($mk -join ' ')

# 人为把 junction 路径写进清单，验证「即使清单点名，也不穿透」
$fle = Join-Path $jdir 'files.json'
$raw = Get-Content -LiteralPath $fle -Encoding UTF8 -Raw
$raw = $raw -replace '"files": \[', '"files": ["linked\\外部素材.txt", '
[System.IO.File]::WriteAllText($fle, $raw, (New-Object System.Text.UTF8Encoding($false)))
Rec 'S2 已把 junction 内文件写入清单（用于验证不穿透）' ((ReportText $fle) -match 'linked') '清单注入'

$un = Join-Path $jdir '卸载-灵工桌宠.exe'
$repUn2 = Join-Path $T 'alp-sec-s2-uninstall.txt'
$code = Run $un @('/S', "--dir=$jdir", '--purge-data', "--report=$repUn2")
$unText2 = WaitReport $repUn2
Rec 'S2 静默卸载退出码 0' ($code -eq 0) ("exit=$code")
Rec 'S2 外部哨兵文件仍在' (Test-Path -LiteralPath (Join-Path $outside '外部素材.txt')) '不穿透断言'
Rec 'S2 外部目录仍在' (Test-Path -LiteralPath $outside) $outside
Rec 'S2 junction 本身未被删除' (Test-Path -LiteralPath $jlink) $jlink
Rec 'S2 报告提到重解析点被跳过' ($unText2 -match '重解析点') '报告提及'
Rec 'S2 报告写明「路径含重解析点…已跳过不穿透」' ($unText2 -match '已跳过不穿透') '拒绝原因'
Rec 'S2 报告「安全校验拒绝」小节非空' ($unText2 -match '---- 安全校验拒绝 ----[\s\S]{0,600}✗') '报告小节'
Rec 'S2 报告统计了跳过的重解析点条目数' ($unText2 -match '共跳过 \d+ 个重解析点') '跳过统计'
Rec 'S2 安装目录内产品文件已删除' (-not (Test-Path -LiteralPath (Join-Path $jdir 'AzurLaneDeskPet.exe'))) '产品文件清理'
# 清 junction（不带 /s）
if (Test-Path -LiteralPath $jlink) { cmd /c "rmdir `"$jlink`"" | Out-Null }
Rec 'S2 junction 已用 rmdir（不带 /s）清除，外部目录不受影响' ((-not (Test-Path -LiteralPath $jlink)) -and (Test-Path -LiteralPath $outside)) 'junction 清理'

# ---------------------------------------------------------------- S3 保护不可绕过
Write-Host "`n===== S3 保护不可绕过（拒绝后不得安排延迟删除） ====="
Remove-Item -LiteralPath (Join-Path $T 'AzurLaneDeskPet-cleanup-*.cmd') -Force -ErrorAction SilentlyContinue
$fakeExe = Join-Path $T 'AzurLaneDeskPet.exe'
Set-Content -LiteralPath $fakeExe -Value 'decoy' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $T 'install.json') -Value ('{"product":"灵工桌宠","dataDir":"' + ($dataDir -replace '\\','\\') + '"}') -Encoding UTF8
Set-Content -LiteralPath (Join-Path $T 'files.json') -Value '{"product":"AzurLaneDeskPet","files":["AzurLaneDeskPet.exe"],"dirs":[]}' -Encoding UTF8

$repUn3 = Join-Path $T 'alp-sec-s3-uninstall.txt'
# 用「独立副本」调用：S1/S2 的卸载会把它们自己所在目录里的卸载器一起删掉
$unRef = Join-Path $ProjectRoot 'dist\app\卸载-灵工桌宠.exe'
$code = Run $unRef @('/S', "--dir=$T", '--purge-data', "--report=$repUn3")
$unText3 = WaitReport $repUn3
Rec 'S3 对 %TEMP% 卸载退出码 0（拒绝但流程完成）' ($code -eq 0) ("exit=$code")
Rec 'S3 报告明确写出安全校验拒绝' ($unText3 -match '安全校验拒绝') '报告断言'
Rec 'S3 报告「安全校验拒绝」小节非空' ($unText3 -match '---- 安全校验拒绝 ----[\s\S]{0,400}✗') '拒绝清单'
Rec 'S3 未生成任何 AzurLaneDeskPet-cleanup-*.cmd' (@(Get-ChildItem -LiteralPath $T -Filter 'AzurLaneDeskPet-cleanup-*.cmd' -ErrorAction SilentlyContinue).Count -eq 0) '延迟删除被禁止'
Rec 'S3 未生成任何 AzurLaneDeskPet-rmdir-*.cmd（旧版漏洞回归）' (@(Get-ChildItem -LiteralPath $T -Filter 'AzurLaneDeskPet-rmdir-*.cmd' -ErrorAction SilentlyContinue).Count -eq 0) '旧漏洞回归'
Rec 'S3 被保护目录内容未变（诱饵 exe 仍在）' (Test-Path -LiteralPath $fakeExe) $fakeExe
Rec 'S3 数据目录未被删除' ($unText3 -notmatch '数据目录身份校验通过') '数据目录也应拒绝'
Remove-Item -LiteralPath $fakeExe, (Join-Path $T 'install.json'), (Join-Path $T 'files.json') -Force -ErrorAction SilentlyContinue

# 安装器侧：拒绝把重解析点作为安装目录
$jr = Join-Path $T 'alp-junction2'
if (Test-Path -LiteralPath $jr) { Remove-Item -LiteralPath $jr -Recurse -Force }
$tgt = Join-Path $T 'alp-junction2-target'
New-Item -ItemType Directory -Force -Path $tgt | Out-Null
cmd /c "mklink /J `"$jr`" `"$tgt`"" | Out-Null
$repIn2 = Join-Path $T 'alp-sec-s3b-install.txt'
$code = Run $setup @('/S', "/D=$jr", '--no-launch', '--no-shortcuts', "--report=$repIn2")
$insText2 = ReportText $repIn2
Rec 'S3b 安装器拒绝把 junction 当安装目录（exit=3）' ($code -eq 3) ("exit=$code")
Rec 'S3b 报告写明拒绝原因' ($insText2 -match '重解析点') '安装器侧拒绝'
cmd /c "rmdir `"$jr`"" | Out-Null
Remove-Item -LiteralPath $tgt -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 数据目录身份校验
Write-Host "`n===== S3c 数据目录身份校验 ====="
$d1 = Join-Path $T 'alp-data-nomarker'
if (Test-Path -LiteralPath $d1) { Remove-Item -LiteralPath $d1 -Recurse -Force }
New-Item -ItemType Directory -Force -Path $d1 | Out-Null
Set-Content -LiteralPath (Join-Path $d1 'config.json') -Value '{"volume":0.5}' -Encoding UTF8
$s3c = Join-Path $T 'alp-sec-s3c'
if (Test-Path -LiteralPath $s3c) { Remove-Item -LiteralPath $s3c -Recurse -Force }
$repI3 = Join-Path $T 'alp-sec-s3c-install.txt'
Run $setup @('/S', "/D=$s3c", '--no-launch', '--no-shortcuts', "--report=$repI3") | Out-Null
$insRaw = Get-Content -LiteralPath (Join-Path $s3c 'install.json') -Encoding UTF8 -Raw
$insRaw = $insRaw -replace '"dataDir": "[^"]*"', ('"dataDir": "' + ($d1 -replace '\\','\\') + '"')
[System.IO.File]::WriteAllText((Join-Path $s3c 'install.json'), $insRaw, (New-Object System.Text.UTF8Encoding($false)))
$repU3c = Join-Path $T 'alp-sec-s3c-uninstall.txt'
Run (Join-Path $s3c '卸载-灵工桌宠.exe') @('/S', "--dir=$s3c", '--purge-data', "--report=$repU3c") | Out-Null
$u3c = WaitReport $repU3c
Rec 'S3c 无标记的伪数据目录被拒绝删除且仍在' (Test-Path -LiteralPath $d1) $d1
Rec 'S3c 报告写明数据目录身份校验未通过' ($u3c -match '数据目录身份校验未通过') '校验断言'
Remove-Item -LiteralPath $d1 -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- S4 正常路径回归
Write-Host "`n===== S4 正常路径回归 ====="
$real = Join-Path $T 'alp-real'
if (Test-Path -LiteralPath $real) { Remove-Item -LiteralPath $real -Recurse -Force }
if (Test-Path -LiteralPath $dataDir) { Remove-Item -LiteralPath $dataDir -Recurse -Force }
Remove-Item -LiteralPath $sm, $dk -Force -ErrorAction SilentlyContinue
$repI4 = Join-Path $T 'alp-sec-s4-install.txt'
$code = Run $setup @('/S', "/D=$real", '--no-launch', '--no-shortcuts', "--report=$repI4")
Rec 'S4 常规静默安装退出码 0' ($code -eq 0) ("exit=$code")
Rec 'S4 安装目录非共用文件夹（preexisting=0）' ((ReportText (Join-Path $real 'files.json')) -match '"preexisting"\s*:\s*0') 'preexisting=0'

# 模拟主程序写数据目录（标记 + config.json 带 configVersion）
New-Item -ItemType Directory -Force -Path (Join-Path $dataDir 'logs') | Out-Null
Set-Content -LiteralPath (Join-Path $dataDir '.azurlandeskpet-data') -Value 'AzurLaneDeskPet data marker' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $dataDir 'config.json') -Value ('{"configVersion":1,"dataDir":"' + ($dataDir -replace '\\','\\') + '"}') -Encoding UTF8
Set-Content -LiteralPath (Join-Path $dataDir 'logs\pet.log') -Value 'x' -Encoding UTF8
# 无关文件放到数据目录顶层，必须被保留
Set-Content -LiteralPath (Join-Path $dataDir '我的笔记.md') -Value 'mine' -Encoding UTF8

# 快捷方式（真实创建，验证卸载删除）
$ws = New-Object -ComObject WScript.Shell
foreach ($l in @($sm, $dk)) {
  $sc = $ws.CreateShortcut($l); $sc.TargetPath = Join-Path $real 'AzurLaneDeskPet.exe'; $sc.WorkingDirectory = $real; $sc.Save()
}

$repU4 = Join-Path $T 'alp-sec-s4-uninstall.txt'
$code = Run (Join-Path $real '卸载-灵工桌宠.exe') @('/S', "--dir=$real", '--purge-data', "--report=$repU4")
$u4 = WaitReport $repU4
Rec 'S4 常规静默卸载退出码 0' ($code -eq 0) ("exit=$code")
Rec 'S4 CLEAN=OK（未能删除 0 项）' (($u4 -match 'CLEAN=OK') -and ($u4 -match '未能删除 : 0 项')) '清理结论'
Rec 'S4 安装目录已删除' (-not (Test-Path -LiteralPath $real)) $real
# 快捷方式由卸载器（可能已是"阶段二"进程）删除：给它一点时间，最多轮询 6 秒再判定，
# 避免负载变大后"删完了但断言跑得太早"的假失败
$linksGone = $false
for ($i = 0; $i -lt 24; $i++) {
  if ((-not (Test-Path -LiteralPath $sm)) -and (-not (Test-Path -LiteralPath $dk))) { $linksGone = $true; break }
  Start-Sleep -Milliseconds 250
}
Rec 'S4 快捷方式已删除' $linksGone '快捷方式（轮询等待收尾）'
$regGone = $false
try { Get-ItemProperty -Path $regKey -ErrorAction Stop | Out-Null } catch { $regGone = $true }
Rec 'S4 注册表项已删除' $regGone 'HKCU Uninstall'
Rec 'S4 数据目录的已知条目已清除（logs/config/标记）' `
  ((-not (Test-Path -LiteralPath (Join-Path $dataDir 'logs'))) -and
   (-not (Test-Path -LiteralPath (Join-Path $dataDir 'config.json'))) -and
   (-not (Test-Path -LiteralPath (Join-Path $dataDir '.azurlandeskpet-data')))) '数据清理'
Rec 'S4 数据目录内的无关文件被保留' (Test-Path -LiteralPath (Join-Path $dataDir '我的笔记.md')) '数据目录保留断言'
Rec 'S4 报告列出数据目录中保留的无关文件' ($u4 -match '我的笔记\.md') '保留清单'
Rec 'S4 卸载器临时副本已自删' (@(Get-ChildItem -LiteralPath $T -Filter 'AzurLaneDeskPet-uninstall-*.exe' -ErrorAction SilentlyContinue).Count -eq 0) '自删除'
Remove-Item -LiteralPath $dataDir -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- S5 主程序自检
Write-Host "`n===== S5 主程序自检（--selftest） ====="
$st = Join-Path $T 'alp-st'
if (Test-Path -LiteralPath $st) { Remove-Item -LiteralPath $st -Recurse -Force }
$repI5 = Join-Path $T 'alp-sec-s5-install.txt'
Run $setup @('/S', "/D=$st", '--no-launch', '--no-shortcuts', "--report=$repI5") | Out-Null
$stRep = Join-Path $T 'alp-st.txt'
if (Test-Path -LiteralPath $stRep) { Remove-Item -LiteralPath $stRep -Force }
$pet = Join-Path $st 'AzurLaneDeskPet.exe'
$code = Run $pet @('--selftest', "--report=$stRep")
Rec 'S5 主程序 --selftest 退出码 0' ($code -eq 0) ("exit=$code")
Rec 'S5 主程序自检报告已生成' (Test-Path -LiteralPath $stRep) $stRep
if (Test-Path -LiteralPath $stRep) {
  $txt = Get-Content -LiteralPath $stRep -Encoding UTF8
  $verdict = $txt | Where-Object { $_ -match '^结论：' } | Select-Object -First 1
  if ($verdict) { Rec 'S5 自检结论行' ($verdict -match '全部通过') $verdict }
  else { Write-Host "  （自检报告无「结论：」行）"; $txt | Select-Object -Last 4 | ForEach-Object { "    $_" } }
}
$repU5 = Join-Path $T 'alp-sec-s5-uninstall.txt'
Run (Join-Path $st '卸载-灵工桌宠.exe') @('/S', "--dir=$st", '--purge-data', "--report=$repU5") | Out-Null
Start-Sleep -Milliseconds 800
Rec 'S5 自检目录已卸载清场' (-not (Test-Path -LiteralPath $st)) $st

# ---------------------------------------------------------------- S6 清理
Write-Host "`n===== S6 清理测试痕迹 ====="
$realUserDataAfter = CountRealUserData
Rec 'S6 用户真实数据目录（%APPDATA%\AzurLaneDeskPet）文件数未变' ($realUserDataAfter -eq $realUserDataBefore) `
  ("测试前 {0} 个 → 测试后 {1} 个（-1 表示目录不存在）" -f $realUserDataBefore, $realUserDataAfter)
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'AzurLaneDeskPet*' -or $_.ProcessName -like '卸载*' } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (-not $KeepArtifacts) {
  foreach ($d in @($shared, $outside, $jdir, (Join-Path $T 'alp-junction2'), (Join-Path $T 'alp-junction2-target'), (Join-Path $T 'alp-data-nomarker'), (Join-Path $T 'alp-custom-azurlane-data'))) { Remove-AnyDir $d }
  Get-ChildItem -LiteralPath $T -Filter 'alp-sec-*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  Get-ChildItem -LiteralPath $T -Filter 'AzurLaneDeskPet-cleanup-*.cmd' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  Get-ChildItem -LiteralPath $T -Directory -Filter 'AzurLaneDeskPet-payload-*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
  Remove-Item -LiteralPath $stRep -Force -ErrorAction SilentlyContinue
  # 自检退出标记文件（alp-st.txt.exit）由主程序在退出瞬间写入/删除，留一点时间
  Get-ChildItem -LiteralPath $T -Filter 'alp-st.txt*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 700
  Get-ChildItem -LiteralPath $T -Filter 'alp-st.txt*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
# build-setup.ps1 的自检报告是它的正常产物，本用例清理它（不算测试残留）
Get-ChildItem -LiteralPath $T -Filter 'alp-build-selftest.txt' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$left = @(Get-ChildItem -LiteralPath $T -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(alp-|AzurLane)' })
Rec 'S6 %TEMP% 下无 alp-*/AzurLane* 残留' ($left.Count -eq 0) (($left | ForEach-Object { $_.Name }) -join ', ')

Write-Host "`n================ 汇总 ================"
$script:results | Format-Table -AutoSize
Write-Host ("通过 {0} / {1}，失败 {2}" -f ($script:results.Count - $script:fails), $script:results.Count, $script:fails)
exit $script:fails
