$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework compiler not found' }
$out=Join-Path $root 'dist'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$exe=Join-Path $out 'ghub-repair.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /utf8output "/out:$exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll (Join-Path $root 'src\GhubRepair.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
$test=Join-Path $out 'tests.exe'
& $csc /nologo /target:exe /utf8output "/out:$test" "/reference:$exe" (Join-Path $root 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& $test
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
$package=Join-Path $out 'ghub-repair-v1.0.0-windows'
New-Item -ItemType Directory -Path $package -Force | Out-Null
foreach ($name in @('README.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $package -Force }
Copy-Item -LiteralPath $exe -Destination $package -Force
New-Item -ItemType Directory -Path (Join-Path $package 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'docs\missing-required-files.png') -Destination (Join-Path $package 'docs') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\VALIDATION.md') -Destination (Join-Path $package 'docs') -Force
Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $out 'ghub-repair-v1.0.0-windows.zip') -Force
Get-FileHash -LiteralPath $exe,(Join-Path $out 'ghub-repair-v1.0.0-windows.zip') -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ASCII
