<#
.SYNOPSIS
    Dot-source to get Set-CortexTimeZoneEnv.

.DESCRIPTION
    docker-compose.yml maps CORTEX_TIMEZONE to the agent's History__TimeZone so history day
    boundaries follow the host's zone instead of the container's UTC. Set-CortexTimeZoneEnv exports
    the host's IANA zone id as $env:CORTEX_TIMEZONE for the compose calls that follow. A value the
    user already set is kept; a zone with no IANA mapping exports nothing (the container zone stays).
#>

function Set-CortexTimeZoneEnv {
    if (-not [string]::IsNullOrWhiteSpace($env:CORTEX_TIMEZONE)) { return }
    $local = [System.TimeZoneInfo]::Local
    $iana = ''
    if ($local.HasIanaId) {
        $iana = $local.Id
    } else {
        $converted = ''
        if ([System.TimeZoneInfo]::TryConvertWindowsIdToIanaId($local.Id, [ref]$converted)) { $iana = $converted }
    }
    if ($iana) { $env:CORTEX_TIMEZONE = $iana }
}
