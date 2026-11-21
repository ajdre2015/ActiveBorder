if (Get-Process ActiveBorder -ErrorAction SilentlyContinue) { throw 'Close existing ActiveBorder instances before running this test; the script will not terminate a user process.' }
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class S10 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static string ClassOf(IntPtr h) { var s=new StringBuilder(256); GetClassNameW(h,s,s.Capacity); return s.ToString(); }
  public static string TextOf(IntPtr h) { var s=new StringBuilder(256); GetWindowTextW(h,s,s.Capacity); return s.ToString(); }
  public static List<IntPtr> ByClassForProcess(string cls, uint pid) {
    var a=new List<IntPtr>(); EnumWindows((h,l)=>{uint p; GetWindowThreadProcessId(h,out p); if(p==pid && ClassOf(h)==cls) a.Add(h); return true;},IntPtr.Zero); return a;
  }
  public static List<IntPtr> Children(IntPtr h) { var a=new List<IntPtr>(); EnumChildWindows(h,(c,l)=>{a.Add(c);return true;},IntPtr.Zero);return a; }
}
'@

$exe = Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\ActiveBorder.exe'
if (-not (Test-Path $exe)) { throw "Build the Release executable first: $exe" }
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'ActiveBorder'
$keyExisted = Test-Path $runPath
$hadValue = $false
$originalValue = $null
if ($keyExisted) {
  $props = Get-ItemProperty -Path $runPath
  $hadValue = $props.PSObject.Properties.Name -contains $valueName
  if ($hadValue) { $originalValue = $props.$valueName }
}
$app = $null
$failures = 0
function Check($name, $condition, $detail) {
  if ($condition) { Write-Host "PASS $name" -ForegroundColor Green }
  else { Write-Host "FAIL $name — $detail" -ForegroundColor Red; $script:failures++ }
}

try {
  if (-not $keyExisted) { New-Item -Path $runPath -Force | Out-Null }
  Remove-ItemProperty -Path $runPath -Name $valueName -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 300
  $app = Start-Process -FilePath $exe -PassThru
  $tray = [IntPtr]::Zero
  for ($i=0; $i -lt 50 -and $tray -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 100
    $found = [S10]::ByClassForProcess('ActiveBorderTray', [uint32]$app.Id)
    if ($found.Count -eq 1) { $tray = $found[0] }
  }
  Check 'tray window exists' ($tray -ne [IntPtr]::Zero) 'ActiveBorderTray not found'
  if ($tray -ne [IntPtr]::Zero) {
    [void][S10]::PostMessageW($tray, 0x0111, [IntPtr]101, [IntPtr]::Zero)
    $settings = [IntPtr]::Zero
    for ($i=0; $i -lt 50 -and $settings -eq [IntPtr]::Zero; $i++) {
      Start-Sleep -Milliseconds 100
      $found = [S10]::ByClassForProcess('ActiveBorderSettings', [uint32]$app.Id)
      if ($found.Count -eq 1) { $settings = $found[0] }
    }
    Check 'settings command opens startup options' ($settings -ne [IntPtr]::Zero) 'ActiveBorderSettings not found'
    if ($settings -ne [IntPtr]::Zero) {
      $controls = @([S10]::Children($settings))
      $startup = $controls | Where-Object { [S10]::ClassOf($_) -eq 'Button' -and [S10]::TextOf($_) -match 'start with windows' } | Select-Object -First 1
      Check 'startup checkbox is present' ($null -ne $startup) 'no startup checkbox found'
      Check 'startup defaults to disabled' (-not (Get-ItemProperty -Path $runPath -Name $valueName -ErrorAction SilentlyContinue)) 'ActiveBorder Run value unexpectedly exists'
    }
  }
  if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; $app.WaitForExit() }
}
finally {
  if ($app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
  if ($hadValue) { Set-ItemProperty -Path $runPath -Name $valueName -Value $originalValue }
  else { Remove-ItemProperty -Path $runPath -Name $valueName -ErrorAction SilentlyContinue }
  if (-not $keyExisted -and (Test-Path $runPath)) {
    $remaining = (Get-ItemProperty -Path $runPath).PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' }
    if (-not $remaining) { Remove-Item -Path $runPath -Force }
  }
}

if ($failures -eq 0) { Write-Host 'ALL STARTUP CHECKS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$failures STARTUP CHECK(S) FAILED" -ForegroundColor Red
exit 1
