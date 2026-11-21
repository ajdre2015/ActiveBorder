if (Get-Process ActiveBorder -ErrorAction SilentlyContinue) { throw 'Close existing ActiveBorder instances before running this test; the script will not terminate a user process.' }
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class S9 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

  public struct S9Rect { public int Left, Top, Right, Bottom; }
  public struct S9Point { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out S9Rect r);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out S9Rect r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref S9Point p);
  public static bool ActionButtonsFit(IntPtr parent) {
    var buttons = new List<IntPtr>();
    EnumChildWindows(parent, (h,l) => { var text=TextOf(h); if(text=="Save" || text=="Cancel") buttons.Add(h); return true; }, IntPtr.Zero);
    if(buttons.Count != 2) return false;
    S9Rect client;
    S9Point origin = new S9Point();
    if(!GetClientRect(parent,out client) || !ClientToScreen(parent,ref origin)) return false;
    foreach(var button in buttons) {
      S9Rect rect;
      if(!GetWindowRect(button,out rect) || rect.Left<origin.X || rect.Top<origin.Y || rect.Right>origin.X+client.Right || rect.Bottom>origin.Y+client.Bottom) return false;
    }
    return true;
  }
  public static string ClassOf(IntPtr h) { var s=new StringBuilder(256); GetClassNameW(h,s,s.Capacity); return s.ToString(); }
  public static string TextOf(IntPtr h) { var s=new StringBuilder(256); GetWindowTextW(h,s,s.Capacity); return s.ToString(); }
  public static List<IntPtr> ByClassForProcess(string cls, uint pid) {
    var a=new List<IntPtr>(); EnumWindows((h,l)=>{uint p; GetWindowThreadProcessId(h,out p); if(p==pid && ClassOf(h)==cls) a.Add(h); return true;},IntPtr.Zero); return a;
  }
  public static List<string> ChildTexts(IntPtr h) {
    var a=new List<string>(); EnumChildWindows(h,(c,l)=>{a.Add(ClassOf(c)+":"+TextOf(c));return true;},IntPtr.Zero); return a;
  }
}
'@

$exe = Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\ActiveBorder.exe'
if (-not (Test-Path $exe)) { throw "Build the Release executable first: $exe" }
$settingsPath = Join-Path $env:LOCALAPPDATA 'ActiveBorder\settings.json'
$settingsDir = Split-Path $settingsPath
$hadSettings = Test-Path $settingsPath
$originalSettings = if ($hadSettings) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runBefore = (Get-ItemProperty -Path $runPath -Name ActiveBorder -ErrorAction SilentlyContinue).ActiveBorder
$app = $null
$failures = 0
function Check($name, $condition, $detail) {
  if ($condition) { Write-Host "PASS $name" -ForegroundColor Green }
  else { Write-Host "FAIL $name — $detail" -ForegroundColor Red; $script:failures++ }
}

try {
  New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
  [IO.File]::WriteAllText($settingsPath, '{"version":1,"followSystemAccent":true,"customColor":"#FF0078D4","borderWidth":5}', [Text.UTF8Encoding]::new($false))
  $fixtureBytes = [IO.File]::ReadAllBytes($settingsPath)
  Start-Sleep -Milliseconds 300
  $app = Start-Process -FilePath $exe -PassThru
  $tray = [IntPtr]::Zero
  for ($i=0; $i -lt 50 -and $tray -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 100
    $found = [S9]::ByClassForProcess('ActiveBorderTray', [uint32]$app.Id)
    if ($found.Count -eq 1) { $tray = $found[0] }
  }
  Check 'tray control window exists' ($tray -ne [IntPtr]::Zero) 'ActiveBorderTray not found'
  if ($tray -ne [IntPtr]::Zero) {
    [void][S9]::PostMessageW($tray, 0x0111, [IntPtr]101, [IntPtr]::Zero)
    $windows = @()
    for ($i=0; $i -lt 50 -and $windows.Count -eq 0; $i++) { Start-Sleep -Milliseconds 100; $windows = @([S9]::ByClassForProcess('ActiveBorderSettings', [uint32]$app.Id)) }
    Check 'settings command opens one settings window' ($windows.Count -eq 1) "found $($windows.Count) settings windows"
    if ($windows.Count -eq 1) {
      $first = $windows[0]
      [void][S9]::PostMessageW($tray, 0x0111, [IntPtr]101, [IntPtr]::Zero)
      Start-Sleep -Milliseconds 300
      $again = @([S9]::ByClassForProcess('ActiveBorderSettings', [uint32]$app.Id))
      Check 'repeated settings command reuses the same window' ($again.Count -eq 1 -and $again[0] -eq $first) "window count=$($again.Count)"
      $children = @([S9]::ChildTexts($first))
      Check 'settings window exposes expected native controls' ($children.Count -ge 6) ($children -join '; ')

      Check 'save and cancel buttons fit inside the settings window' ([S9]::ActionButtonsFit($first)) 'action buttons are clipped by the client area'
      [void][S9]::PostMessageW($first, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
      Start-Sleep -Milliseconds 200
      Check 'closing settings does not exit the app' (-not $app.HasExited) 'application exited'
      $settingsAfterClose = [IO.File]::ReadAllBytes($settingsPath)
      $runAfterClose = (Get-ItemProperty -Path $runPath -Name ActiveBorder -ErrorAction SilentlyContinue).ActiveBorder
      Check 'closing discards settings without changing persisted fixture' ([Convert]::ToBase64String($fixtureBytes) -ceq [Convert]::ToBase64String($settingsAfterClose)) 'settings.json changed after closing'
      Check 'closing leaves the Run value unchanged' ([string]::Equals([string]$runBefore, [string]$runAfterClose, [StringComparison]::Ordinal)) "before='$runBefore' after='$runAfterClose'"

      Start-Sleep -Milliseconds 150
      Check 'closing hides the settings window' (-not [S9]::IsWindowVisible($first)) 'settings window remained visible'
      [void][S9]::PostMessageW($tray, 0x8001, [IntPtr]::Zero, [IntPtr]0x0203)
      $doubleClickVisible = $false
      for ($i = 0; $i -lt 50 -and -not $doubleClickVisible; $i++) { Start-Sleep -Milliseconds 100; $doubleClickVisible = [S9]::IsWindowVisible($first) }
      Check 'double-clicking the tray icon opens settings' $doubleClickVisible 'settings window stayed hidden'
    }
  }
  [void][S9]::PostMessageW(([S9]::ByClassForProcess('ActiveBorderTray', [uint32]$app.Id) | Select-Object -First 1), 0x0111, [IntPtr]100, [IntPtr]::Zero)
  [void]$app.WaitForExit(5000)
}
finally {
  if ($app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
  if ($hadSettings) { [IO.File]::WriteAllBytes($settingsPath, $originalSettings) }
  elseif (Test-Path $settingsPath) { Remove-Item $settingsPath -Force }
}

if ($failures -eq 0) { Write-Host 'ALL SETTINGS CHECKS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$failures SETTINGS CHECK(S) FAILED" -ForegroundColor Red
exit 1
