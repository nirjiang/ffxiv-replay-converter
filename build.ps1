param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '找不到 Windows 的 .NET Framework C# 編譯器。' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$arguments = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/codepage:65001',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll',
    ('/resource:' + (Join-Path $PSScriptRoot 'profile.json') + ',ConverterProfile.json'),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/win32icon:' + (Join-Path $PSScriptRoot 'app.ico')),
    ('/out:' + (Join-Path $OutputDirectory 'FF14回顧轉換工具.exe')),
    (Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'Updater.cs'), (Join-Path $PSScriptRoot 'UpdateTests.cs'), (Join-Path $PSScriptRoot 'GameClient.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw '編譯失敗。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用說明.txt'), (Join-Path $PSScriptRoot 'LICENSE-opcodediff.txt'), (Join-Path $PSScriptRoot 'LICENSE-FFXIVOpcodes.txt') -Destination $OutputDirectory -Force
Write-Output (Join-Path $OutputDirectory 'FF14回顧轉換工具.exe')
