#requires -Version 5.1
<#
  Start-Plan.ps1 -- one-click "run the overnight backlog planner now" (ADR 17/19).

  The sibling of Start-Reflect.ps1: brings the pieces up IN ORDER, THROUGH THEIR SAFETY GATES,
  triggers one morning-brief run, and returns. Manual-only by design so the planner never
  competes with a late-night build / test / stream for the B70s. The brief reads the glance +
  last Reflect recap + the board-sync delta, authors what-changed / drifting / needs-a-call /
  next-slice, applies only the gated in-repo auto-safe reconciliations, and surfaces the rest.

    [1/5] vllama facade (:8090)      start `vllama serve` if it isn't listening
    [2/5] judges resident           `vllama up --model ...` for author + critic, THROUGH the
                                     22 GB host-RAM gate; if the gate refuses, STOP and report
    [3/5] ember bot (:8092 trigger)  ensure the long-running bot is up and Discord-connected
    [4/5] POST /brief (sync)         run author + critic and WAIT for them to finish
    [5/5] free the GPUs              `vllama kill-all` to release VRAM (skip with -KeepWarm)

  Double-click "Plan Now.cmd" on the Desktop, or run this directly.

  NOTE: this triggers the OVERNIGHT planner trigger (Ember:Overnight:LocalTriggerPort, default
  8092). Set that port + Ember:Overnight:Enabled + ChannelId first (see the runbook), and run
  the bot so the trigger is listening. The brief reuses the same vllama judges as Reflect.
#>

param([switch]$KeepWarm)   # -KeepWarm: leave the judges resident after the run (skip freeing VRAM)

$ErrorActionPreference = 'Stop'

# --- Paths / knobs -----------------------------------------------------------
$Vllama       = 'D:\work\vllama\src\Vllama\bin\Release\net9.0\vllama.exe'
$FacadePort   = 8090
$Judges       = @(
    @{ Model = 'qwen3-30b-a3b-128k'; Slot = 'slot-a'; Alias = 'vllama-planner' },  # author (dual-split 0,1)
    @{ Model = 'qwen2.5-14b-q4';     Slot = 'slot-b'; Alias = 'vllama-critic'  }   # critic (single card 1)
)
$EmberProject = 'D:\work\ember\src\Ember'
$TriggerPort  = 8092                       # Ember:Overnight:LocalTriggerPort
$TriggerBase  = "http://127.0.0.1:$TriggerPort"

# --- Helpers -----------------------------------------------------------------
function Say  ($m) { Write-Host $m -ForegroundColor Cyan }
function Ok   ($m) { Write-Host "  OK  $m" -ForegroundColor Green }
function Warn ($m) { Write-Host "  --  $m" -ForegroundColor Yellow }
function Die  ($m) { Write-Host "`n  XX  $m" -ForegroundColor Red; Write-Host "Nothing was triggered." -ForegroundColor Red; exit 1 }

function Test-TcpPort {
    param([int]$Port, [int]$TimeoutMs = 800)
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne($TimeoutMs) -and $c.Connected) { $c.EndConnect($iar); $c.Close(); return $true }
        $c.Close(); return $false
    } catch { return $false }
}

function Invoke-Trigger {
    param([string]$Path, [string]$Method = 'GET', [int]$TimeoutSec = 5)
    try {
        $r = Invoke-WebRequest -Uri "$TriggerBase$Path" -Method $Method -UseBasicParsing -TimeoutSec $TimeoutSec
        return [pscustomobject]@{ Connected = $true; Code = [int]$r.StatusCode; Body = ($r.Content).Trim() }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if ($resp) {
            $body = ''
            try { $body = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd().Trim() } catch {}
            return [pscustomobject]@{ Connected = $true; Code = [int]$resp.StatusCode; Body = $body }
        }
        return [pscustomobject]@{ Connected = $false; Code = 0; Body = $_.Exception.Message }
    } catch {
        return [pscustomobject]@{ Connected = $false; Code = 0; Body = $_.Exception.Message }
    }
}

function Get-VllamaStatus {
    try { return (& $Vllama status --json 2>$null | Out-String | ConvertFrom-Json) } catch { return $null }
}

function Test-Resident {
    param($Status, [string]$Model)
    if (-not $Status) { return $false }
    foreach ($s in $Status.slots) { if ($s.model -eq $Model -and $s.healthy -and $s.llama_alive) { return $true } }
    return $false
}

# --- 0. Sanity ---------------------------------------------------------------
Write-Host ''
Say '=== Overnight plan -- run now ==='
if (-not (Test-Path $Vllama))       { Die "vllama.exe not found at $Vllama (build it: dotnet build -c Release in D:\work\vllama\src\Vllama)." }
if (-not (Test-Path $EmberProject)) { Die "ember project not found at $EmberProject." }

# --- 1. vllama facade --------------------------------------------------------
Say "`n[1/5] vllama facade on :$FacadePort"
if (Test-TcpPort $FacadePort) {
    Ok 'facade already up'
} else {
    Warn 'facade down -- starting `vllama serve` ...'
    Start-Process -FilePath $Vllama -ArgumentList 'serve' -WorkingDirectory (Split-Path $Vllama)
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Test-TcpPort $FacadePort)) { Start-Sleep -Seconds 2 }
    if (Test-TcpPort $FacadePort) { Ok 'facade up' } else { Die 'facade did not come up within 60s.' }
}

# --- 2. judges resident (through the safety gate) ----------------------------
Say "`n[2/5] judges resident (author + critic)"
$status = Get-VllamaStatus
if ($status) { Write-Host ("      host RAM {0:N1} / {1} GB gate" -f $status.host_used_gb, $status.preflight_threshold_gb) -ForegroundColor DarkGray }

foreach ($j in $Judges) {
    $m = $j.Model
    if (Test-Resident $status $m) { Ok "$m resident"; continue }
    $upArgs = @('up', '--model', $m)
    if ($j.Slot) { $upArgs += @('--slot', $j.Slot) }
    Warn "$m not resident -- ``vllama $($upArgs -join ' ')`` (preflight + gate) ..."
    & $Vllama @upArgs
    if ($LASTEXITCODE -ne 0) {
        Die "vllama refused to bring up $m (exit $LASTEXITCODE) -- most likely the 22 GB host-RAM gate. Close the stream / free RAM and re-run."
    }
    $status = Get-VllamaStatus
    if (Test-Resident $status $m) { Ok "$m up" } else { Die "$m still not healthy after ``up``." }
}

# --- 2b. serve-level readiness gate (vllama ADR-0007) ------------------------
Say '      verifying judges can serve through the facade (`vllama ready`) ...'
$readyJson = (& $Vllama ready | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    if ($readyJson) { Write-Host $readyJson -ForegroundColor DarkGray }
    Die "a judge alias cannot serve through the facade (``vllama ready`` exit $LASTEXITCODE). Refusing up front rather than firing a brief into a 503."
}
Ok 'both judges serve through the facade'

# --- 3. ember bot + trigger --------------------------------------------------
Say "`n[3/5] ember bot (overnight trigger on :$TriggerPort)"
$ready = Invoke-Trigger '/ready'
if (-not $ready.Connected) {
    $proc = Get-Process -Name Ember -ErrorAction SilentlyContinue
    if ($proc) {
        Die "Ember.exe is running but the overnight trigger isn't answering on :$TriggerPort. Set Ember:Overnight:LocalTriggerPort and restart the bot -- close its window and re-run me."
    }
    Warn 'bot down -- starting it (`dotnet run`) ...'
    Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', $EmberProject) -WorkingDirectory $EmberProject
}

$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $deadline) {
    $ready = Invoke-Trigger '/ready'
    if ($ready.Connected -and $ready.Code -eq 200) { break }
    $why = if ($ready.Connected) { $ready.Body } else { 'starting' }
    Write-Host "      ... waiting for bot ($why)" -ForegroundColor DarkGray
    Start-Sleep -Seconds 3
}
if (-not ($ready.Connected -and $ready.Code -eq 200)) {
    Die "bot not ready within 180s (last: $($ready.Body)). Check the bot window for errors."
}
Ok 'bot up and Discord-connected'

# --- 4. trigger one run (synchronous: returns when author + critic finish) ----
Say "`n[4/5] trigger the brief -- author + critic run now, this takes a few minutes ..."
$fire = Invoke-Trigger '/brief' 'POST' 1200
if ($fire.Connected -and $fire.Code -eq 200) {
    Write-Host "`n  >>  Brief done: $($fire.Body)" -ForegroundColor Green
    Write-Host "      Read it in the brief channel and react to label it:  check = accurate  pencil = partial  X = wrong" -ForegroundColor Green
} elseif ($fire.Connected -and $fire.Code -eq 409) {
    Warn "an overnight run was already in progress -- not starting a second, and leaving vllama up."
    Write-Host ''
    exit 0
} else {
    Die "trigger failed (code $($fire.Code): $($fire.Body))."
}

# --- 5. free the GPUs (unless -KeepWarm) -------------------------------------
if ($KeepWarm) {
    Say "`n[5/5] -KeepWarm set -- leaving the judges resident."
} else {
    Say "`n[5/5] freeing GPU VRAM (vllama kill-all) ..."
    & $Vllama kill-all | Out-Null
    Start-Sleep -Seconds 1
    $after = Get-VllamaStatus
    if ($after) {
        $live = @($after.slots | Where-Object { $_.llama_alive }).Count
        Write-Host ("  OK  VRAM released -- resident llama working set {0} GB, live slots {1}" -f $after.tracked_resident_llama_working_set_gb, $live) -ForegroundColor Green
    } else {
        Ok 'kill-all issued'
    }
    Write-Host "      (the vllama facade on :$FacadePort stays up -- it holds no VRAM and speeds the next run)" -ForegroundColor DarkGray
}
Write-Host ''
