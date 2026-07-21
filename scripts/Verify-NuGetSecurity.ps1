[CmdletBinding()]
param(
    [string] $Project = 'SessionDock.slnx'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PropertyValue {
    param(
        [Parameter(Mandatory)]
        [object] $InputObject,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Invoke-PackageAudit {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('vulnerable', 'deprecated')]
        [string] $Kind
    )

    $arguments = @(
        'package', 'list',
        '--project', $Project,
        "--$Kind",
        '--include-transitive',
        '--format', 'json',
        '--output-version', '1',
        '--no-restore'
    )
    $output = @(& dotnet @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet $Kind audit failed to run:`n$($output -join [Environment]::NewLine)"
    }

    try {
        $report = $output -join [Environment]::NewLine | ConvertFrom-Json
    }
    catch {
        throw "NuGet $kind audit returned invalid JSON."
    }
    if ((Get-PropertyValue $report 'version') -ne 1) {
        throw "NuGet $kind audit returned an unsupported report version."
    }

    $findings = [Collections.Generic.List[string]]::new()
    foreach ($projectReport in @(Get-PropertyValue $report 'projects')) {
        $projectPath = [string] (Get-PropertyValue $projectReport 'path')
        $frameworks = Get-PropertyValue $projectReport 'frameworks'
        if ($null -eq $frameworks) {
            continue
        }
        foreach ($framework in @($frameworks)) {
            foreach ($collectionName in @('topLevelPackages', 'transitivePackages')) {
                $packages = Get-PropertyValue $framework $collectionName
                if ($null -eq $packages) {
                    continue
                }
                foreach ($package in @($packages)) {
                    $evidence = if ($Kind -eq 'vulnerable') {
                        Get-PropertyValue $package 'vulnerabilities'
                    }
                    else {
                        Get-PropertyValue $package 'deprecationReasons'
                    }
                    if ($null -eq $evidence -or @($evidence).Count -eq 0) {
                        continue
                    }

                    $id = [string] (Get-PropertyValue $package 'id')
                    $version = [string] (Get-PropertyValue $package 'resolvedVersion')
                    $findings.Add("$projectPath :: $id $version")
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        throw "NuGet $kind packages were found:`n$($findings -join [Environment]::NewLine)"
    }
}

Invoke-PackageAudit -Kind vulnerable
Invoke-PackageAudit -Kind deprecated
Write-Host "NuGet vulnerability and deprecation validation passed for $Project."
