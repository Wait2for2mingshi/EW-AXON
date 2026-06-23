[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int]$Port = 5001,
    [string]$RuleName = "",
    [string]$RemoteAddress = "Any",
    [string]$Profile = "Any",
    [switch]$SkipFirewall,
    [switch]$StatusOnly
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-EffectiveRuleName {
    param([int]$RulePort, [string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return "EW Assistant MCP Server TCP $RulePort"
    }

    return $Name
}

function Upsert-FirewallRule {
    param(
        [int]$RulePort,
        [string]$DisplayName,
        [string]$AllowedRemoteAddress,
        [string]$RuleProfile
    )

    $existingRules = @(Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue)
    if ($existingRules.Count -eq 0) {
        if ($PSCmdlet.ShouldProcess($DisplayName, "Create inbound TCP firewall rule")) {
            New-NetFirewallRule `
                -DisplayName $DisplayName `
                -Direction Inbound `
                -Action Allow `
                -Enabled True `
                -Protocol TCP `
                -LocalPort $RulePort `
                -Profile $RuleProfile `
                -RemoteAddress $AllowedRemoteAddress | Out-Null
        }

        Write-Host "Created firewall rule: $DisplayName"
        return
    }

    foreach ($rule in $existingRules) {
        if ($PSCmdlet.ShouldProcess($rule.DisplayName, "Update inbound TCP firewall rule")) {
            Set-NetFirewallRule `
                -Name $rule.Name `
                -Enabled True `
                -Direction Inbound `
                -Action Allow `
                -Profile $RuleProfile

            $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter `
                -Protocol TCP `
                -LocalPort $RulePort

            $rule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter `
                -RemoteAddress $AllowedRemoteAddress
        }
    }

    Write-Host "Updated firewall rule: $DisplayName"
}

function Show-ListenerStatus {
    param([int]$ListenPort)

    Write-Host ""
    Write-Host "TCP listeners on port $ListenPort:"
    $listeners = @(Get-NetTCPConnection -LocalPort $ListenPort -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -eq 0) {
        Write-Warning "No process is listening on TCP port $ListenPort."
        return
    }

    $listeners |
        Select-Object LocalAddress, LocalPort, State, OwningProcess |
        Format-Table -AutoSize
}

function Show-FirewallRule {
    param([string]$DisplayName)

    Write-Host ""
    Write-Host "Firewall rule:"
    $rules = @(Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue)
    if ($rules.Count -eq 0) {
        Write-Warning "Firewall rule not found: $DisplayName"
        return
    }

    foreach ($rule in $rules) {
        $portFilter = $rule | Get-NetFirewallPortFilter
        $addressFilter = $rule | Get-NetFirewallAddressFilter

        [PSCustomObject]@{
            DisplayName   = $rule.DisplayName
            Enabled       = $rule.Enabled
            Direction     = $rule.Direction
            Action        = $rule.Action
            Profile       = $rule.Profile
            Protocol      = $portFilter.Protocol
            LocalPort     = $portFilter.LocalPort
            RemoteAddress = $addressFilter.RemoteAddress
        } | Format-List
    }
}

if ($Port -lt 1 -or $Port -gt 65535) {
    throw "Port must be between 1 and 65535."
}

$effectiveRuleName = Get-EffectiveRuleName -RulePort $Port -Name $RuleName

if (-not $StatusOnly -and -not $SkipFirewall -and -not (Test-IsAdministrator)) {
    throw "Please run PowerShell as Administrator to create or update Windows Firewall rules."
}

if (-not $StatusOnly -and -not $SkipFirewall) {
    Upsert-FirewallRule `
        -RulePort $Port `
        -DisplayName $effectiveRuleName `
        -AllowedRemoteAddress $RemoteAddress `
        -RuleProfile $Profile
}

Show-FirewallRule -DisplayName $effectiveRuleName
Show-ListenerStatus -ListenPort $Port

Write-Host ""
Write-Host "Expected Dify MCP SSE URL: http://<windows-ip>:$Port/sse"
