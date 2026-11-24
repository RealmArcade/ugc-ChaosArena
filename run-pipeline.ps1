<#
.SYNOPSIS
    Single re-runnable pipeline for Chaos Arena balance verification and bot policy evaluation.
.DESCRIPTION
    Implements Phase 4 of the master plan:
    1. Validates schema and asserts draftable heroes keep their presentation fields.
    2. Compiles MapScript to WASI WebAssembly.
    3. Measures B1, B2, B3 and B4 from one uniform-vs-uniform match run.
    4. Evaluates the trained bot policy against uniform random (G1).
    5. Outputs the balance report and the bot profile evidence.
#>

[CmdletBinding()]
param (
    [string]$MapDirectory = "",
    [string]$EngineDirectory = "",
    [string]$WasiSdkPath = "",
    [int]$MatchCount = 4000,
    [int]$SeedCount = 400,
    [int]$Parallel = 8,
    [int]$SeedStart = 1,
    [string]$PriorOutcomesPath = "",
    [int]$MinimumPicksPerHero = 400,
    [int]$MinimumPicksPerTile = 30,
    [int]$BotMatchCount = 2000,
    [int]$ProfileDuelMatchCount = 400,
    [string]$OutputReportPath = ""
)

$ErrorActionPreference = "Stop"
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
if (-not $MapDirectory) { $MapDirectory = $PSScriptRoot }
if (-not (Test-Path (Join-Path $MapDirectory "metadata.json"))) { throw "MapDirectory does not hold metadata.json, pass -MapDirectory." }
if (-not $EngineDirectory) { throw "Pass -EngineDirectory with the path of the Realm checkout that holds Realm.Tools.MatchRunner." }
if (-not (Test-Path (Join-Path $EngineDirectory "Realm.Tools.MatchRunner"))) { throw "EngineDirectory does not hold Realm.Tools.MatchRunner, pass -EngineDirectory." }
if (-not $WasiSdkPath) { $WasiSdkPath = $env:WASI_SDK_PATH }
if (-not $WasiSdkPath -or -not (Test-Path $WasiSdkPath)) { throw "Pass -WasiSdkPath or set WASI_SDK_PATH to a wasi-sdk directory." }
if (-not $OutputReportPath) { $OutputReportPath = Join-Path $MapDirectory "balance\chaos-arena-balance-report.json" }
$outputDirectory = Split-Path -Parent $OutputReportPath
if ($outputDirectory -and -not (Test-Path $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }

function Get-EquityStatus {
    param ([double]$Rate, [int]$Sample, [int]$MinimumSample, [double]$Tolerance)

    if ($Sample -lt $MinimumSample) { return "InsufficientSample" }
    $halfWidth = 1.96 * [Math]::Sqrt($Rate * (1.0 - $Rate) / [double]$Sample)
    if ([Math]::Abs($Rate - 0.5) -gt $halfWidth) { return "Failed" }
    if ([Math]::Abs($Rate - 0.5) -gt $Tolerance) { return "Inconclusive" }
    return "Passed"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " CHAOS ARENA: RE-RUNNABLE BALANCE & BOT PIPELINE" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Guard check: the draftable heroes and their presentation fields
Write-Host "[1/5] Checking metadata integrity & presentation guards..." -ForegroundColor Yellow
$metadataPath = Join-Path $MapDirectory "metadata.json"
if (-not (Test-Path $metadataPath)) {
    throw "metadata.json not found at $metadataPath"
}

$metadata = Get-Content $metadataPath -Raw | ConvertFrom-Json
$units = @($metadata.Templates.Units)
$scriptText = Get-Content (Join-Path $MapDirectory "MapScript.cs") -Raw
$draftableBlock = [regex]::Match($scriptText, 'DraftableUnits\s*=\s*\{(?<body>.*?)\};', 'Singleline')
if (-not $draftableBlock.Success) {
    throw "MapScript.cs does not declare DraftableUnits, so the pipeline cannot know which heroes the draft offers."
}

$heroIds = @([regex]::Matches($draftableBlock.Groups['body'].Value, 'UnitIds\.(\w+)') | ForEach-Object {
    "unit/" + ($_.Groups[1].Value -creplace '(?<!^)([A-Z])', '_$1').ToLowerInvariant()
})
if ($heroIds.Count -eq 0) {
    throw "DraftableUnits lists no heroes, so B1 has nothing to measure."
}

foreach ($heroId in $heroIds) {
    $element = $units | Where-Object { $_.TemplateID -eq $heroId } | Select-Object -First 1
    if (-not $element) {
        throw "Guard violation: DraftableUnits names $heroId, which metadata.json does not define."
    }
    if (-not $element.Name) {
        throw "Guard violation: unit $heroId is missing required Name field"
    }
    if (-not $element.Description) {
        throw "Guard violation: unit $heroId is missing required Description field"
    }
}
Write-Host "  -> $($heroIds.Count) draftable heroes verified against metadata.json." -ForegroundColor Green

# 2. Build WASM
Write-Host "[2/5] Compiling MapScript to WASI WebAssembly..." -ForegroundColor Yellow
$env:WASI_SDK_PATH = $WasiSdkPath
$publishOutput = & dotnet publish (Join-Path $MapDirectory "MapScript.csproj") -c Release 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed:`n$publishOutput"
}
$wasmPath = Join-Path $MapDirectory "bin\Release\net10.0\wasi-wasm\publish\MapScript.wasm"
if (-not (Test-Path $wasmPath)) {
    throw "WASM artifact not found at $wasmPath"
}
Write-Host "  -> MapScript.wasm published successfully." -ForegroundColor Green

# 3. Measure B1, B2, B3 and B4 from one uniform-vs-uniform run
Write-Host "[3/5] Playing uniform seeds $SeedStart-$MatchCount (parallel=$Parallel)..." -ForegroundColor Yellow
$runnerProject = Join-Path $EngineDirectory "Realm.Tools.MatchRunner"
$randomOutcomes = [System.IO.Path]::ChangeExtension($OutputReportPath, ".random.jsonl")
$resumeOutcomes = if ($SeedStart -gt 1) { "$randomOutcomes.resume.jsonl" } else { $randomOutcomes }

& dotnet run --project $runnerProject -c Release -- `
    --map $MapDirectory `
    --seed-range "$SeedStart-$MatchCount" `
    --seats 2 `
    --parallel $Parallel `
    --max-seconds 1800 `
    --policy uniform `
    --output $resumeOutcomes

if ($LASTEXITCODE -ne 0) {
    throw "MatchRunner uniform pass failed"
}

if ($SeedStart -gt 1 -or $PriorOutcomesPath) {
    $sources = @()
    if ($PriorOutcomesPath) {
        if (Test-Path $PriorOutcomesPath -PathType Container) {
            $sources += @(Get-ChildItem $PriorOutcomesPath -Filter *.jsonl | Where-Object { $_.Name -notlike "*.summary.json" } | ForEach-Object { $_.FullName })
        }
        else {
            $sources += $PriorOutcomesPath
        }
    }
    if ($SeedStart -gt 1) {
        $sources += $resumeOutcomes
    }

    $mergedBySeed = @{}
    foreach ($source in $sources) {
        foreach ($line in Get-Content $source) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $seed = [int]((ConvertFrom-Json $line).seed) } catch { continue }
            if ($seed -gt 0) { $mergedBySeed[$seed] = $line }
        }
    }

    $mergedLines = @($mergedBySeed.Keys | Sort-Object | ForEach-Object { $mergedBySeed[$_] })
    Set-Content -Path $randomOutcomes -Value $mergedLines -Encoding utf8
    Write-Host "  -> Merged $($mergedLines.Count) outcome lines into $randomOutcomes." -ForegroundColor Green
}

$heroPicks = @{}
$tilePicks = @{}
$decidedMatches = 0
$seatOneWins = 0
$measuredMatches = 0

foreach ($line in Get-Content $randomOutcomes) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $measuredMatches++

    $match = $line | ConvertFrom-Json
    $winnerSeat = [int]$match.winnerSeat
    if ($winnerSeat -eq 0) { continue }
    $decidedMatches++
    if ($winnerSeat -eq 1) { $seatOneWins++ }

    foreach ($seat in $match.perSeat) {
        $heroId = $seat.firstDraftHeroId
        if (-not $heroId) { continue }

        if (-not $heroPicks.ContainsKey($heroId)) {
            $heroPicks[$heroId] = [PSCustomObject]@{ Picks = 0; Wins = 0 }
        }
        $heroPicks[$heroId].Picks++
        if ($seat.seat -eq $winnerSeat) { $heroPicks[$heroId].Wins++ }

        $tileX = ([double]$seat.firstDraftTileX).ToString($invariant)
        $tileZ = ([double]$seat.firstDraftTileZ).ToString($invariant)
        $tileKey = "$heroId|$tileX|$tileZ"
        if (-not $tilePicks.ContainsKey($tileKey)) {
            $tilePicks[$tileKey] = [PSCustomObject]@{ HeroId = $heroId; TileX = $tileX; TileZ = $tileZ; Picks = 0; Wins = 0 }
        }
        $tilePicks[$tileKey].Picks++
        if ($seat.seat -eq $winnerSeat) { $tilePicks[$tileKey].Wins++ }
    }
}

$heroResults = @()
foreach ($heroId in $heroIds) {
    $sample = if ($heroPicks.ContainsKey($heroId)) { $heroPicks[$heroId] } else { [PSCustomObject]@{ Picks = 0; Wins = 0 } }
    $rate = if ($sample.Picks -gt 0) { [double]$sample.Wins / [double]$sample.Picks } else { 0.5 }
    $heroResults += [PSCustomObject]@{
        HeroId = $heroId
        Picks = $sample.Picks
        Wins = $sample.Wins
        WinRate = [Math]::Round($rate * 100.0, 2)
        Ci95 = if ($sample.Picks -gt 0) { [Math]::Round(1.96 * [Math]::Sqrt($rate * (1.0 - $rate) / [double]$sample.Picks) * 100.0, 2) } else { 0.0 }
        DeltaFrom50Percent = [Math]::Round([Math]::Abs($rate - 0.5) * 100.0, 2)
        Status = Get-EquityStatus -Rate $rate -Sample $sample.Picks -MinimumSample $MinimumPicksPerHero -Tolerance 0.03
    }
}

$tileResults = @()
foreach ($tileKey in $tilePicks.Keys) {
    $sample = $tilePicks[$tileKey]
    $rate = if ($sample.Picks -gt 0) { [double]$sample.Wins / [double]$sample.Picks } else { 0.5 }
    $tileResults += [PSCustomObject]@{
        HeroId = $sample.HeroId
        TileX = $sample.TileX
        TileZ = $sample.TileZ
        Picks = $sample.Picks
        Wins = $sample.Wins
        WinRate = [Math]::Round($rate * 100.0, 2)
        Ci95 = if ($sample.Picks -gt 0) { [Math]::Round(1.96 * [Math]::Sqrt($rate * (1.0 - $rate) / [double]$sample.Picks) * 100.0, 2) } else { 0.0 }
        Status = Get-EquityStatus -Rate $rate -Sample $sample.Picks -MinimumSample $MinimumPicksPerTile -Tolerance 0.05
    }
}

$failedHeroes = @($heroResults | Where-Object { $_.Status -eq "Failed" })
$undersampledHeroes = @($heroResults | Where-Object { $_.Status -eq "InsufficientSample" })
$inconclusiveHeroes = @($heroResults | Where-Object { $_.Status -eq "Inconclusive" })
$failedTiles = @($tileResults | Where-Object { $_.Status -eq "Failed" })
$undersampledTiles = @($tileResults | Where-Object { $_.Status -eq "InsufficientSample" })

$maximumHeroWinRate = if ($heroResults.Count -gt 0) { ($heroResults | Measure-Object -Property WinRate -Maximum).Maximum } else { 0.0 }
$seatOneRate = if ($decidedMatches -gt 0) { [double]$seatOneWins / [double]$decidedMatches } else { 0.5 }

$heroBalanceStatus = if ($undersampledHeroes.Count -gt 0) { "InsufficientSample" } elseif ($failedHeroes.Count -gt 0) { "Failed" } elseif ($inconclusiveHeroes.Count -gt 0) { "Inconclusive" } else { "Passed" }
$seatBalanceStatus = Get-EquityStatus -Rate $seatOneRate -Sample $decidedMatches -MinimumSample 2000 -Tolerance 0.02
$inconclusiveTiles = @($tileResults | Where-Object { $_.Status -eq "Inconclusive" })
$sampledTiles = @($tileResults | Where-Object { $_.Picks -ge $MinimumPicksPerTile })
$requiredSampledTiles = [Math]::Ceiling($tileResults.Count / 2.0)
$tileBalanceStatus = if ($tileResults.Count -eq 0 -or $sampledTiles.Count -lt $requiredSampledTiles) { "InsufficientSample" } elseif ($failedTiles.Count -gt 0) { "Failed" } elseif ($inconclusiveTiles.Count -gt 0) { "Inconclusive" } else { "Passed" }
$heroesAtRequiredSample = @($heroResults | Where-Object { $_.Picks -ge $MinimumPicksPerHero })
$skillGuardStatus = if ($heroesAtRequiredSample.Count -eq 0) { "InsufficientSample" } elseif (($heroesAtRequiredSample | Measure-Object -Property WinRate -Maximum).Maximum -gt 60.0) { "Failed" } else { "Passed" }

Write-Host ("  -> B1 hero equity: {0}. Worst hero {1:N2}% (n={2})." -f $heroBalanceStatus, $maximumHeroWinRate, $MinimumPicksPerHero) -ForegroundColor $(if ($heroBalanceStatus -eq "Passed") { "Green" } else { "Yellow" })
Write-Host ("  -> B2 seat balance: {0}. Seat 1 won {1:N2}% of {2} matches." -f $seatBalanceStatus, ($seatOneRate * 100.0), $decidedMatches) -ForegroundColor $(if ($seatBalanceStatus -eq "Passed") { "Green" } else { "Yellow" })
Write-Host ("  -> B3 tile equity: {0}. {1} pairs, {2} failed, {3} undersampled." -f $tileBalanceStatus, $tileResults.Count, $failedTiles.Count, $undersampledTiles.Count) -ForegroundColor $(if ($tileBalanceStatus -eq "Passed") { "Green" } else { "Yellow" })
Write-Host ("  -> B4 skill guard: {0}. Highest hero {1:N2}%." -f $skillGuardStatus, $maximumHeroWinRate) -ForegroundColor $(if ($skillGuardStatus -eq "Passed") { "Green" } else { "Yellow" })

# 4. Evaluate the trained bot against uniform random (G1)
Write-Host "[4/5] Evaluating the trained bot policy against uniform random..." -ForegroundColor Yellow
$mapName = if ($metadata.MapProperties -and $metadata.MapProperties.MapName) { $metadata.MapProperties.MapName } else { "Chaos Arena" }
$botProfilePath = Join-Path $MapDirectory "${mapName}_bot.json"
if (-not (Test-Path $botProfilePath)) {
    throw "Bot profile not found at $botProfilePath"
}

$botOutcomes = [System.IO.Path]::ChangeExtension($OutputReportPath, ".bot.jsonl")
$botSummary = "$botOutcomes.summary.json"

& dotnet run --project $runnerProject -c Release -- `
    --map $MapDirectory `
    --seed-range 1-$BotMatchCount `
    --seats 2 `
    --parallel $Parallel `
    --max-seconds 1800 `
    --seat-policy "1=profile:$botProfilePath" `
    --seat-policy "2=uniform" `
    --output $botOutcomes

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $botSummary)) {
    throw "MatchRunner bot duel pass failed"
}

$botSummaryDoc = Get-Content $botSummary -Raw | ConvertFrom-Json
$seat1Wins = if ($botSummaryDoc.winners.psobject.Properties['1']) { [int]$botSummaryDoc.winners.'1' } else { 0 }
$totalMatches = [int]$botSummaryDoc.matches
$seat1WinRate = if ($totalMatches -gt 0) { [Math]::Round(($seat1Wins / $totalMatches) * 100.0, 2) } else { 0.0 }
$botSampleFloor = 2000
$botStatus = if ($totalMatches -lt $botSampleFloor) { "InsufficientSample" } elseif ($seat1WinRate -ge 65.0) { "Passed" } else { "Failed" }

$profileFirstPicks = @{}
foreach ($line in Get-Content $botOutcomes) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $match = $line | ConvertFrom-Json
    foreach ($seat in $match.perSeat) {
        if ([int]$seat.seat -ne 1) { continue }
        $heroId = $seat.firstDraftHeroId
        if (-not $heroId) { continue }
        if (-not $profileFirstPicks.ContainsKey($heroId)) { $profileFirstPicks[$heroId] = 0 }
        $profileFirstPicks[$heroId]++
    }
}
$profilePickTotal = 0
foreach ($count in $profileFirstPicks.Values) { $profilePickTotal += $count }
$topProfilePick = if ($profilePickTotal -gt 0) { ($profileFirstPicks.Values | Measure-Object -Maximum).Maximum } else { 0 }
$topProfilePickShare = if ($profilePickTotal -gt 0) { [double]$topProfilePick / [double]$profilePickTotal } else { 0.0 }
$pickConcentrationStatus = if ($profilePickTotal -lt 100) { "InsufficientSample" } elseif ($topProfilePickShare -ge 0.80) { "Failed" } else { "Passed" }

Write-Host "  -> Bot (seat 1) won $seat1Wins of $totalMatches matches ($seat1WinRate%). Status: $botStatus." -ForegroundColor $(if ($botStatus -eq "Passed") { "Green" } else { "Yellow" })
Write-Host ("  -> G3 pick concentration: {0}. Top hero holds {1:N1}% of {2} profile first picks." -f $pickConcentrationStatus, ($topProfilePickShare * 100.0), $profilePickTotal) -ForegroundColor $(if ($pickConcentrationStatus -eq "Passed") { "Green" } else { "Yellow" })

Write-Host "[4b/5] Playing the profile against itself (G2)..." -ForegroundColor Yellow
$profileDuelOutcomes = [System.IO.Path]::ChangeExtension($OutputReportPath, ".profile-duel.jsonl")
$profileDuelSummary = "$profileDuelOutcomes.summary.json"

& dotnet run --project $runnerProject -c Release -- `
    --map $MapDirectory `
    --seed-range 1-$ProfileDuelMatchCount `
    --seats 2 `
    --parallel $Parallel `
    --max-seconds 1800 `
    --seat-policy "1=profile:$botProfilePath" `
    --seat-policy "2=profile:$botProfilePath" `
    --output $profileDuelOutcomes

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $profileDuelSummary)) {
    throw "MatchRunner profile duel pass failed"
}

$profileDuelDoc = Get-Content $profileDuelSummary -Raw | ConvertFrom-Json
$duelSeatOneWins = if ($profileDuelDoc.winners.psobject.Properties['1']) { [int]$profileDuelDoc.winners.'1' } else { 0 }
$duelMatches = [int]$profileDuelDoc.matches
$duelWinRate = if ($duelMatches -gt 0) { [double]$duelSeatOneWins / [double]$duelMatches } else { 0.5 }
$duelSampleFloor = 200
$trainedVsTrainedStatus = if ($duelMatches -lt $duelSampleFloor) { "InsufficientSample" } elseif ([Math]::Abs($duelWinRate - 0.5) -gt 0.05) { "Failed" } else { "Passed" }

Write-Host ("  -> G2 trained vs trained: {0}. Seat 1 won {1:N2}% of {2} matches." -f $trainedVsTrainedStatus, ($duelWinRate * 100.0), $duelMatches) -ForegroundColor $(if ($trainedVsTrainedStatus -eq "Passed") { "Green" } else { "Yellow" })

# 5. Emit the balance report
Write-Host "[5/5] Writing balance report to $OutputReportPath..." -ForegroundColor Yellow
$report = [PSCustomObject]@{
    Timestamp = (Get-Date).ToString("o")
    Map = $mapName
    MatchesPlayed = $measuredMatches
    BotMatchesPlayed = $totalMatches
    HeroEquity = [PSCustomObject]@{
        Status = $heroBalanceStatus
        MinimumPicksPerHero = $MinimumPicksPerHero
        FailedHeroes = @($failedHeroes | ForEach-Object { "$($_.HeroId) ($($_.WinRate)%, n=$($_.Picks))" })
        InconclusiveHeroes = @($inconclusiveHeroes | ForEach-Object { "$($_.HeroId) ($($_.WinRate)%, n=$($_.Picks))" })
        UndersampledHeroes = @($undersampledHeroes | ForEach-Object { "$($_.HeroId) (n=$($_.Picks))" })
        Heroes = $heroResults
    }
    SeatBalance = [PSCustomObject]@{
        Status = $seatBalanceStatus
        SeatOneWins = $seatOneWins
        DecidedMatches = $decidedMatches
        SeatOneWinRatePercent = [Math]::Round($seatOneRate * 100.0, 2)
    }
    TileEquity = [PSCustomObject]@{
        Status = $tileBalanceStatus
        MinimumPicksPerTile = $MinimumPicksPerTile
        PairCount = $tileResults.Count
        SampledPairs = $sampledTiles.Count
        RequiredSampledPairs = $requiredSampledTiles
        FailedPairs = @($failedTiles | ForEach-Object { "$($_.HeroId) @ ($($_.TileX), $($_.TileZ)) ($($_.WinRate)%, n=$($_.Picks))" })
        Pairs = $tileResults
    }
    SkillGuard = [PSCustomObject]@{
        Status = $skillGuardStatus
        HighestHeroWinRatePercent = [Math]::Round($maximumHeroWinRate, 2)
    }
    BotPerformance = [PSCustomObject]@{
        Status = $botStatus
        SeatOneWins = $seat1Wins
        TotalMatches = $totalMatches
        MinimumMatches = $botSampleFloor
        WinRatePercent = $seat1WinRate
        DeltaVs50Percent = [Math]::Round($seat1WinRate - 50.0, 2)
    }
    TrainedVsTrained = [PSCustomObject]@{
        Status = $trainedVsTrainedStatus
        SeatOneWins = $duelSeatOneWins
        TotalMatches = $duelMatches
        WinRatePercent = [Math]::Round($duelWinRate * 100.0, 2)
        TolerancePercent = 5.0
    }
    PickConcentration = [PSCustomObject]@{
        Status = $pickConcentrationStatus
        TopHeroPicks = $topProfilePick
        TotalProfilePicks = $profilePickTotal
        TopHeroSharePercent = [Math]::Round($topProfilePickShare * 100.0, 2)
        CeilingPercent = 80.0
    }
    Artifacts = [PSCustomObject]@{
        Wasm = $wasmPath
        Profile = $botProfilePath
        RandomOutcomes = $randomOutcomes
        BotSummary = $botSummary
        ProfileDuelSummary = $profileDuelSummary
    }
}

$report | ConvertTo-Json -Depth 6 | Set-Content $OutputReportPath -Encoding utf8

$allPassed = $heroBalanceStatus -eq "Passed" -and $seatBalanceStatus -eq "Passed" -and $tileBalanceStatus -eq "Passed" -and $skillGuardStatus -eq "Passed" -and $botStatus -eq "Passed" -and $trainedVsTrainedStatus -eq "Passed" -and $pickConcentrationStatus -eq "Passed"
Write-Host "==========================================================" -ForegroundColor Cyan
if ($allPassed) {
    Write-Host " PIPELINE RUN COMPLETED SUCCESSFULLY" -ForegroundColor Green
} else {
    Write-Host " PIPELINE RUN FINISHED WITH UNMET GATES (B1: $heroBalanceStatus, B2: $seatBalanceStatus, B3: $tileBalanceStatus, B4: $skillGuardStatus, G1: $botStatus, G2: $trainedVsTrainedStatus, G3: $pickConcentrationStatus)" -ForegroundColor Yellow
}
Write-Host " Report: $OutputReportPath" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
