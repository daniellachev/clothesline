$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compilerPath = Join-Path $frameworkDir 'csc.exe'
$wpfDir = Join-Path $frameworkDir 'WPF'
New-Item -ItemType Directory -Path (Join-Path $projectDir 'bin') -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll','System.Security.dll','System.Xml.dll','System.Xaml.dll')
$compileArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:' + (Join-Path $projectDir 'bin/Clothesline.exe')),('/win32manifest:' + (Join-Path $projectDir 'app.manifest')))
foreach ($reference in $references) { $compileArgs += '/reference:' + (Join-Path $frameworkDir $reference) }
foreach ($reference in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) { $compileArgs += '/reference:' + (Join-Path $wpfDir $reference) }
$compileArgs += @(Get-ChildItem -LiteralPath $projectDir -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compilerPath @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Clothesline build failed.' }
Write-Output (Join-Path $projectDir 'bin/Clothesline.exe')
