# 长时间连续操作的复现装置：按固定顺序反复触发用户平时会做的动作，
# 目的不是"跑通"，而是把卡死复现出来。配合 tools\probe.ps1 一起开：
#   终端 A: powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe.ps1 -Seconds 1800
#   终端 B: powershell -NoProfile -ExecutionPolicy Bypass -File tools\soak.ps1 -Minutes 30
# 每一轮都打印耗时，某一轮突然变慢/不动就是卡死开始。
param(
  [int]$Minutes = 30,
  [int]$RoundSeconds = 600,
  [string]$Log = "$env:TEMP\dsh-pet-soak.log"
)

Add-Type -Namespace Soak -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, System.IntPtr extra);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int cmd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam, uint flags, uint timeout, out System.IntPtr result);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);
public delegate bool EnumProc(System.IntPtr hWnd, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, System.IntPtr lParam);
'@

$LEFTDOWN = 0x0002; $LEFTUP = 0x0004; $RIGHTDOWN = 0x0008; $RIGHTUP = 0x0010

function Log([string]$line) {
  $stamp = (Get-Date).ToString('HH:mm:ss.fff')
  "$stamp  $line" | Tee-Object -FilePath $Log -Append | Out-Null
}

function Get-Runtime {
  $path = Join-Path $env:USERPROFILE '.dsh\pets\runtime.json'
  if (-not (Test-Path $path)) { return $null }
  try { return Get-Content $path -Raw | ConvertFrom-Json } catch { return $null }
}

$runtime = Get-Runtime
if (-not $runtime) { Write-Error '找不到 ~/.dsh/pets/runtime.json'; exit 1 }
$stateUrl = "$($runtime.host)$($runtime.prefix)/state"
$sayUrl = "$($runtime.host)$($runtime.prefix)/say"
$token = $runtime.token
$headers = @{ 'X-Pet-Token' = $token }

$dsh = Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue |
  Where-Object { $_.MainWindowHandle -ne [System.IntPtr]::Zero } | Select-Object -First 1
if (-not $dsh) { Write-Error '找不到 DSH 主窗口'; exit 1 }

# 浮层是 WS_EX_LAYERED | TOOLWINDOW | NOACTIVATE 的窗口，Process.MainWindowHandle 恒为 0，
# 必须自己 EnumWindows 按 pid 找（这个坑让第一版 soak 一直在"浮层不见了"里打转）。
function Find-WindowByPid([int]$WantPid) {
  $found = [System.IntPtr]::Zero
  $callback = [Soak.Win+EnumProc]{
    param([System.IntPtr]$hWnd, [System.IntPtr]$lParam)
    $owner = 0
    $null = [Soak.Win]::GetWindowThreadProcessId($hWnd, [ref]$owner)
    if ($owner -eq $WantPid -and [Soak.Win]::IsWindowVisible($hWnd)) {
      $script:foundHwnd = $hWnd
      return $false
    }
    return $true
  }
  $script:foundHwnd = [System.IntPtr]::Zero
  $null = [Soak.Win]::EnumWindows($callback, [System.IntPtr]::Zero)
  return $script:foundHwnd
}

function Get-PetRect {
  $pet = Get-Process -Name 'PetOverlay' -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $pet) { return $null }
  $hwnd = Find-WindowByPid $pet.Id
  if ($hwnd -eq [System.IntPtr]::Zero) { return $null }
  $rect = New-Object 'Soak.Win+RECT'
  if (-not [Soak.Win]::GetWindowRect($hwnd, [ref]$rect)) { return $null }
  return @{ Proc = $pet; Rect = $rect; Hwnd = $hwnd }
}

function Move-To([int]$x, [int]$y) { $null = [Soak.Win]::SetCursorPos($x, $y) }

function Click([string]$button = 'left') {
  if ($button -eq 'right') {
    [Soak.Win]::mouse_event($RIGHTDOWN, 0, 0, 0, [System.IntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [Soak.Win]::mouse_event($RIGHTUP, 0, 0, 0, [System.IntPtr]::Zero)
  } else {
    [Soak.Win]::mouse_event($LEFTDOWN, 0, 0, 0, [System.IntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [Soak.Win]::mouse_event($LEFTUP, 0, 0, 0, [System.IntPtr]::Zero)
  }
}

function Press-Esc {
  Add-Type -AssemblyName System.Windows.Forms
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
}

function Drag-Once($info) {
  $r = $info.Rect
  $fromX = [int](($r.Left + $r.Right) / 2)
  $fromY = [int](($r.Top + $r.Bottom) / 2)
  $toX = $fromX - 220
  $toY = $fromY + 60
  Move-To $fromX $fromY
  Start-Sleep -Milliseconds 60
  [Soak.Win]::mouse_event($LEFTDOWN, 0, 0, 0, [System.IntPtr]::Zero)
  for ($i = 1; $i -le 12; $i++) {
    Move-To ([int]($fromX + ($toX - $fromX) * $i / 12)) ([int]($fromY + ($toY - $fromY) * $i / 12))
    Start-Sleep -Milliseconds 12
  }
  [Soak.Win]::mouse_event($LEFTUP, 0, 0, 0, [System.IntPtr]::Zero)
}

function Get-State {
  try { return Invoke-RestMethod -Uri $stateUrl -Headers $headers -TimeoutSec 5 } catch { return $null }
}

Log ("soak start minutes=$Minutes roundSeconds=$RoundSeconds state=$stateUrl")
$rounds = [Math]::Max(1, [int][Math]::Ceiling($Minutes * 60 / $RoundSeconds))

for ($round = 1; $round -le $rounds; $round++) {
  $roundStart = Get-Date
  $ops = 0
  Log "round $round 开始"
  while (((Get-Date) - $roundStart).TotalSeconds -lt $RoundSeconds) {
    $info = Get-PetRect
    if (-not $info) { Log '浮层不见了，等 3 秒'; Start-Sleep -Seconds 3; continue }
    $r = $info.Rect
    $cx = [int](($r.Left + $r.Right) / 2)
    $cy = [int](($r.Top + $r.Bottom) / 2)

    # 1) 悬停进出：动作行随悬停出现/收起
    Move-To $cx $cy; Start-Sleep -Milliseconds 300
    Move-To ($cx + 260) ($cy + 120); Start-Sleep -Milliseconds 300
    $ops++

    # 2) 右键菜单开→Esc
    Move-To $cx $cy; Start-Sleep -Milliseconds 120
    Click 'right'; Start-Sleep -Milliseconds 400
    Press-Esc; Start-Sleep -Milliseconds 200
    $ops++

    # 3) ✎ 写作窗开→关
    Move-To $cx $cy; Start-Sleep -Milliseconds 200
    Move-To ([int]($cx - 19)) ([int]($r.Bottom + 18)); Start-Sleep -Milliseconds 120
    Click 'left'; Start-Sleep -Milliseconds 500
    Press-Esc; Start-Sleep -Milliseconds 200
    $ops++

    # 4) 🎤 语音窗开→关（每次都会走一遍 SAPI 启动）
    Move-To $cx $cy; Start-Sleep -Milliseconds 200
    Move-To ([int]($cx + 19)) ([int]($r.Bottom + 18)); Start-Sleep -Milliseconds 120
    Click 'left'; Start-Sleep -Milliseconds 700
    Press-Esc; Start-Sleep -Milliseconds 200
    $ops++

    # 5) 拖一次
    Drag-Once $info; Start-Sleep -Milliseconds 250
    $ops++

    # 6) 连发两条 /say（走 sessionController.prompt，最容易碰会话状态）
    foreach ($i in 1..2) {
      try {
        $body = @{ text = "soak $round-$ops-$i：自动压测消息，可忽略" } | ConvertTo-Json -Compress
        $null = Invoke-WebRequest -Uri $sayUrl -Method POST -Headers $headers -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 20
      } catch {
        Log "say 失败：$($_.Exception.Message)"
      }
    }

    # 7) 最小化/还原 DSH
    $null = [Soak.Win]::ShowWindow($dsh.MainWindowHandle, 6)
    Start-Sleep -Milliseconds 700
    $null = [Soak.Win]::ShowWindow($dsh.MainWindowHandle, 9)
    Start-Sleep -Milliseconds 400
    $ops++

    $state = Get-State
    if (-not $state) { Log 'state 取不到（宿主可能被卡住）' }
  }
  Log ("round $round 结束，操作 $ops 组，用时 {0:N0}s" -f ((Get-Date) - $roundStart).TotalSeconds)
}

Log 'soak done'