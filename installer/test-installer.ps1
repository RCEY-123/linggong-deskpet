# alp-run-tests.ps1 —— 用于人工/CI 复跑「灵工桌宠」安装器与卸载器的验收测试
param(
  [string]$ProjectRoot = '',
  [string]$Dist = '',
  [string]$Target = (Join-Path $env:TEMP 'alp-test')
)
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($Dist)) { $Dist = Join-Path $ProjectRoot 'dist' }
$setup  = Join-Path $Dist '灵工桌宠-安装程序.exe'
$uninst = Join-Path $Dist 'app\卸载-灵工桌宠.exe'
$repIn  = Join-Path $env:TEMP 'alp-install.txt'
$repUn  = Join-Path $env:TEMP 'alp-uninst.txt'
$regKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AzurLaneDeskPet'
$dataDir = Join-Path $env:TEMP 'alp-sandbox-data'      # 沙箱数据目录（见下）
$realUserData = Join-Path $env:APPDATA 'AzurLaneDeskPet'   # 用户真实数据目录：本脚本只做「没被动过」的对照，绝不写它
$results = New-Object System.Collections.ArrayList

# ---------------------------------------------------------------------------
# 数据目录沙箱（v1.0.8 起必须这样跑）
#   被测的主程序与卸载器都通过环境变量 AZURLANEDESK_PET_DATA 定位数据目录，
#   所以这里把"默认数据目录"指到 %TEMP% 沙箱里：验收测试永远不会碰
#   %APPDATA%\AzurLaneDeskPet 这个真实目录（旧版会把用户的角色与设置一起删掉）。
# ---------------------------------------------------------------------------
$env:AZURLANEDESK_PET_DATA = $dataDir
function CountRealUserData {
  if (!(Test-Path -LiteralPath $realUserData)) { return -1 }
  return @(Get-ChildItem -LiteralPath $realUserData -Recurse -File -Force -ErrorAction SilentlyContinue).Count
}
$realUserDataBefore = CountRealUserData

function Rec([string]$name, [bool]$ok, [string]$detail) {
  [void]$results.Add([pscustomobject]@{ Test = $name; OK = $ok; Detail = $detail })
  $tag = if ($ok) { 'PASS' } else { 'FAIL' }
  Write-Host ("[{0}] {1} :: {2}" -f $tag, $name, $detail)
}

# ---------------------------------------------------------------- T1
Write-Host "`n=== T1 产物 ==="
$t1 = (Test-Path $setup) -and (Test-Path $uninst)
Rec 'T1 两个 exe 存在' $t1 ("setup={0} uninstaller={1}" -f (Test-Path $setup), (Test-Path $uninst))

# ---------------------------------------------------------------- T2 + T3 dry-run
Write-Host "`n=== T3 dry-run（不得写盘） ==="
if (Test-Path $Target) { Remove-Item $Target -Recurse -Force -ErrorAction SilentlyContinue }
$dryRep = Join-Path $env:TEMP 'alp-dryrun.txt'
if (Test-Path $dryRep) { Remove-Item $dryRep -Force }
$p = Start-Process -FilePath $setup -ArgumentList @('/S', "/D=$Target", '--dry-run', '--no-launch', '--no-shortcuts', "--report=$dryRep") -Wait -PassThru
$dryOk = ($p.ExitCode -eq 0) -and (-not (Test-Path $Target)) -and (Test-Path $dryRep)
Rec 'T3 dry-run 退出码 0 且未创建目录' $dryOk ("exit={0} dirExists={1} report={2}" -f $p.ExitCode, (Test-Path $Target), (Test-Path $dryRep))
$dryText = if (Test-Path $dryRep) { Get-Content $dryRep -Encoding UTF8 -Raw } else { '' }
Rec 'T3 dry-run 报告含 RESULT=OK(dry-run)' ($dryText -match 'RESULT=OK\(dry-run\)') 'report 内容校验'

Write-Host "`n=== T2 静默安装 ==="
if (Test-Path $Target) { Remove-Item $Target -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $repIn) { Remove-Item $repIn -Force }
$p = Start-Process -FilePath $setup -ArgumentList @('/S', "/D=$Target", '--no-launch', '--no-shortcuts', "--report=$repIn") -Wait -PassThru
$exeOk = Test-Path (Join-Path $Target 'AzurLaneDeskPet.exe')
$manOk = Test-Path (Join-Path $Target 'install.json')
$unOk  = Test-Path (Join-Path $Target '卸载-灵工桌宠.exe')
$txtOk = Test-Path (Join-Path $Target '使用说明.txt')
$assetsOk = (Test-Path (Join-Path $Target 'assets'))
Rec 'T2 静默安装退出码 0' ($p.ExitCode -eq 0) ("exit={0}" -f $p.ExitCode)
Rec 'T2 主程序存在' $exeOk (Join-Path $Target 'AzurLaneDeskPet.exe')
Rec 'T2 install.json 存在' $manOk (Join-Path $Target 'install.json')
Rec 'T2 卸载器在负载内' $unOk (Join-Path $Target '卸载-灵工桌宠.exe')
Rec 'T2 使用说明.txt 在负载内' $txtOk (Join-Path $Target '使用说明.txt')
Rec 'T2 assets 目录在负载内' $assetsOk (Join-Path $Target 'assets')
$manText = if ($manOk) { Get-Content (Join-Path $Target 'install.json') -Encoding UTF8 -Raw } else { '' }
$manFields = @('"product"', '"version"', '"installDir"', '"dataDir"', '"shortcuts"', '"registryKey"', '"installedAt"')
$miss = @($manFields | Where-Object { $manText -notmatch [regex]::Escape($_) })
Rec 'T2 install.json 字段齐全' ($miss.Count -eq 0) ("missing=" + ($miss -join ','))
$repText = if (Test-Path $repIn) { Get-Content $repIn -Encoding UTF8 -Raw } else { '' }
Rec 'T2 报告含 exit=0' ($repText -match 'exit=0') 'report 内容校验'
Rec 'T2 报告中文未乱码' ($repText -match '灵工桌宠') 'UTF-8 读取校验'
$regOk = $false
try { $rp = Get-ItemProperty $regKey -ErrorAction Stop; $regOk = ($rp.DisplayName -eq '灵工桌宠' -and $rp.DisplayVersion -match '^\d+\.\d+' -and $rp.NoModify -eq 1 -and $rp.NoRepair -eq 1) } catch { }
if (-not $regOk) { Write-Host ('  注册表读取到：' + (Test-Path $regKey)) }
Rec 'T2 注册表卸载项正确' $regOk 'HKCU Uninstall\AzurLaneDeskPet'

# ---------------------------------------------------------------- 注册表中文读取
if ($regOk) {
  $rp2 = Get-ItemProperty $regKey
  Rec 'T2 DisplayName 中文正确' ($rp2.DisplayName -eq '灵工桌宠') ("DisplayName=" + $rp2.DisplayName)
  Rec 'T2 UninstallString 指向卸载器' ($rp2.UninstallString -match '卸载-灵工桌宠\.exe') ("UninstallString=" + $rp2.UninstallString)
}

# ---------------------------------------------------------------- 造数据 / 快捷方式，供卸载验证
Write-Host "`n=== 准备用户数据与快捷方式（验证卸载清理） ==="
New-Item -ItemType Directory -Force -Path (Join-Path $dataDir 'logs') | Out-Null
# 安全模型：数据目录必须带身份标记（或 config.json 含 configVersion）才允许被删除 —— 这里模拟主程序写入
Set-Content -Path (Join-Path $dataDir '.azurlandeskpet-data') -Value 'AzurLaneDeskPet data marker' -Encoding UTF8
# 注意：config.json 里的 dataDir 必须指向沙箱自己，否则卸载器会把它当"自定义数据目录"去删真实目录
Set-Content -Path (Join-Path $dataDir 'config.json') -Value ('{"configVersion":1,"dataDir":"' + ($dataDir -replace '\\', '\\') + '","volume":80}') -Encoding UTF8
Set-Content -Path (Join-Path $dataDir 'logs\pet.log') -Value 'hello' -Encoding UTF8
$custom = Join-Path $env:TEMP 'alp-custom-azurlane-data'
New-Item -ItemType Directory -Force -Path $custom | Out-Null
Set-Content -Path (Join-Path $custom 'voice.txt') -Value 'test' -Encoding UTF8
Set-Content -Path (Join-Path $custom '.azurlandeskpet-data') -Value 'AzurLaneDeskPet data marker' -Encoding UTF8

$sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\灵工桌宠.lnk'
$dk = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '灵工桌宠.lnk'
$ws = New-Object -ComObject WScript.Shell
foreach ($l in @($sm, $dk)) {
  $sc = $ws.CreateShortcut($l)
  $sc.TargetPath = Join-Path $Target 'AzurLaneDeskPet.exe'
  $sc.WorkingDirectory = $Target
  $sc.Save()
}
Write-Host ("  快捷方式：{0} / {1}" -f (Test-Path $sm), (Test-Path $dk))

# 把自定义 dataDir 写进 install.json（模拟主程序记录的路径），供卸载器读取
$manPath = Join-Path $Target 'install.json'
if (Test-Path $manPath) {
  $mt = Get-Content $manPath -Encoding UTF8 -Raw
  $mt = $mt -replace '"dataDir"\s*:\s*"[^"]*"', ('"dataDir": "' + ($custom -replace '\\', '\\') + '"')
  [System.IO.File]::WriteAllText($manPath, $mt, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "  已把 install.json 的 dataDir 改成自定义目录（含 AzurLane）"
}
# 另造一个“危险”目录，验证安全校验不会删它
$danger = Join-Path $env:TEMP 'alp-danger'
New-Item -ItemType Directory -Force -Path $danger | Out-Null
Set-Content -Path (Join-Path $danger 'x.txt') -Value 'keep' -Encoding UTF8

# ---------------------------------------------------------------- T4 静默卸载
Write-Host "`n=== T4 静默卸载 ==="
if (Test-Path $repUn) { Remove-Item $repUn -Force }
$p = Start-Process -FilePath (Join-Path $Target '卸载-灵工桌宠.exe') -ArgumentList @('/S', "--dir=$Target", '--purge-data', "--report=$repUn") -Wait -PassThru
Start-Sleep -Milliseconds 800
$unText = if (Test-Path $repUn) { Get-Content $repUn -Encoding UTF8 -Raw } else { '' }
Rec 'T4 卸载退出码 0' ($p.ExitCode -eq 0) ("exit={0}" -f $p.ExitCode)
Rec 'T4 安装目录已删除' (-not (Test-Path $Target)) $Target
Rec 'T4 默认数据目录已删除' (-not (Test-Path $dataDir)) $dataDir
# 安全策略：dataDir 只删「已知条目」，非产品文件保留；voice.txt 属非产品文件 → 目录应保留
Rec 'T4 自定义 dataDir 的非产品文件被保留（安全策略）' (Test-Path (Join-Path $custom 'voice.txt')) $custom
Rec 'T4 自定义 dataDir 仍在（因含保留文件）' (Test-Path $custom) $custom
Rec 'T4 报告列出数据目录中保留的文件' ($unText -match 'voice\.txt') '保留清单'
Rec 'T4 开始菜单快捷方式已删除' (-not (Test-Path $sm)) $sm
Rec 'T4 桌面快捷方式已删除' (-not (Test-Path $dk)) $dk
$regGone = $false
try { Get-ItemProperty $regKey -ErrorAction Stop | Out-Null } catch { $regGone = $true }
# 卸载器会把自己复制到 %TEMP% 再重启（阶段二）执行真正的清理，因此调用方进程返回时
# 注册表可能还没删完。这里轮询等待，避免用固定 sleep 造成偶发假失败。
if (!$regGone) {
  for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 200
    try { Get-ItemProperty $regKey -ErrorAction Stop | Out-Null } catch { $regGone = $true; break }
  }
}
Rec 'T4 注册表卸载项已删除' $regGone 'HKCU Uninstall\AzurLaneDeskPet'
$prodGone = $false
try { Get-ItemProperty 'HKCU:\Software\AzurLaneDeskPet' -ErrorAction Stop | Out-Null } catch { $prodGone = $true }
if (!$prodGone) {
  for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 200
    try { Get-ItemProperty 'HKCU:\Software\AzurLaneDeskPet' -ErrorAction Stop | Out-Null } catch { $prodGone = $true; break }
  }
}
Rec 'T4 产品注册表键已删除' $prodGone 'HKCU\Software\AzurLaneDeskPet'
Rec 'T4 无危险目录误删' (Test-Path $danger) $danger
Rec 'T4 报告含 exit=0' ($unText -match 'exit=0') 'report 内容校验'
Rec 'T4 报告中文未乱码' ($unText -match '灵工桌宠') 'UTF-8 读取校验'
Rec 'T4 报告含统计信息' ($unText -match '删除文件') 'report 统计段'
# 临时副本是否已自删
$left = @(Get-ChildItem $env:TEMP -Filter 'AzurLaneDeskPet-uninstall-*.exe' -ErrorAction SilentlyContinue)
Rec 'T4 卸载器临时副本已自删' ($left.Count -eq 0) ("剩余 {0} 个临时副本" -f $left.Count)

# ---------------------------------------------------------------- T5 GUI 不阻塞
Write-Host "`n=== T5 GUI 启动 3 秒后强杀 ==="
$gp = Start-Process -FilePath $setup -PassThru
Start-Sleep -Seconds 3
$alive = -not $gp.HasExited
Stop-Process -Id $gp.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
$killed = $null -eq (Get-Process -Id $gp.Id -ErrorAction SilentlyContinue)
Rec 'T5 安装器 GUI 可启动且可被强杀' ($alive -and $killed) ("3 秒时仍在运行={0}，已杀死={1}" -f $alive, $killed)

# ---------------------------------------------------------------- T6 用户真实数据目录未被触碰
Write-Host "`n=== T6 用户真实数据目录未被触碰 ==="
$realUserDataAfter = CountRealUserData
Rec 'T6 用户真实数据目录（%APPDATA%\AzurLaneDeskPet）文件数未变' ($realUserDataAfter -eq $realUserDataBefore) `
  ("测试前 {0} 个 → 测试后 {1} 个（-1 表示目录不存在）" -f $realUserDataBefore, $realUserDataAfter)

# ---------------------------------------------------------------- 清理
Write-Host "`n=== 清理测试痕迹 ==="
foreach ($d in @($danger, (Join-Path $env:TEMP 'alp-custom-azurlane-data'), $dataDir)) {
  if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
}
foreach ($f in @($dryRep, $repIn, $repUn)) {
  if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
}
$payloads = @(Get-ChildItem $env:TEMP -Directory -Filter 'AzurLaneDeskPet-payload-*' -ErrorAction SilentlyContinue)
foreach ($d in $payloads) { Remove-Item $d.FullName -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ("  已清理临时目录/报告（残留 payload 目录 {0} 个）" -f $payloads.Count)

Write-Host "`n================ 汇总 ================"
$results | Format-Table -AutoSize
$fail = @($results | Where-Object { -not $_.OK })
Write-Host ("通过 {0} / {1}，失败 {2}" -f ($results.Count - $fail.Count), $results.Count, $fail.Count)
if ($fail.Count -gt 0) { Write-Host '失败项：'; $fail | ForEach-Object { Write-Host ("  - {0} :: {1}" -f $_.Test, $_.Detail) } }
exit $fail.Count
