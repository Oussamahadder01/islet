# Builds Islet with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK, NuGet or other download is needed.
$ErrorActionPreference = 'Stop'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$winmd = Join-Path $env:WINDIR 'System32\WinMetadata'
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
    "$fw\WPF\PresentationFramework.dll",
    "$fw\WPF\PresentationCore.dll",
    "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll",
    "$fw\System.Windows.Forms.dll",
    "$fw\System.Drawing.dll",
    "$fw\System.Web.Extensions.dll",
    "$fw\System.Runtime.WindowsRuntime.dll",
    "$fw\System.Runtime.InteropServices.WindowsRuntime.dll",
    "$fw\System.Runtime.dll",
    "$fw\System.Threading.Tasks.dll",
    "$winmd\Windows.Foundation.winmd",
    "$winmd\Windows.Media.winmd",
    "$winmd\Windows.Devices.winmd",
    "$winmd\Windows.Storage.winmd"
) | ForEach-Object { "/reference:$_" }

$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /warn:4 "/out:$out\Islet.exe" @refs @sources
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "Built $out\Islet.exe"
