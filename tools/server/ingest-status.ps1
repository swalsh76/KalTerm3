<#
.SYNOPSIS
Prints a compact, read-only status of the ingest process on the server. Runs ON the server (deploy-ingest.ps1 copies it to C:\KalTerm\ingest\status.ps1).

.DESCRIPTION
Task and process state, the running release, the last status line and the warnings and errors in the newest log, the database size, approximate
row counts and the rate since the previous call, gap rows by kind, and the run state. It never writes to the KalTerm database. Row counts come
from catalog views (never COUNT(*) over the time-series tables).
#>
param(
    [string]$Instance = 'lpc:.\KALTERM',
    [string]$Database = 'KalTerm',
    [string]$Root = 'C:\KalTerm\ingest'
)
$ErrorActionPreference = 'Continue'
$now = Get-Date
"== ingest status $($now.ToString('yyyy-MM-dd HH:mm:ss')) (server time)"

$task = Get-ScheduledTask -TaskName 'KalTerm-Ingest' -ErrorAction SilentlyContinue
if ($task) {
    $info = Get-ScheduledTaskInfo -TaskName 'KalTerm-Ingest'
    "task: $($task.State); last run $($info.LastRunTime.ToString('yyyy-MM-dd HH:mm:ss')); last result $($info.LastTaskResult)"
}
else {
    'task: not registered'
}

$release = if (Test-Path "$Root\release.txt") { (Get-Content "$Root\release.txt").Trim() } else { '?' }
"release: $release"
$proc = Get-Process -Name 'KalTerm.Ingest.Host' -ErrorAction SilentlyContinue
if ($proc) {
    $p = $proc | Select-Object -First 1
    "process: pid $($p.Id); up $([int]($now - $p.StartTime).TotalMinutes) min; working set $([int]($p.WorkingSet64 / 1MB)) MB; handles $($p.HandleCount)"
}
else {
    'process: NOT RUNNING'
}

$log = Get-ChildItem "$Root\logs" -Filter 'ingest-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    $text = Get-Content $log.FullName -Tail 4000
    "log: $($log.Name) ($([int]($log.Length / 1KB)) KB, last write $([int]($now - $log.LastWriteTime).TotalSeconds) s ago)"
    $statusLine = $text | Where-Object { $_ -match 'status: connected=' } | Select-Object -Last 1
    if ($statusLine) { "last status line: $($statusLine.Trim())" }
    $bad = @($text | Where-Object { $_ -match '^(warn|fail|crit):' })
    "warnings/errors in the last 4000 lines: $($bad.Count)"
    $bad | Select-Object -Last 6 | ForEach-Object { '  ' + $_.Substring(0, [Math]::Min(200, $_.Length)) }
}
else {
    'log: none'
}

$connection = New-Object System.Data.SqlClient.SqlConnection "Server=$Instance;Database=$Database;Integrated Security=SSPI;TrustServerCertificate=True;Application Name=KalTerm.Status"
try {
    $connection.Open()
    $query = {
        param($sql)
        $c = $connection.CreateCommand(); $c.CommandText = $sql; $c.CommandTimeout = 30
        $r = $c.ExecuteReader(); $rows = @(); while ($r.Read()) { $row = @(); for ($i = 0; $i -lt $r.FieldCount; $i++) { $row += $r[$i] }; $rows += , $row }; $r.Close(); , $rows
    }
    $size = & $query "SELECT SUM(size)*8/1024, SUM(CASE WHEN max_size=-1 THEN -1 ELSE max_size*8/1024 END) FROM sys.database_files WHERE type = 0"
    $dataMb = $size[0][0]; $capMb = $size[0][1]
    "database: data file $dataMb MB of cap $capMb MB"
    $counts = @{}
    foreach ($row in (& $query "SELECT o.name, SUM(p.rows) FROM sys.partitions p JOIN sys.objects o ON o.object_id = p.object_id WHERE o.name IN ('ticker','trade','market') AND p.index_id IN (0,1,5) GROUP BY o.name")) { $counts[$row[0]] = [long]$row[1] }
    "rows (approx): ticker $($counts['ticker']); trade $($counts['trade']); market $($counts['market'])"

    $stateFile = "$Root\status-last.json"
    if (Test-Path $stateFile) {
        $prev = Get-Content $stateFile -Raw | ConvertFrom-Json
        $minutes = ($now - [datetime]$prev.at).TotalMinutes
        if ($minutes -gt 0.2) {
            "since last check ($([math]::Round($minutes, 1)) min): ticker +$([long]$counts['ticker'] - $prev.ticker) ($([int](([long]$counts['ticker'] - $prev.ticker) / $minutes)) /min); trade +$([long]$counts['trade'] - $prev.trade); data file +$($dataMb - $prev.dataMb) MB"
        }
    }
    @{ at = $now.ToString('o'); ticker = $counts['ticker']; trade = $counts['trade']; dataMb = $dataMb } | ConvertTo-Json | Set-Content $stateFile

    $gaps = & $query "SELECT kind, COUNT(*), MAX(started_at) FROM dbo.ingest_gap GROUP BY kind ORDER BY kind"
    if ($gaps.Count -eq 0) { 'gaps: none' } else { $gaps | ForEach-Object { "gap $($_[0]): $($_[1]) (latest start $($_[2]))" } }
    $state = & $query "SELECT name, value, updated_at FROM dbo.ingest_state"
    $state | ForEach-Object { "state $($_[0]) = $($_[1]) at $($_[2]) UTC" }
    $statuses = & $query "SELECT status, COUNT(*) FROM dbo.market GROUP BY status ORDER BY 2 DESC"
    'markets by status: ' + (($statuses | ForEach-Object { "$($_[0])=$($_[1])" }) -join ', ')
}
catch {
    "database: cannot query ($($_.Exception.Message.Split("`n")[0]))"
}
finally {
    $connection.Dispose()
}

$drive = Get-PSDrive C
"disk C: free $([int]($drive.Free / 1GB)) GB"
