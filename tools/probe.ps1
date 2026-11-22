# 卡死取证探针：每 N 毫秒同时测三件事，落成 CSV。
#   1) DSH 主窗口的 UI 线程是否在 1 秒内回过 WM_NULL（回不了 = 界面真的卡住）
#   2) 宠物浮层自己的 UI 线程是否在 1 秒内回过 WM_NULL
#   3) 宿主插件 /state 的 HTTP 延迟
# 三条曲线分开记，卡死时一眼能看出是 DSH 卡、浮层卡，还是宿主卡。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe.ps1 -Seconds 600
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe.ps1 -Seconds 600 -Out C:\tmp\probe.csv
param(
  [int]$Seconds = 600,
  [int]$IntervalMs = 200,
  [int]$TimeoutMs = 1000,
  [string]$Out = "$env:TEMP\dsh-pet-probe.csv"
)

Add-Type -Namespace Probe -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.IntPtr wParam,
    System.IntPtr lParam, uint fuFlags, uint uTimeout, out System.IntPtr lpdwResult);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);
public delegate bool EnumProc(System.IntPtr hWnd, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, System.IntPtr lParam);
'@

# 浮层是 TOOLWINDOW + NOACTIVATE，Process.MainWindowHandle 恒为 0，只能按 pid 枚举窗口。
function Find-WindowByPid([int]$WantPid) {
  $script:foundHwnd = [System.IntPtr]::Zero
  $callback = [Probe.Win+EnumProc]{
    param([System.IntPtr]$hWnd, [System.IntPtr]$lParam)
    $owner = 0
    $null = [Probe.Win]::GetWindowThreadProcessId($hWnd, [ref]$owner)
    if ($owner -eq $WantPid -and [Probe.Win]::IsWindowVisible($hWnd)) {
      $script:foundHwnd = $hWnd
      return $false
    }
    return $true
  }
  $null = [Probe.Win]::EnumWindows($callback, [System.IntPtr]::Zero)
  return $script:foundHwnd
}

function Measure-Ui {
  param([System.IntPtr]$Hwnd, [int]$Timeout)
  if ($Hwnd -eq [System.IntPtr]::Zero) { return -1 }
  $result = [System.IntPtr]::Zero
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  # SMTO_ABORTIFHUNG(0x2) | SMTO_BLOCK(0x1)：超时就返回 0，不会把探针自己卡死
  $ok = [Probe.Win]::SendMessageTimeout($Hwnd, 0, [System.IntPtr]::Zero, [System.IntPtr]::Zero, 3, $Timeout, [ref]$result)
  $sw.Stop()
  if ($ok -eq [System.IntPtr]::Zero) { return -2 }
  return [int]$sw.ElapsedMilliseconds
}

function Get-PetRuntime {
  $path = Join-Path $env:USERPROFILE '.dsh\pets\runtime.json'
  if (-not (Test-Path $path)) { return $null }
  try { return Get-Content $path -Raw | ConvertFrom-Json } catch { return $null }
}

$runtime = Get-PetRuntime
if (-not $runtime) { Write-Error '找不到 ~/.dsh/pets/runtime.json，宠物插件没装好？'; exit 1 }
$stateUrl = "$($runtime.host)$($runtime.prefix)/state"
$token = $runtime.token

$dsh = Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue |
  Where-Object { $_.MainWindowHandle -ne [System.IntPtr]::Zero } |
  Select-Object -First 1
if (-not $dsh) { Write-Error '找不到 DSH 主窗口'; exit 1 }
$pet = Get-Process -Name 'PetOverlay' -ErrorAction SilentlyContinue | Select-Object -First 1
$petHwnd = if ($pet) { Find-WindowByPid $pet.Id } else { [System.IntPtr]::Zero }

Write-Host ("DSH hwnd={0} pid={1} | 浮层 pid={2} hwnd={3} | state={4}" -f $dsh.MainWindowHandle, $dsh.Id, ($pet.Id), $petHwnd, $stateUrl)
if (Test-Path $Out) { Remove-Item $Out -Force }
'ts,ui_dsh_ms,ui_pet_ms,http_ms,dsh_alive,pet_alive' | Set-Content -Path $Out -Encoding UTF8

$deadline = (Get-Date).AddSeconds($Seconds)
$rows = 0
while ((Get-Date) -lt $deadline) {
  $loopStart = Get-Date

  $dshNow = Get-Process -Id $dsh.Id -ErrorAction SilentlyContinue
  $dshAlive = if ($dshNow) { 1 } else { 0 }
  $petNow = Get-Process -Name 'PetOverlay' -ErrorAction SilentlyContinue | Select-Object -First 1
  $petAlive = if ($petNow) { 1 } else { 0 }

  $uiDsh = Measure-Ui -Hwnd $dsh.MainWindowHandle -Timeout $TimeoutMs
  $uiPet = if ($petNow) { Measure-Ui -Hwnd (Find-WindowByPid $petNow.Id) -Timeout $TimeoutMs } else { -1 }

  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  try {
    $null = Invoke-WebRequest -Uri $stateUrl -Headers @{ 'X-Pet-Token' = $token } -UseBasicParsing -TimeoutSec 5
    $http = [int]$sw.ElapsedMilliseconds
  } catch {
    $http = -2
  }
  $sw.Stop()

  $ts = (Get-Date).ToString('HH:mm:ss.fff')
  "$ts,$uiDsh,$uiPet,$http,$dshAlive,$petAlive" | Add-Content -Path $Out -Encoding UTF8
  $rows += 1

  $spent = ((Get-Date) - $loopStart).TotalMilliseconds
  $sleep = [Math]::Max(0, $IntervalMs - $spent)
  Start-Sleep -Milliseconds $sleep
}

Write-Host "完成：$rows 行 → $Out"
$data = Import-Csv $Out
$badDsh = @($data | Where-Object { [int]$_.ui_dsh_ms -lt 0 }).Count
$badHttp = @($data | Where-Object { [int]$_.http_ms -lt 0 }).Count
$maxDsh = ($data | ForEach-Object { [int]$_.ui_dsh_ms } | Measure-Object -Maximum).Maximum
$maxHttp = ($data | ForEach-Object { [int]$_.http_ms } | Measure-Object -Maximum).Maximum
Write-Host ("DSH 无响应 {0} 次（最长 {1}ms）| HTTP 失败 {2} 次（最长 {3}ms）" -f $badDsh, $maxDsh, $badHttp, $maxHttp)