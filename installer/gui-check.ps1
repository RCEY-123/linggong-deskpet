# alp-gui-check.ps1 —— 图形模式补充验证（不点击任何按钮）
#   G1: 安装器不带 /S 启动 → 3 秒内保持运行（窗口存在）→ 强杀
#   G2: 真实安装（默认快捷方式）→ 校验开始菜单/桌面 .lnk 存在
#   G3: 卸载器不带 /S 启动（会自搬迁到 %TEMP%）→ 确认弹窗出现即“正在等用户确认”
#       → 强杀临时副本（此时尚未点“是”，不会有任何删除动作）
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$setup  = Join-Path $ProjectRoot 'dist\灵工桌宠-安装程序.exe'
$target = Join-Path $env:TEMP 'alp-gui-test'
$sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\灵工桌宠.lnk'
$dk = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '灵工桌宠.lnk'
$regKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AzurLaneDeskPet'
$fails = 0

function Rec([string]$n, [bool]$ok, [string]$d) {
  Write-Host ("[{0}] {1} :: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $n, $d)
  if (-not $ok) { $script:fails++ }
}

Write-Host "`n=== G2 真实安装（含快捷方式） ==="
if (Test-Path $target) { Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item $sm, $dk -Force -ErrorAction SilentlyContinue
$rep = Join-Path $env:TEMP 'alp-gui-install.txt'
$p = Start-Process -FilePath $setup -ArgumentList @('/S', "/D=$target", '--no-launch', "--report=$rep") -Wait -PassThru
Rec 'G2 静默安装（默认建快捷方式）退出码 0' ($p.ExitCode -eq 0) ("exit={0}" -f $p.ExitCode)
Rec 'G2 开始菜单快捷方式已创建' (Test-Path $sm) $sm
Rec 'G2 桌面快捷方式已创建' (Test-Path $dk) $dk
if (Test-Path $sm) {
  $ws = New-Object -ComObject WScript.Shell
  $sc = $ws.CreateShortcut($sm)
  Rec 'G2 快捷方式指向主程序' ($sc.TargetPath -eq (Join-Path $target 'AzurLaneDeskPet.exe')) ("TargetPath=" + $sc.TargetPath)
  Rec 'G2 快捷方式含图标与工作目录' (($sc.IconLocation -like '*AzurLaneDeskPet.exe*') -and ($sc.WorkingDirectory -eq $target)) ("Icon=" + $sc.IconLocation + " WD=" + $sc.WorkingDirectory)
}
Rec 'G2 清单记录了快捷方式' ((Get-Content (Join-Path $target 'install.json') -Encoding UTF8 -Raw) -match 'Start Menu') 'install.json shortcuts'

Write-Host "`n=== G1 安装器 GUI 模式不为阻塞（3 秒后强杀） ==="
$g = Start-Process -FilePath $setup -PassThru
Start-Sleep -Seconds 3
$alive = -not $g.HasExited
$title = ''
try { $g.Refresh(); $title = $g.MainWindowTitle } catch { }
Stop-Process -Id $g.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Rec 'G1 安装器 GUI 启动后保持运行（未自动退出）' $alive ("3 秒仍在运行={0}" -f $alive)
Rec 'G1 安装器窗口标题为灵工桌宠' ($title -like '*灵工桌宠*') ("MainWindowTitle=" + $title)
$mine = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -like "$target*") })
Rec 'G1 本测试产生的进程已被清理' ($mine.Count -eq 0) ("残留 " + $mine.Count + " 个")

Write-Host "`n=== G3 卸载器 GUI 模式（自搬迁 + 等待确认，不点击） ==="
$un = Join-Path $target '卸载-灵工桌宠.exe'
$before = @(Get-ChildItem $env:TEMP -Filter 'AzurLaneDeskPet-uninstall-*.exe' -ErrorAction SilentlyContinue).Count
$u = Start-Process -FilePath $un -PassThru
$pidOrig = $u.Id
Start-Sleep -Seconds 4
$origGone = $null -eq (Get-Process -Id $pidOrig -ErrorAction SilentlyContinue)
$temps = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*AzurLaneDeskPet-uninstall-*.exe' })
Rec 'G3 原卸载器进程已退出（已搬迁到临时副本）' $origGone ("pid {0} 仍存在={1}" -f $pidOrig, (-not $origGone))
Rec 'G3 存在临时副本进程并等待用户确认' ($temps.Count -ge 1) ("临时副本进程数=" + $temps.Count)
# 未点击“是”，安装目录与注册表必须原封不动
Rec 'G3 未确认前不删除任何东西（安装目录仍在）' (Test-Path (Join-Path $target 'AzurLaneDeskPet.exe')) $target
$stillReg = $false
try { Get-ItemProperty $regKey -ErrorAction Stop | Out-Null; $stillReg = $true } catch { }
Rec 'G3 未确认前注册表项仍在' $stillReg 'HKCU Uninstall\AzurLaneDeskPet'
# 强杀临时副本与任何残留窗口
foreach ($t in $temps) { Stop-Process -Id $t.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 600
$left = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'AzurLaneDeskPet-uninstall*' })
Rec 'G3 已清理临时副本进程' ($left.Count -eq 0) ("残留=" + $left.Count)

Write-Host "`n=== 收尾：静默卸载清场 ==="
$p2 = Start-Process -FilePath $un -ArgumentList @('/S', "/dir=$target", '--purge-data', "--report=$($env:TEMP)\alp-gui-uninst.txt") -Wait -PassThru
Start-Sleep -Milliseconds 800
Rec 'S 收尾静默卸载退出码 0' ($p2.ExitCode -eq 0) ("exit={0}" -f $p2.ExitCode)
Rec 'S 安装目录已清除' (-not (Test-Path $target)) $target
Rec 'S 快捷方式已清除' ((-not (Test-Path $sm)) -and (-not (Test-Path $dk))) "$sm / $dk"
$gone = $false
try { Get-ItemProperty $regKey -ErrorAction Stop | Out-Null } catch { $gone = $true }
Rec 'S 注册表项已清除' $gone $regKey

Write-Host "`n清理临时文件..."
foreach ($f in @($rep, (Join-Path $env:TEMP 'alp-gui-uninst.txt'))) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
foreach ($d in @(Get-ChildItem $env:TEMP -Directory -Filter 'AzurLaneDeskPet-payload-*' -ErrorAction SilentlyContinue)) { Remove-Item $d.FullName -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ("`n失败项：{0}" -f $fails)
exit $fails
