param(
    [string]$Repository = 'Liis17/BarkCloud'
)

# Applies every ruleset from .github/rulesets: dev accepts any PR that passes "Tests passed";
# master accepts only promotion PRs from dev (the source branch is checked by the same check).
# Rulesets listed in $obsolete are deleted after the new ones are in place.
$ErrorActionPreference = 'Stop'
$obsolete = @('main rules')
$rulesetFiles = Get-ChildItem (Join-Path $PSScriptRoot '../rulesets') -Filter '*.json'
$configs = @($rulesetFiles | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })

# The required check must already exist in every protected branch, otherwise PRs block forever.
foreach ($ref in ($configs | ForEach-Object { $_.conditions.ref_name.include } | Sort-Object -Unique)) {
    $branch = $ref -replace '^refs/heads/', ''
    gh api "repos/$Repository/contents/.github/workflows/ci.yml?ref=$branch" --silent 2>$null
    if ($LASTEXITCODE -ne 0) {
        $global:LASTEXITCODE = 0
        throw "No workflow producing 'Tests passed' in $branch. No rules were changed."
    }
}

$rulesets = gh api "repos/$Repository/rulesets" --paginate --slurp | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read repository rulesets.' }
$rulesets = @($rulesets | ForEach-Object { $_ })

foreach ($file in $rulesetFiles) {
    $name = (Get-Content $file.FullName -Raw | ConvertFrom-Json).name
    $existing = @($rulesets | Where-Object { $_.name -eq $name })
    if ($existing.Count -gt 1) { throw "More than one '$name' ruleset exists." }
    if ($existing.Count -eq 1) {
        gh api --method PUT "repos/$Repository/rulesets/$($existing[0].id)" --input $file.FullName --silent
    } else {
        gh api --method POST "repos/$Repository/rulesets" --input $file.FullName --silent
    }
    if ($LASTEXITCODE -ne 0) { throw "Cannot apply ruleset '$name'." }
}

foreach ($ruleset in @($rulesets | Where-Object { $obsolete -contains $_.name })) {
    gh api --method DELETE "repos/$Repository/rulesets/$($ruleset.id)" --silent
    if ($LASTEXITCODE -ne 0) { throw "Cannot delete obsolete ruleset '$($ruleset.name)'." }
}

foreach ($branch in @('dev', 'master')) {
    gh api "repos/$Repository/rules/branches/$branch" --jq '.[].type'
    if ($LASTEXITCODE -ne 0) { throw "Cannot verify rules for $branch." }
}
