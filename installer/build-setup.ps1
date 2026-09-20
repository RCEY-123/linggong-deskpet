# ============================================================================
# build-setup.ps1 —— 构建「灵工桌宠」安装器与卸载器
# ----------------------------------------------------------------------------
# 流程：
#   0) 生成 icon.ico（若 installer\icon.ico 已存在则复用）—— 深蓝底 + 金色舰装/锚徽记
#   1) 组装 payload staging：优先用 <项目根>\dist\app，其次 <项目根>\app；
#      都没有主程序时现场编译一个极小的占位 WinForms 程序（仅供构建/测试，绝不覆盖真程序）
#   2) 打包 payload.zip（内容 = 负载目录下全部文件）
#   3) 用系统自带 csc（.NET Framework 4.x / C# 5，/codepage:65001）编译：
#        · dist\灵工桌宠-安装程序.exe   （/resource:payload.zip,payload.zip）
#        · dist\app\卸载-灵工桌宠.exe
#   4) 自检：dry-run 安装 + 编译产物存在性
#
# 用法（项目根默认取本脚本上一级目录，因此项目文件夹改名/移动后无需改脚本）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File "<项目根>\installer\build-setup.ps1"
#   powershell ... -ProjectRoot 'D:\other' -SkipSelfTest
#
# 依赖：Windows + .NET Framework 4.x 自带 csc.exe（不需要 dotnet SDK / node / nuget）
# ============================================================================
param(
  [string]$ProjectRoot = '',
  [switch]$SkipSelfTest,
  [switch]$ForceIcon,
  [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
  $ProjectRoot = Split-Path -Parent $PSScriptRoot          # installer 的上一级 = 项目根
}
if (!(Test-Path -LiteralPath $ProjectRoot)) { throw "项目根不存在：$ProjectRoot" }

function Say([string]$t) { Write-Output $t }
function Fail([string]$t) { throw $t }

$installerDir = $PSScriptRoot
$dist         = Join-Path $ProjectRoot 'dist'
$distApp      = Join-Path $dist 'app'
$icoPath      = Join-Path $installerDir 'icon.ico'
$zipPath      = Join-Path $installerDir 'payload.zip'
$setupCs      = Join-Path $installerDir 'DeskPetSetup.cs'
$uninstCs     = Join-Path $installerDir 'Uninstaller.cs'
$infoCs       = Join-Path $installerDir 'InstallerInfo.cs'
$setupOut     = Join-Path $dist '灵工桌宠-安装程序.exe'
$uninstOut    = Join-Path $distApp '卸载-灵工桌宠.exe'

# 负载来源解析：
#   1) dist\app（tools\build.ps1 的标准输出目录）
#   2) 项目根 app\（部分构建流程把成品放在这里）
#   3) 都没有 → 现编译占位主程序（仅供无主程序时也能构建/测试）
function Resolve-PayloadSource([string]$root, [string]$distAppDir) {
  $candidates = @($distAppDir, (Join-Path $root 'app'))
  foreach ($c in $candidates) {
    $exe = Join-Path $c 'AzurLaneDeskPet.exe'
    if (!(Test-Path $exe)) { continue }
    try {
      $len = (Get-Item $exe).Length
      $head = [System.IO.File]::ReadAllBytes($exe)
      if ($len -gt 4096 -and $head.Length -gt 1 -and $head[0] -eq 0x4D -and $head[1] -eq 0x5A) { return $c }
      Say ("  候选负载目录的主程序体积/格式异常（{0}，{1} bytes），跳过" -f $c, $len)
    } catch { }
  }
  return ''
}
$payloadSrc   = Resolve-PayloadSource $ProjectRoot $distApp
$appPlaceholder = if ($payloadSrc) { Join-Path $payloadSrc 'AzurLaneDeskPet.exe' } else { Join-Path $distApp 'AzurLaneDeskPet.exe' }

foreach ($f in @($setupCs, $uninstCs, $infoCs)) { if (!(Test-Path $f)) { Fail "缺少源码：$f" } }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# ---------------------------------------------------------------------------
# csc：先找出编译器
# ---------------------------------------------------------------------------
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path $csc)) { Fail '找不到系统自带的 csc.exe（需要 .NET Framework 4.x）' }
$cscArgs = @('/nologo', '/codepage:65001', '/optimize+', '/platform:anycpu')

# 工作目录（含中文）：csc 对命令行里的中文路径不总是友好，
# 因此统一复制到 ASCII 临时目录编译，再把产物拷回目标位置。
$work = Join-Path $env:TEMP ('alp-build-' + [System.Diagnostics.Process]::GetCurrentProcess().Id)
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null
Say "工作目录：$work"

try {
  # -------------------------------------------------------------------------
  # [0/5] 图标
  # -------------------------------------------------------------------------
  Say '[0/5] 准备图标...'
  if ((Test-Path $icoPath) -and !$ForceIcon) {
    Say ("  复用已有 icon.ico（{0} bytes）" -f (Get-Item $icoPath).Length)
  } else {
    Add-Type -AssemblyName System.Drawing
    $sizes = @(256, 128, 64, 48, 32, 16)
    $pngs = @()
    foreach ($s in $sizes) {
      $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
      $g = [System.Drawing.Graphics]::FromImage($bmp)
      $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
      $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $g.Clear([System.Drawing.Color]::Transparent)

      # 深海军蓝渐变底 + 金色描边（圆角方章）
      $navy1 = [System.Drawing.Color]::FromArgb(255, 0x0E, 0x1B, 0x33)
      $navy2 = [System.Drawing.Color]::FromArgb(255, 0x16, 0x30, 0x5C)
      $gold  = [System.Drawing.Color]::FromArgb(255, 0xE8, 0xC8, 0x6A)
      $pad = [double]$s * 0.045
      $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
      $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $navy1, $navy2, 60.0)
      $path = New-Object System.Drawing.Drawing2D.GraphicsPath
      $r = [double]$s * 0.14
      $x = $rect.X; $y = $rect.Y; $w = $rect.Width; $h = $rect.Height
      $path.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
      $path.AddArc(($x + $w - 2 * $r), $y, 2 * $r, 2 * $r, 270, 90)
      $path.AddArc(($x + $w - 2 * $r), ($y + $h - 2 * $r), 2 * $r, 2 * $r, 0, 90)
      $path.AddArc($x, ($y + $h - 2 * $r), 2 * $r, 2 * $r, 90, 90)
      $path.CloseFigure()
      $g.FillPath($lg, $path)
      $pen = New-Object System.Drawing.Pen($gold, ([float]([Math]::Max(1.0, $s * 0.032))))
      $g.DrawPath($pen, $path)

      $cx = $s / 2.0
      # 船体（舰首朝右）用多边形拼，避免 GraphicsPath 重载在 PowerShell 下的解析歧义
      $hy = $s * 0.585
      $hw = $s * 0.30
      $hh = $s * 0.105
      $hullBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xF2, 0xF6, 0xFF))
      $hp = New-Object System.Drawing.Pen($gold, ([float]([Math]::Max(0.8, $s * 0.014))))
      $hull = New-Object System.Drawing.Drawing2D.GraphicsPath
      $p1 = New-Object System.Drawing.PointF([float]($cx - $hw), [float]($hy - $hh * 0.55))
      $p2 = New-Object System.Drawing.PointF([float]($cx + $hw * 0.80), [float]($hy - $hh * 0.55))
      $p3 = New-Object System.Drawing.PointF([float]($cx + $hw * 1.18), [float]($hy + $hh * 0.40))
      $p4 = New-Object System.Drawing.PointF([float]($cx - $hw * 0.60), [float]($hy + $hh * 0.85))
      $p5 = New-Object System.Drawing.PointF([float]($cx - $hw * 1.02), [float]($hy + $hh * 0.20))
      $hull.AddPolygon(@($p1, $p2, $p3, $p4, $p5))
      $g.FillPath($hullBrush, $hull)
      $g.DrawPath($hp, $hull)
      # 甲板金线
      $g.DrawLine($hp, [float]($cx - $hw * 0.72), [float]($hy - $hh * 0.15), [float]($cx + $hw * 0.66), [float]($hy - $hh * 0.15))
      # 舰桥（金色方块 + 深蓝细节）
      $by = $hy - $hh * 0.55 - $s * 0.125
      $bw = $s * 0.135
      $bh = $s * 0.125
      $bBrush = New-Object System.Drawing.SolidBrush($gold)
      $bPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0x0E, 0x1B, 0x33), ([float]([Math]::Max(0.6, $s * 0.010))))
      $br = New-Object System.Drawing.RectangleF([float]($cx - $bw * 0.70), [float]$by, [float]$bw, [float]$bh)
      $g.FillRectangle($bBrush, $br)
      $g.DrawRectangle($bPen, [float]$br.X, [float]$br.Y, [float]$br.Width, [float]$br.Height)
      # 桅杆 + 斜拉索
      $mp = New-Object System.Drawing.Pen($gold, ([float]([Math]::Max(0.8, $s * 0.022))))
      $g.DrawLine($mp, [float]($cx - $bw * 0.28), [float]$by, [float]($cx - $bw * 0.28), [float]($by - $s * 0.125))
      $g.DrawLine($mp, [float]($cx - $bw * 0.28), [float]($by - $s * 0.105), [float]($cx + $hw * 0.62), [float]($hy - $hh * 1.55))
      $g.DrawLine($mp, [float]($cx - $bw * 0.28), [float]($by - $s * 0.058), [float]($cx - $hw * 0.92), [float]($hy - $hh * 1.35))
      # 顶部锚形徽记
      $ay = $s * 0.225
      $ar = $s * 0.080
      $g.DrawEllipse($mp, [float]($cx - $ar * 0.55), [float]($ay - $ar), [float]($ar * 1.1), [float]($ar * 1.1))
      $g.DrawLine($mp, [float]$cx, [float]($ay - $ar * 0.45), [float]$cx, [float]($ay + $ar * 1.15))
      $g.DrawLine($mp, [float]($cx - $ar), [float]($ay + $ar * 0.10), [float]($cx + $ar), [float]($ay + $ar * 0.10))
      $g.DrawArc($mp, [float]($cx - $ar), [float]($ay - $ar * 0.30), [float]($ar * 2), [float]($ar * 1.45), 20, 140)

      $g.Dispose(); $lg.Dispose(); $path.Dispose(); $pen.Dispose(); $hull.Dispose()
      $hullBrush.Dispose(); $hp.Dispose(); $bBrush.Dispose(); $mp.Dispose()
      $ms = New-Object System.IO.MemoryStream
      $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
      $pngs += , @($s, $ms.ToArray())
      $ms.Dispose(); $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($icoPath)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
      $s = $p[0]; $bytes = $p[1]
      $d = if ($s -ge 256) { 0 } else { $s }
      $bw.Write([Byte]$d); $bw.Write([Byte]$d); $bw.Write([Byte]0); $bw.Write([Byte]0)
      $bw.Write([UInt16]1); $bw.Write([UInt16]32)
      $bw.Write([UInt32]$bytes.Length); $bw.Write([UInt32]$offset)
      $offset += $bytes.Length
    }
    foreach ($p in $pngs) { $bw.Write($p[1]) }
    $bw.Flush(); $bw.Close(); $fs.Close()
    Say ("  已生成 icon.ico（{0} bytes，{1} 个尺寸）" -f (Get-Item $icoPath).Length, $pngs.Count)
  }

  # -------------------------------------------------------------------------
  # [1/5] staging
  # -------------------------------------------------------------------------
  Say '[1/5] 组装安装负载 staging...'
  $stage = Join-Path $work 'payload'
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $stage | Out-Null

  # 负载来源已在脚本开头解析（payloadSrc），这里据此组装 staging。
  # 只有主程序缺失/无效时才会写占位 exe（受任务约束：不覆盖主代理的产物）。
  $hasRealApp = ($payloadSrc -ne '')
  if ($hasRealApp) {
    Say ("  使用已有负载目录（AzurLaneDeskPet.exe {0:N0} bytes）：{1}" -f (Get-Item $appPlaceholder).Length, $payloadSrc)
    robocopy $payloadSrc $stage /E /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
    # 负载目录里可能残留上一次构建的旧卸载器，先剔除，稍后放入本轮编译结果
    $stale = @(Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -like '*卸载*' })
    foreach ($s in $stale) { Remove-Item $s.FullName -Force -ErrorAction SilentlyContinue }
    if (!(Test-Path (Join-Path $stage 'assets'))) { New-Item -ItemType Directory -Force -Path (Join-Path $stage 'assets') | Out-Null }
  } else {
    Say '  没有可用主程序 → 生成占位负载（仅供测试）'
    New-Item -ItemType Directory -Force -Path $distApp | Out-Null
    # 占位主程序：极小 WinForms 程序
    $stubCs = Join-Path $work 'StubPet.cs'
    @'
using System;
using System.Drawing;
using System.Windows.Forms;
internal static class StubPet
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool silent = false;
        foreach (string a in args) { if (a == "/S" || a == "--silent") silent = true; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (silent) return;
        Form f = new Form();
        f.Text = "灵工桌宠（占位测试程序）";
        f.ClientSize = new Size(420, 180);
        f.BackColor = Color.FromArgb(0x0E, 0x1B, 0x33);
        f.StartPosition = FormStartPosition.CenterScreen;
        Label l = new Label();
        l.Text = "灵工桌宠" + Environment.NewLine + "这是安装器自检用的占位程序。";
        l.ForeColor = Color.FromArgb(0xE8, 0xC8, 0x6A);
        l.Font = new Font("宋体", 14F, FontStyle.Bold);
        l.SetBounds(20, 30, 380, 60);
        f.Controls.Add(l);
        Label p = new Label();
        p.Text = "product dir: " + AppDomain.CurrentDomain.BaseDirectory;
        p.ForeColor = Color.White;
        p.Font = new Font("Consolas", 8F);
        p.SetBounds(20, 110, 380, 40);
        f.Controls.Add(p);
        Application.Run(f);
    }
}
'@ | Set-Content -Path $stubCs -Encoding UTF8
    $stubOut = Join-Path $work 'AzurLaneDeskPet.exe'
    & $csc @cscArgs /target:winexe "/out:$stubOut" /r:System.Windows.Forms.dll /r:System.Drawing.dll $stubCs
    if (!(Test-Path $stubOut)) { Fail '占位主程序编译失败' }
    Copy-Item $stubOut $appPlaceholder -Force
    Say ("  占位主程序已生成：{0}" -f $appPlaceholder)
  }

  # ---- 统一补齐负载必备件（占位/真程序走同一条路，保证契约完整）----
  New-Item -ItemType Directory -Force -Path $distApp | Out-Null
  if (!$hasRealApp) {
    Copy-Item (Join-Path $work 'AzurLaneDeskPet.exe') (Join-Path $distApp 'AzurLaneDeskPet.exe') -Force
  }
  # assets 目录（仅当不存在时补占位素材）
  $assetsDir = Join-Path $distApp 'assets'
  if (!(Test-Path $assetsDir)) {
    New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
    Add-Type -AssemblyName System.Drawing
    $png = Join-Path $assetsDir 'ship-placeholder.png'
    $b = New-Object System.Drawing.Bitmap(128, 128)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 0x16, 0x30, 0x5C))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0xE8, 0xC8, 0x6A), 3.0)
    $g.DrawRectangle($pen, 8, 8, 112, 112)
    $g.DrawString('PLACEHOLDER', (New-Object System.Drawing.Font('Consolas', 9)), (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), 12, 56)
    $g.Dispose(); $pen.Dispose(); $b.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    Say '  已补 assets\ship-placeholder.png 占位素材'
  }
  # 使用说明.txt（仅当不存在时补占位）
  $readmePath = Join-Path $distApp '使用说明.txt'
  if (!(Test-Path $readmePath)) {
    $readme = @()
    $readme += '灵工桌宠 v1.0.0 —— 使用说明'
    $readme += ''
    $readme += '【启动】双击安装目录下的 AzurLaneDeskPet.exe，或使用开始菜单 / 桌面快捷方式。'
    $readme += '【程序目录】AzurLaneDeskPet.exe、assets\ 素材目录、install.json（安装清单）。'
    $readme += '【用户数据】%APPDATA%\AzurLaneDeskPet（config.json、logs\、角色素材与导入内容），升级不会丢失。'
    $readme += '【卸载】开始菜单 →「灵工桌宠」→ 卸载，或运行安装目录下的「卸载-灵工桌宠.exe」。'
    $readme += '        静默卸载：卸载-灵工桌宠.exe /S --purge-data --report=<报告文件>'
    $readme += ''
    $readme += '（本文件由 installer\build-setup.ps1 在 dist\app\使用说明.txt 缺失时补写）'
    $readme += ''
    $readme += '灵工桌宠'
    [System.IO.File]::WriteAllLines($readmePath, $readme, (New-Object System.Text.UTF8Encoding($false)))
    Say '  已补 使用说明.txt 基础说明'
  }
  # 同步进 staging（占位模式下 distApp 就是负载目录；真程序模式下 payloadSrc 已复制过）
  if (!$hasRealApp) { robocopy $distApp $stage /E /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null }

  # 卸载器临时占位（真实卸载器紧接着会覆盖它）
  if (!(Test-Path (Join-Path $stage '卸载-灵工桌宠.exe'))) {
    Set-Content -Path (Join-Path $stage '卸载-灵工桌宠.exe') -Value '' -Encoding Ascii
  }

  $stageFiles = (Get-ChildItem $stage -Recurse -File).Count
  Say ("  staging 文件数：{0}" -f $stageFiles)

  # -------------------------------------------------------------------------
  # [2/5] 编译卸载器 → 放进 staging + dist\app +（若有）项目根 app\
  # -------------------------------------------------------------------------
  Say '[2/5] 编译卸载器...'
  New-Item -ItemType Directory -Force -Path $distApp | Out-Null
  Copy-Item $uninstCs (Join-Path $work 'Uninstaller.cs') -Force
  Copy-Item $infoCs (Join-Path $work 'InstallerInfo.cs') -Force
  Copy-Item $icoPath (Join-Path $work 'icon.ico') -Force
  $tmpUninst = Join-Path $work 'Uninstaller.exe'
  & $csc @cscArgs /target:winexe "/out:$tmpUninst" "/win32icon:$(Join-Path $work 'icon.ico')" `
        /define:UNINSTALLER `
        /r:System.Windows.Forms.dll /r:System.Drawing.dll `
        (Join-Path $work 'Uninstaller.cs') (Join-Path $work 'InstallerInfo.cs')
  if (!(Test-Path $tmpUninst)) { Fail '卸载器编译失败' }
  Copy-Item $tmpUninst $uninstOut -Force
  Copy-Item $tmpUninst (Join-Path $stage '卸载-灵工桌宠.exe') -Force
  # 负载来源若是项目根 app\，同步一份过去，保持两边一致
  if ($hasRealApp -and $payloadSrc -ne $distApp) {
    $mirror = Join-Path $payloadSrc '卸载-灵工桌宠.exe'
    Copy-Item $tmpUninst $mirror -Force
    Say ("  已同步到负载目录：{0}" -f $mirror)
  }
  Say ("  完成：{0}（{1:N0} bytes）" -f $uninstOut, (Get-Item $uninstOut).Length)

  # -------------------------------------------------------------------------
  # [3/5] 打包 payload.zip
  # -------------------------------------------------------------------------
  Say '[3/5] 打包 payload.zip...'
  if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
  $zipFiles = ([System.IO.Compression.ZipFile]::OpenRead($zipPath)).Entries.Count
  Say ("  payload.zip {0:N0} bytes，{1} 个条目" -f (Get-Item $zipPath).Length, $zipFiles)

  # -------------------------------------------------------------------------
  # [4/5] 编译安装器
  # -------------------------------------------------------------------------
  Say '[4/5] 编译安装器...'
  Copy-Item $setupCs (Join-Path $work 'DeskPetSetup.cs') -Force
  Copy-Item $infoCs (Join-Path $work 'InstallerInfo.cs') -Force
  Copy-Item $zipPath (Join-Path $work 'payload.zip') -Force
  $tmpSetup = Join-Path $work 'Setup.exe'
  $resArg = "/resource:" + (Join-Path $work 'payload.zip') + ",payload.zip"
  & $csc @cscArgs /target:winexe "/out:$tmpSetup" "/win32icon:$(Join-Path $work 'icon.ico')" `
        $resArg `
        /r:System.Windows.Forms.dll /r:System.Drawing.dll `
        /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
        (Join-Path $work 'DeskPetSetup.cs') (Join-Path $work 'InstallerInfo.cs')
  if (!(Test-Path $tmpSetup)) { Fail '安装器编译失败' }
  Copy-Item $tmpSetup $setupOut -Force
  Say ("  完成：{0}（{1:N0} bytes）" -f $setupOut, (Get-Item $setupOut).Length)

  # -------------------------------------------------------------------------
  # [5/5] 自检
  # -------------------------------------------------------------------------
  if ($SkipSelfTest) {
    Say '[5/5] 已按 -SkipSelfTest 跳过自检'
  } else {
    Say '[5/5] 自检（dry-run，不写盘）...'
    $dryReport = Join-Path $env:TEMP 'alp-build-selftest.txt'
    if (Test-Path $dryReport) { Remove-Item $dryReport -Force }
    $proc = Start-Process -FilePath $setupOut -ArgumentList @('/S', '--dry-run', '--no-launch', '--no-shortcuts', "--report=$dryReport") -Wait -PassThru
    Say ("  dry-run 退出码 = {0}" -f $proc.ExitCode)
    if ($proc.ExitCode -ne 0) { Fail ("自检失败：dry-run 退出码 $($proc.ExitCode)") }
    if (!(Test-Path $dryReport)) { Fail '自检失败：未生成报告文件' }
    Get-Content $dryReport -Encoding UTF8 | Select-Object -First 12 | ForEach-Object { "  | $_" }

    Say '  产物检查：'
    foreach ($p in @($setupOut, $uninstOut, $zipPath, $icoPath)) {
      if (Test-Path $p) { Say ("    OK  {0}  ({1:N0} bytes)" -f $p, (Get-Item $p).Length) }
      else { Fail "自检失败：缺少产物 $p" }
    }
  }

  Say ''
  Say '构建完成：'
  Say ("  安装器  : {0}" -f $setupOut)
  Say ("  卸载器  : {0}" -f $uninstOut)
  Say ("  负载    : {0}" -f $zipPath)
  Say ("  图标    : {0}" -f $icoPath)
}
finally {
  if (!$KeepStaging) {
    try { if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
  } else {
    Say "已保留工作目录：$work"
  }
}
