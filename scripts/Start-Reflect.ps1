#requires -Version 5.1
<#
  Start-Reflect.ps1 -- one-click "run a constellation recap now" (ADR 17).

  Brings the pieces up IN ORDER, THROUGH THEIR SAFETY GATES, then triggers one recap and
  returns. Manual-only by design: nothing here runs unattended, so a recap never competes with
  a late-night build / test / stream for the B70s.

    [1/5] vllama facade (:8090)      start `vllama serve` if it isn't listening
    [2/5] judges resident           `vllama up --model ...` for planner + critic, THROUGH the
                                     22 GB host-RAM gate; if the gate refuses, STOP and report
    [3/5] ember bot (:8091 trigger)  ensure the long-running bot is up and Discord-connected
    [4/5] POST /reflect (sync)       run the judges and WAIT for them to finish
    [5/5] free the GPUs              `vllama kill-all` to release VRAM (skip with -KeepWarm)

  Double-click "Reflect Now.cmd" on the Desktop, or run this directly.
#>

param([switch]$KeepWarm)   # -KeepWarm: leave the judges resident after the run (skip freeing VRAM)

$ErrorActionPreference = 'Stop'

# --- Paths / knobs -----------------------------------------------------------
$Vllama       = 'D:\work\vllama\src\Vllama\bin\Release\net9.0\vllama.exe'
$FacadePort   = 8090
$Judges       = @(
    @{ Model = 'qwen3-30b-a3b-128k'; Slot = $null   },   # vllama-planner (slot-a default; dual-split)
    @{ Model = 'qwen2.5-14b-q4';     Slot = 'slot-b' }    # vllama-critic  (MUST pin --slot slot-b, else
)                                                         # it defaults to slot-a/8080 and collides)
$EmberProject = 'D:\work\ember\src\Ember'
$TriggerPort  = 8091
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
Say '=== Reflect -- run now ==='
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
Say "`n[2/5] judges resident (planner + critic)"
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

# --- 3. ember bot + trigger --------------------------------------------------
Say "`n[3/5] ember bot (trigger on :$TriggerPort)"
$ready = Invoke-Trigger '/ready'
if (-not $ready.Connected) {
    $proc = Get-Process -Name Ember -ErrorAction SilentlyContinue
    if ($proc) {
        Die "Ember.exe is running but the trigger isn't answering on :$TriggerPort. It predates this feature or LocalTriggerPort isn't set -- close its window and re-run me to restart it."
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

# --- 4. trigger one run (synchronous: returns when the judges finish) ---------
Say "`n[4/5] trigger the recap -- the judges run now, this takes a few minutes ..."
$fire = Invoke-Trigger '/reflect' 'POST' 1200
if ($fire.Connected -and $fire.Code -eq 200) {
    Write-Host "`n  >>  Recap done: $($fire.Body)" -ForegroundColor Green
    Write-Host "      Watch #reflect and react to label it:  check = accurate  pencil = partial  X = wrong" -ForegroundColor Green
} elseif ($fire.Connected -and $fire.Code -eq 409) {
    Warn "a reflect run was already in progress -- not starting a second, and leaving vllama up."
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
