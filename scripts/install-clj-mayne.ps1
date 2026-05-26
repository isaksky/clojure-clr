#!/usr/bin/env pwsh
# Build Clojure.Main and install it as a global .NET tool (default name: clj-mayne)
# with startup matching the branch's sub-500ms target.
#
# Pipeline:
#   1. Build Clojure.Main      - produces the tool exe / dependencies.
#   2. Build Clojure.Compile   - its PostBuild AOT-compiles ~30 runtime
#                                namespaces to clojure.*.clj(c).dll and copies
#                                them into Clojure.Main's bin output. Without
#                                this, startup source-loads (~3s, master speed).
#   3. crossgen2 R2R           - precompiles the generated namespace DLLs to
#                                ReadyToRun in-place; drops cold start from
#                                ~450ms (IL) to ~270-310ms on Windows.
#   4. dotnet pack             - bundles bin into a nupkg.
#   5. dotnet tool install     - installs the nupkg as a global tool.
#   6. Touch .clj*.dll         - bumps timestamps in the install dir so
#                                clojure.lang.RT.load picks the AOT'd assembly
#                                over the .clj source. NuGet's extract order
#                                leaves sources newer than DLLs by a few ms,
#                                which silently triggers source loading.
#
# Each crossgen2 call must get exactly one "-r" per reference assembly (every
# other namespace DLL in bin + every runtime DLL). A malformed reference list
# produces R2R images that fail to load ("Cannot find initializer ..."), so the
# loop below builds refs carefully. Use -SkipR2R to install IL-only (~450ms).
#
# Usage:
#   pwsh ./scripts/install-clj-mayne.ps1
#   pwsh ./scripts/install-clj-mayne.ps1 -SkipR2R
#   pwsh ./scripts/install-clj-mayne.ps1 -Framework net10.0 -ToolName clj-mayne

[CmdletBinding()]
param(
    [string]$Framework = 'net10.0',
    [string]$Configuration = 'Release',
    [string]$ToolName = 'clj-mayne',
    [string]$PackageId = 'clj-mayne',
    [string]$Rid,
    [switch]$SkipR2R
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

# Match both clojure.*.clj.dll and clojure.*.cljc.dll generated namespace assemblies.
$cljDlls = @(Get-ChildItem $binDir -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -match '^clojure\..*\.clj[c]?\.dll$' })
if ($cljDlls.Count -eq 0) {
    throw "No clojure.*.clj(c).dll found in $binDir after Clojure.Compile build"
}
Write-Host "    $($cljDlls.Count) generated namespace DLLs in $binDir"

# ReadyToRun-compile the generated namespace DLLs in-place (default on)
if (-not $SkipR2R) {
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
    if (-not (Test-Path $runtimeDir)) { throw "Runtime directory not found: $runtimeDir" }

    Write-Host "==> ReadyToRun ($Rid) via $($crossgen.FullName)" -ForegroundColor Cyan
    # Reference list is shared except for the assembly being compiled, so build
    # the runtime-DLL part once.
    $runtimeRefs = @()
    Get-ChildItem $runtimeDir -Filter '*.dll' | ForEach-Object { $runtimeRefs += @('-r', $_.FullName) }
    $binDlls = Get-ChildItem $binDir -Filter '*.dll'

    $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("clojure-clr-r2r." + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmpDir | Out-Null
    try {
        # NB: do not name the loop variable $input - that is a PowerShell
        # automatic variable (the pipeline enumerator). Referencing it inside the
        # Where-Object below would silently drop every bin reference, producing a
        # partial R2R image (~IL size, no startup gain).
        foreach ($cljDll in $cljDlls) {
            $refs = @()
            $binDlls | Where-Object { $_.Name -ne $cljDll.Name } | ForEach-Object { $refs += @('-r', $_.FullName) }
            $refs += $runtimeRefs
            & $crossgen.FullName -o (Join-Path $tmpDir $cljDll.Name) @refs $cljDll.FullName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "crossgen2 failed for $($cljDll.Name)" }
        }
        # Move every R2R output back over the IL original.
        Get-ChildItem $tmpDir -Filter '*.dll' | ForEach-Object {
            Move-Item -Force -Path $_.FullName -Destination (Join-Path $binDir $_.Name)
        }
        Write-Host "    R2R complete for $($cljDlls.Count) DLLs" -ForegroundColor Cyan
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
