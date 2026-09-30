$ErrorActionPreference = 'SilentlyContinue'
$name = 'BdsHeadless'
Stop-Service $name -Force
sc.exe delete $name | Out-Null
Get-Process -Name 'bds-headless' | Stop-Process -Force
