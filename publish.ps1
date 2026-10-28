[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish src/WhiteProxy.Admin/WhiteProxy.Admin.csproj -p:PublishProfile=Portable-win-x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish に失敗しました。' }
    Write-Host "単一EXE: $PSScriptRoot/artifacts/publish/win-x64/WhiteProxy.exe"
}
finally { Pop-Location }
