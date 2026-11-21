$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot '..\artifacts\publish\ActiveBorder.exe'
if (-not (Test-Path $exe)) { throw "Publish the app first: $exe" }

$subKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'ActiveBorder'
$sentinelName = "ActiveBorder.CleanupTest.$PID"
$baseKey = [Microsoft.Win32.Registry]::CurrentUser
$key = $baseKey.OpenSubKey($subKeyPath, $false)
$keyExisted = $null -ne $key
$hadValue = $false
$originalValue = $null
$originalValueKind = [Microsoft.Win32.RegistryValueKind]::String
$hadSentinel = $false
$originalSentinel = $null
$originalSentinelKind = [Microsoft.Win32.RegistryValueKind]::String
if ($keyExisted) {
  try {
    $hadValue = $key.GetValueNames() -contains $valueName
    if ($hadValue) {
      $originalValueKind = $key.GetValueKind($valueName)
      $originalValue = $key.GetValue($valueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    $hadSentinel = $key.GetValueNames() -contains $sentinelName
    if ($hadSentinel) {
      $originalSentinelKind = $key.GetValueKind($sentinelName)
      $originalSentinel = $key.GetValue($sentinelName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
  }
  finally { $key.Dispose() }
}

try {
  $key = $baseKey.CreateSubKey($subKeyPath)
  $key.SetValue($valueName, 'cleanup-test-value', [Microsoft.Win32.RegistryValueKind]::String)
  $key.SetValue($sentinelName, 'must-remain-untouched', [Microsoft.Win32.RegistryValueKind]::String)
  $key.Dispose()

  $process = Start-Process -FilePath $exe -ArgumentList '--uninstall-cleanup' -Wait -PassThru
  if ($process.ExitCode -ne 0) { throw "Cleanup command exited with code $($process.ExitCode)." }

  $key = $baseKey.OpenSubKey($subKeyPath, $false)
  if ($null -ne $key) {
    try {
      if ($key.GetValueNames() -contains $valueName) { throw 'ActiveBorder Run value was not removed.' }
      if ($key.GetValue($sentinelName) -ne 'must-remain-untouched') { throw 'Cleanup changed another Run value.' }
    }
    finally { $key.Dispose() }
  }
  else { throw 'Cleanup removed the entire Run key.' }

  Write-Host 'PASS uninstall cleanup removes only the ActiveBorder Run value' -ForegroundColor Green
}
finally {
  $key = $baseKey.CreateSubKey($subKeyPath)
  try {
    if ($hadValue) { $key.SetValue($valueName, $originalValue, $originalValueKind) }
    else { $key.DeleteValue($valueName, $false) }
    if ($hadSentinel) { $key.SetValue($sentinelName, $originalSentinel, $originalSentinelKind) }
    else { $key.DeleteValue($sentinelName, $false) }
    $remainingValues = $key.GetValueNames().Length
  }
  finally { $key.Dispose() }
  if (-not $keyExisted -and $remainingValues -eq 0) { $baseKey.DeleteSubKey($subKeyPath, $false) }
}
