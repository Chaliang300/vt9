param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
$wpf = Join-Path (Split-Path $compiler) 'WPF'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Application icon. assets\VT9Status.ico is exported by the program itself (vector drawn),
# so no binary asset has to be maintained by hand.
$icon = Join-Path $PSScriptRoot 'assets\VT9Status.ico'
$exe = Join-Path $OutputDirectory 'VT9Status.exe'

function Invoke-Compile([string[]]$extra) {
    $arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001',
        "/out:$exe",
        '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xaml.dll',
        "/reference:$wpf\WindowsBase.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\PresentationFramework.dll") + $extra + $sources
    & $compiler $arguments
    return $LASTEXITCODE
}

if (Test-Path -LiteralPath $icon) { $code = Invoke-Compile @("/win32icon:$icon") } else { $code = Invoke-Compile @() }
if ($code -ne 0) { throw "Compilation failed: $code" }

# First build (or the icon was deleted): let the new binary draw the ico, then rebuild with it.
if (-not (Test-Path -LiteralPath $icon)) {
    & $exe '--export-icons' $icon
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path -LiteralPath $icon) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Test-Path -LiteralPath $icon) {
        $code = Invoke-Compile @("/win32icon:$icon")
        if ($code -ne 0) { throw "Compilation failed: $code" }
        Write-Output "Generated $icon"
    } else { Write-Warning 'VT9Status.ico could not be generated; the executable has no application icon.' }
}

if ([IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') -ne $PSScriptRoot.TrimEnd('\')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VT9Status.exe.config') -Destination $OutputDirectory
}
Write-Output "Built $exe"
