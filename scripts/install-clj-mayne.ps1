#!/usr/bin/env pwsh
# Build Clojure.Main and install it as a global .NET tool (default name: clj-mayne)
# with startup matching the branch's sub-500ms target.
#
# Pipeline:
#   1. Build Clojure.Main      - produces the tool exe / dependencies.
#   2. Build Clojure.Compile   - its PostBuild AOT-compiles ~30 runtime
#                                namespaces to clojure.*.clj.dll and copies
#                                them into Clojure.Main's bin output. Without
#                                this, startup source-loads (~3s, master speed).
#   3. dotnet pack             - bundles bin into a nupkg.
#   4. dotnet tool install     - installs the nupkg as a global tool.
#   5. Touch .clj*.dll         - bumps timestamps in the install dir so
#                                clojure.lang.RT.load picks the AOT'd assembly
#                                over the .clj source. NuGet's extract order
#                                leaves sources newer than DLLs by a few ms,
#                                which silently triggers source loading.
#
# R2R note: the research/scripts/readytorun-generated-clj-dlls.zsh approach was
# tried here too, but crossgen2'd .clj.dll files lose the initializer
# Clojure.Main looks for ("Cannot find initializer for clojure.core.protocols.clj").
# IL-only AOT'd namespaces already get the tool to ~450ms cold start on Windows,
# so we skip R2R. Run with -EnableR2R to attempt it anyway (expect breakage).
#
# Usage:
#   pwsh ./scripts/install-clj-mayne.ps1
#   pwsh ./scripts/install-clj-mayne.ps1 -Framework net10.0 -ToolName clj-mayne

[CmdletBinding()]
param(
    [string]$Framework = 'net10.0',
    [string]$Configuration = 'Release',
    [string]$ToolName = 'clj-mayne',
    [string]$PackageId = 'clj-mayne',
    [string]$Rid,
    [switch]$EnableR2R
)

$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$mainProj     = Join-Path $repoRoot 'Clojure/Clojure.Main/Clojure.Main.csproj'
$compileProj  = Join-Path $repoRoot 'Clojure/Clojure.Compile/Clojure.Compile.csproj'
$packOutDir   = Join-Path $repoRoot "Clojure/Clojure.Main/bin/$Configuration"
$binDir       = Join-Path $packOutDir $Framework

if (-not (Test-Path $mainProj))    { throw "Project not found: $mainProj" }
if (-not (Test-Path $compileProj)) { throw "Project not found: $compileProj" }

function Invoke-Checked {
    param([string]$Label, [scriptblock]$Action)
    Write-Host "==> $Label" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed (exit $LASTEXITCODE)" }
}

# 1. Build Clojure.Main
Invoke-Checked "Build Clojure.Main ($Framework, $Configuration)" {
    dotnet build -f $Framework -c $Configuration -p:TargetFrameworks=$Framework $mainProj
}

# 2. Build Clojure.Compile (PostBuild generates clojure.*.clj.dll into $binDir)
Invoke-Checked "Build Clojure.Compile (generates clojure.*.clj.dll)" {
    dotnet build -f $Framework -c $Configuration -p:TargetFrameworks=$Framework $compileProj
}

$cljDlls = @(Get-ChildItem $binDir -Filter 'clojure.*.clj.dll' -ErrorAction SilentlyContinue)
if ($cljDlls.Count -eq 0) {
    throw "No clojure.*.clj.dll found in $binDir after Clojure.Compile build"
}
Write-Host "    $($cljDlls.Count) generated namespace DLLs in $binDir"

# Optional R2R (opt-in - currently breaks initializer loading on Windows)
if ($EnableR2R) {
    if (-not $Rid) {
        $Rid = (dotnet --info | Select-String '^\s*RID:\s*(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1)
        if (-not $Rid) { throw "Could not determine RID; pass -Rid explicitly" }
    }
    $crossgenPkg = Join-Path $env:USERPROFILE ".nuget/packages/microsoft.netcore.app.crossgen2.$($Rid.ToLower())"
    $crossgen = $null
    if (Test-Path $crossgenPkg) {
        $crossgen = Get-ChildItem $crossgenPkg -Recurse -Filter 'crossgen2.exe' -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1
    }
    if (-not $crossgen) {
        Invoke-Checked "Acquire crossgen2 ($Rid) via dotnet publish" {
            dotnet publish -c $Configuration -r $Rid --self-contained false `
                -p:PublishReadyToRun=true -p:TargetFrameworks=$Framework -f $Framework `
                $mainProj | Out-Host
        }
        $crossgen = Get-ChildItem $crossgenPkg -Recurse -Filter 'crossgen2.exe' -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1
    }
    if (-not $crossgen) { throw "crossgen2.exe not found under $crossgenPkg" }

    $tfmMajor = ($Framework -replace '^net','') -replace '\..*$',''
    $runtimeLine = dotnet --list-runtimes |
        Where-Object { $_ -match "^Microsoft\.NETCore\.App\s+$tfmMajor\." } |
        Select-Object -Last 1
    if (-not $runtimeLine) { throw "No Microsoft.NETCore.App $tfmMajor.x runtime installed" }
    $runtimeVersion = ($runtimeLine -split '\s+')[1]
    $runtimeBase = ([regex]'\[(.+)\]').Match($runtimeLine).Groups[1].Value
    $runtimeDir = Join-Path $runtimeBase $runtimeVersion

    Write-Host "==> ReadyToRun via $($crossgen.FullName)" -ForegroundColor Cyan
    Write-Host "    WARNING: R2R may break .clj.dll initializer loading" -ForegroundColor Yellow
    $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("clojure-clr-r2r." + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmpDir | Out-Null
    try {
        foreach ($input in $cljDlls) {
            $refs = @()
            Get-ChildItem $binDir -Filter '*.dll' | Where-Object { $_.Name -ne $input.Name } | ForEach-Object { $refs += @('-r', $_.FullName) }
            Get-ChildItem $runtimeDir -Filter '*.dll' | ForEach-Object { $refs += @('-r', $_.FullName) }
            & $crossgen.FullName -o (Join-Path $tmpDir $input.Name) @refs $input.FullName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "crossgen2 failed for $($input.Name)" }
        }
        Get-ChildItem $tmpDir -Filter 'clojure.*.clj.dll' | ForEach-Object {
            Move-Item -Force -Path $_.FullName -Destination (Join-Path $binDir $_.Name)
        }
    } finally {
        Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
    }
}

# 3. Pack
Invoke-Checked "Pack as tool '$ToolName' (PackageId=$PackageId)" {
    dotnet pack --no-restore -c $Configuration `
        -p:TargetFrameworks=$Framework `
        -p:ToolCommandName=$ToolName `
        -p:PackageId=$PackageId `
        $mainProj
}

$nupkg = Get-ChildItem $packOutDir -Filter "$PackageId.*.nupkg" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $nupkg) { throw "No $PackageId.*.nupkg found in $packOutDir" }
$version = $nupkg.BaseName.Substring($PackageId.Length + 1)

# 4. Install (idempotent)
$installed = dotnet tool list --global | Select-String -Pattern "^\s*$([regex]::Escape($PackageId))\s"
if ($installed) {
    Invoke-Checked "Uninstall existing $PackageId" { dotnet tool uninstall --global $PackageId }
}
Invoke-Checked "Install $PackageId $version from $packOutDir" {
    dotnet tool install --global --add-source $packOutDir --version $version $PackageId
}

# 5. Touch .clj*.dll in the install dir so clojure.lang.RT.load prefers the
#    AOT'd assembly over the .clj source. See the header comment for why.
$installDir = Join-Path $env:USERPROFILE ".dotnet/tools/.store/$PackageId/$version/$PackageId/$version/tools/$Framework/any"
if (Test-Path $installDir) {
    $stamped = 0
    Get-ChildItem $installDir -Filter '*.clj*.dll' | ForEach-Object {
        $_.LastWriteTime = Get-Date
        $stamped++
    }
    Write-Host "==> Stamped $stamped .clj*.dll files newer than sources" -ForegroundColor Cyan
} else {
    Write-Warning "Install dir not found at expected path; skipping timestamp fix: $installDir"
}

# 6. Verify - quick startup sanity check
Write-Host "==> Verifying ($ToolName -e ...)" -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
& $ToolName -e "(println :hello-from (clojure-version))"
$sw.Stop()
if ($LASTEXITCODE -ne 0) { throw "verification failed" }
Write-Host ("Done in {0:N0} ms. Invoke with: {1}" -f $sw.Elapsed.TotalMilliseconds, $ToolName) -ForegroundColor Green
