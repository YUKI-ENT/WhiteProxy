[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LocalAddress,
    [Parameter(Mandatory)][string[]]$RemoteAddress,
    [ValidateRange(1,65535)][int]$Port = 3128
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '管理者PowerShellで実行してください。'
}
$localIp = [Net.IPAddress]::Parse($LocalAddress)
if ($localIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $LocalAddress -eq '0.0.0.0') { throw '待受する特定のIPv4アドレスを指定してください。' }
foreach ($range in $RemoteAddress) {
    $parts = $range.Split('/')
    $remoteIp = [Net.IPAddress]::Parse($parts[0])
    if ($remoteIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $parts.Length -gt 2 -or
        ($parts.Length -eq 2 -and ([int]$parts[1] -lt 1 -or [int]$parts[1] -gt 32))) { throw '送信元はIPv4または/1〜/32のCIDRで限定してください。' }
}
$executable = Join-Path $env:ProgramFiles 'WhiteProxy/WhiteProxy.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '先に発行EXEからWindowsサービスを登録してください。' }
$name = 'WhiteProxy-Scoped-Inbound'
if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) { throw '同名ルールが存在します。既存ルールを確認してから変更してください。' }
New-NetFirewallRule -Name $name -DisplayName 'WhiteProxy 管理VLANからの接続' -Direction Inbound -Action Allow `
    -Protocol TCP -LocalAddress $LocalAddress -LocalPort $Port -RemoteAddress $RemoteAddress `
    -Program $executable -Profile Domain,Private -EdgeTraversalPolicy Block
