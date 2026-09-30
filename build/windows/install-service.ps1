param(
    [Parameter(Mandatory)] [string] $InstallDir,
    [switch] $AutoStart
)
$ErrorActionPreference = 'Stop'
$name = 'BdsHeadless'
$exe = Join-Path $InstallDir 'bds-headless-service.exe'

$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service $name -Force }
    sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
}

$start = if ($AutoStart) { 'auto' } else { 'demand' }
sc.exe create $name binPath= "`"$exe`"" start= $start obj= "NT SERVICE\$name" DisplayName= "BDS Headless Client" | Out-Null
sc.exe description $name "Lets Xbox and PlayStation friends join a Minecraft Bedrock server." | Out-Null
sc.exe sidtype $name unrestricted | Out-Null
sc.exe failure $name reset= 86400 actions= restart/10000/restart/10000/restart/60000 | Out-Null

$sid = (New-Object System.Security.Principal.NTAccount("NT SERVICE\$name")).Translate([System.Security.Principal.SecurityIdentifier]).Value

# Defaults, plus: Users may start/stop (RP/WP), the service may change its own start type (DC).
$sddl = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' +
        '(A;;CCLCSWRPWPLOCRRC;;;BU)' +
        "(A;;CCDCLCSWRPWPLOCRRC;;;$sid)" +
        'S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)'
sc.exe sdset $name $sddl | Out-Null

# Data (encrypted token, database) readable only by the service, SYSTEM and admins.
$data = Join-Path $env:ProgramData 'BdsHeadless'
New-Item -ItemType Directory -Force -Path $data | Out-Null
icacls $data /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*${sid}:(OI)(CI)F" | Out-Null

Start-Service $name
