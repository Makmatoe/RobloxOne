[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Repository = 'Makmatoe/SessionDock',

    [string] $ReleaseReviewer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-GhJson {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub API request failed: gh $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    if ($output.Count -eq 0) { return $null }
    return ($output -join [Environment]::NewLine) | ConvertFrom-Json
}

function Invoke-GhMutation {
    param(
        [Parameter(Mandatory)] [string] $Description,
        [Parameter(Mandatory)] [string[]] $Arguments
    )
    if ($PSCmdlet.ShouldProcess($Repository, $Description)) {
        $output = @(& gh @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "$Description failed:`n$($output -join [Environment]::NewLine)"
        }
    }
}

& gh auth status | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI authentication is required.' }
$repositoryState = Invoke-GhJson @('api', "repos/$Repository")
if (-not $repositoryState.permissions.admin) {
    throw "The current GitHub token is not an administrator of $Repository. Re-run this script with an admin-authenticated gh session."
}

Invoke-GhMutation 'enable vulnerability alerts' @(
    'api', '--method', 'PUT', "repos/$Repository/vulnerability-alerts")
Invoke-GhMutation 'enable automated dependency security updates' @(
    'api', '--method', 'PUT', "repos/$Repository/automated-security-fixes")
Invoke-GhMutation 'enable secret scanning and push protection' @(
    'api', '--method', 'PATCH', "repos/$Repository",
    '-f', 'security_and_analysis[secret_scanning][status]=enabled',
    '-f', 'security_and_analysis[secret_scanning_push_protection][status]=enabled')
Invoke-GhMutation 'enforce read-only default workflow permissions' @(
    'api', '--method', 'PUT', "repos/$Repository/actions/permissions/workflow",
    '-f', 'default_workflow_permissions=read',
    '-F', 'can_approve_pull_request_reviews=false')

$actions = Invoke-GhJson @('api', "repos/$Repository/actions/permissions")
$selected = Invoke-GhJson @(
    'api', "repos/$Repository/actions/permissions/selected-actions")
$obsoleteActionPatterns = @(
    ('Azure/artifact-' + 'signing-action@*'),
    ('Azure/' + 'login@*'))
$patterns = @($selected.patterns_allowed | Where-Object {
        $_ -notin $obsoleteActionPatterns
    } | Sort-Object -Unique)
$patternsChanged = @($selected.patterns_allowed).Count -ne $patterns.Count
if (-not $actions.sha_pinning_required) {
    Invoke-GhMutation 'require full commit SHA pinning for every Action' @(
        'api', '--method', 'PUT', "repos/$Repository/actions/permissions",
        '-F', 'enabled=true',
        '-f', 'allowed_actions=selected',
        '-F', 'sha_pinning_required=true')
}
if ($patternsChanged) {
    if ($PSCmdlet.ShouldProcess(
            $Repository,
            'remove unused managed signing Action repositories')) {
        $body = @{
            github_owned_allowed = [bool] $selected.github_owned_allowed
            verified_allowed = [bool] $selected.verified_allowed
            patterns_allowed = [string[]] $patterns
        } | ConvertTo-Json -Compress
        $output = @($body | & gh api --method PUT `
            "repos/$Repository/actions/permissions/selected-actions" `
            --input - 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "Removing unused managed signing Action repositories failed:`n$($output -join [Environment]::NewLine)"
        }
    }
}

$rulesets = @(Invoke-GhJson @('api', "repos/$Repository/rulesets"))
$mainRuleset = @($rulesets | Where-Object {
        $_.target -eq 'branch' -and $_.name -eq 'Protect main'
    })
$tagRuleset = @($rulesets | Where-Object {
        $_.target -eq 'tag' -and $_.name -eq 'Protect release tags'
    })
if ($mainRuleset.Count -ne 1) {
    Write-Warning 'Create an active main ruleset requiring pull requests, resolved conversations, strict current status checks, and blocking deletion/non-fast-forward updates. No ruleset was invented because required check names and bypass actors are repository-specific.'
}
if ($tagRuleset.Count -ne 1) {
    Write-Warning 'Create an active refs/tags/v* ruleset blocking creation/update/deletion by unauthorized actors. No bypass actor was chosen automatically.'
}

$environments = Invoke-GhJson @('api', "repos/$Repository/environments")
foreach ($name in @('release', 'release-publication')) {
    $environment = @($environments.environments | Where-Object { $_.name -ceq $name })
    $hasReviewer = $environment.Count -eq 1 -and
        @($environment[0].protection_rules | Where-Object {
            $_.type -eq 'required_reviewers' -and $_.reviewers.Count -gt 0
        }).Count -gt 0
    if (-not $hasReviewer) {
        if ([string]::IsNullOrWhiteSpace($ReleaseReviewer)) {
            Write-Warning "Environment '$name' still needs an explicit reviewer. Re-run with -ReleaseReviewer <GitHub-login>; this script will never choose one automatically."
        }
        else {
            Write-Warning "Reviewer '$ReleaseReviewer' was supplied, but environment reviewer mutation is intentionally left for the repository owner to confirm in GitHub because user/team IDs and self-review policy are governance choices."
        }
    }
}

Write-Host 'GitHub security configuration audit completed without weakening existing rules.'
