#!/usr/bin/env pwsh
# Benchmark ClojureCLR tool startup across tools x expressions and print a
# markdown table. The "Why" column is derived by inspecting each tool's actual
# global-tool install (R2R vs IL, compiled namespace count, whether spec is
# precompiled), so it reflects what is really on disk rather than guesses.
#
# Usage:
#   pwsh ./scripts/bench-startup.ps1
#   pwsh ./scripts/bench-startup.ps1 -Tools clj-mayne,Clojure.main -Runs 7
#   pwsh ./scripts/bench-startup.ps1 -Expressions "(println :ok)","(defn f [x] x)(f 1)"

[CmdletBinding()]
param(
    [string[]]$Tools = @('clj-mayne', 'Clojure.main'),
    [string[]]$Expressions = @(
        '(println :ok)',
        '(defn h [a] (println a)) (h :ok)',
        "(require '[clojure.string :as s]) (println (s/upper-case ""ok""))",
        "(require '[clojure.spec.alpha :as s]) (s/def ::n int?) (println (s/valid? ::n 1))"
    ),
    [int]$Runs = 5,
    [int]$Warmup = 1
)

$ErrorActionPreference = 'Stop'

# Locate a global tool's payload dir (the "tools/<tfm>/any" folder) by command name.
function Get-ToolInstallDir {
    param([string]$Command)
    $store = Join-Path $env:USERPROFILE '.dotnet/tools/.store'
    if (-not (Test-Path $store)) { return $null }
    # Find the DotnetToolSettings.xml whose <Command Name="..."> matches.
    foreach ($settings in Get-ChildItem $store -Recurse -Filter 'DotnetToolSettings.xml' -ErrorAction SilentlyContinue) {
        try {
            [xml]$xml = Get-Content $settings.FullName
            $names = @($xml.DotNetCliTool.Commands.Command.Name)
            if ($names -contains $Command) { return $settings.Directory.FullName }
        } catch { }
    }
    return $null
}

# Derive a short, factual "Why" from what is actually installed.
function Get-WhyText {
    param([string]$Command)
    $dir = Get-ToolInstallDir -Command $Command
    if (-not $dir) { return 'not a global tool / build details unavailable' }

    $core = Join-Path $dir 'clojure.core.clj.dll'
    $compiledNs = @(Get-ChildItem $dir -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match '\.clj[c]?\.dll$' }).Count
    $specCompiled = Test-Path (Join-Path $dir 'clojure.spec.alpha.clj.dll')

    if (Test-Path $core) {
        $mb = [math]::Round((Get-Item $core).Length / 1MB, 1)
        # R2R images are much larger than IL (core ~2.8MB R2R vs ~0.9MB IL).
        $kind = if ($mb -gt 1.5) { 'R2R' } else { 'IL' }
        $spec = if ($specCompiled) { 'spec compiled' } else { 'spec source-loaded' }
        return "$kind namespaces (core $mb MB) - $compiledNs compiled ns - $spec"
    }
    return 'no compiled core - namespaces source-loaded'
}

function Measure-Startup {
    param([string]$Command, [string]$Expr, [int]$Runs, [int]$Warmup)
    for ($i = 0; $i -lt $Warmup; $i++) { & $Command -e $Expr *> $null }
    $samples = for ($i = 0; $i -lt $Runs; $i++) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        & $Command -e $Expr *> $null
        $sw.Stop()
        if ($LASTEXITCODE -ne 0) { throw "$Command exited $LASTEXITCODE on: $Expr" }
        $sw.Elapsed.TotalMilliseconds
    }
    $sorted = @($samples | Sort-Object)
    [pscustomobject]@{
        Median = [math]::Round($sorted[[int]([math]::Floor($sorted.Count / 2))])
        Min    = [math]::Round(($sorted | Select-Object -First 1))
        Max    = [math]::Round(($sorted | Select-Object -Last 1))
    }
}

# Resolve which requested tools are actually runnable.
$active = foreach ($t in $Tools) {
    if (Get-Command $t -ErrorAction SilentlyContinue) { $t }
    else { Write-Warning "Tool '$t' not found on PATH - skipping" }
}
if (-not $active) { throw "No runnable tools among: $($Tools -join ', ')" }

$why = @{}
foreach ($t in $active) { $why[$t] = Get-WhyText -Command $t }

Write-Host "Benchmarking $($active.Count) tool(s) x $($Expressions.Count) expression(s); $Runs runs each (+$Warmup warmup)..." -ForegroundColor Cyan

$rows = foreach ($expr in $Expressions) {
    foreach ($t in $active) {
        Write-Host ("  {0,-14} {1}" -f $t, $expr) -ForegroundColor DarkGray
        $r = Measure-Startup -Command $t -Expr $expr -Runs $Runs -Warmup $Warmup
        [pscustomobject]@{
            Expression = $expr
            Tool       = $t
            MedianMs   = $r.Median
            MinMs      = $r.Min
            Why        = $why[$t]
        }
    }
}

# Render a markdown table.
function Esc([string]$s) { ($s -replace '\|', '\|') }
$lines = @()
$lines += '| Expression | Tool | Median ms | Min ms | Why |'
$lines += '|---|---|---:|---:|---|'
foreach ($row in $rows) {
    $lines += ('| `{0}` | {1} | {2} | {3} | {4} |' -f (Esc $row.Expression), $row.Tool, $row.MedianMs, $row.MinMs, (Esc $row.Why))
}
Write-Host ""
$lines -join "`n" | Write-Output
