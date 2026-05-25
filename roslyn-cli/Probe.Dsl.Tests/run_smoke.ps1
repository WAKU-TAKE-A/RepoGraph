$ErrorActionPreference = "Stop"

$repoRoot = (Get-Item $PSScriptRoot\..\..).FullName
$probeProj = "$repoRoot\roslyn-cli\Probe\Probe.csproj"
$fixtureDir = "$repoRoot\roslyn-cli\Probe.Dsl.Tests\Fixtures\SmokeProject"
$outOff = "$repoRoot\tmp\dsl-smoke\off"
$outOn = "$repoRoot\tmp\dsl-smoke\on"
$fixtureBin = "$fixtureDir\bin"
$fixtureObj = "$fixtureDir\obj"
$fixtureAnalyzer = "$fixtureDir\analyzer.yml"

function Reset-SmokeFixture {
    if (Test-Path $fixtureBin) { Remove-Item -Recurse -Force $fixtureBin }
    if (Test-Path $fixtureObj) { Remove-Item -Recurse -Force $fixtureObj }
    if (Test-Path $fixtureAnalyzer) { Remove-Item -Force $fixtureAnalyzer }
}

try {
    Reset-SmokeFixture

    Write-Host "--- Smoke Test: DSL Disabled ---"
    Copy-Item "$fixtureDir\analyzer_off.yml" $fixtureAnalyzer -Force
    if (Test-Path $outOff) { Remove-Item -Recurse -Force $outOff }
    & dotnet run --project $probeProj -- scan "$fixtureDir\SmokeProject.csproj" --output $outOff

    $depsOff = "$outOff\output\graphs\type_dependency_graph.json"
    if (Test-Path $depsOff) {
        $content = Get-Content -Encoding UTF8 $depsOff -Raw
        if ($content -match "candidate") {
            Write-Error "Test Failed: Found 'candidate' in type_dependency_graph.json when DSL is OFF!"
            exit 1
        }
    }
    Write-Host "PASS: No candidates found when DSL is OFF."

    Write-Host "`n--- Smoke Test: DSL Enabled ---"
    $absRules = "$fixtureDir\rules" -replace '\\', '\\'
    $yamlContent = @"
analysis:
  enableDslCandidates: true
  dslRulesDirectory: `"$absRules`"
"@
    Set-Content -Path $fixtureAnalyzer -Value $yamlContent -Encoding UTF8 -Force

    if (Test-Path $outOn) { Remove-Item -Recurse -Force $outOn }
    & dotnet run --project $probeProj -- scan "$fixtureDir\SmokeProject.csproj" --output $outOn

    $depsOn = "$outOn\output\graphs\type_dependency_graph.json"
    if (-Not (Test-Path $depsOn)) {
        Write-Error "Test Failed: type_dependency_graph.json not found when DSL is ON!"
        exit 1
    }

    $contentOn = Get-Content -Encoding UTF8 $depsOn -Raw
    if ($contentOn -notmatch "smoke.test.rule" -or $contentOn -notmatch "candidate") {
        Write-Error "Test Failed: Candidate edge from smoke.test.rule NOT found when DSL is ON!"
        Write-Host "File contents:"
        Write-Host $contentOn
        exit 1
    }

    Write-Host "PASS: Candidate edge successfully emitted and persisted when DSL is ON."
    Write-Host "Smoke tests passed successfully!"
}
finally {
    Reset-SmokeFixture
}
