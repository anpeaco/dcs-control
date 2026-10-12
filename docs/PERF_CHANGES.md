# Stutter fixes: change log and validation

Every change made to remove a cause of delay or stutter, with what to look at after a flight to see whether it worked, and how to switch it back if performance gets worse. One change per row; the **Status** column is updated after each test flight. Keep the order of the A/B flights below: one change per flight, same mission, same route, Virtual Desktop's performance overlay on.

All of these are in release 0.5.6 preview as built on 12 October 2026 (OFXR layer with fork patches 0001-0022). Status values: **Not flown** (built and tested offline only), **In flight** (installed and flown, but not isolated by an A/B), **Confirmed** (a flight showed the expected effect), **No effect** (flown, nothing measurable), **Regressed** (flown, worse: switched back, see the entry).

## Baseline

The 11 October 2026 22:43 flight (Quest 3 over Virtual Desktop at 72 Hz, profile `vd-fg-boost-opt`, 2×, Match refresh at 36 FPS, OFXR fork 0.2.9.1 with patches 0001-0017, about 6 minutes). From OFXR's log:

| Signal | Count |
| --- | --- |
| Headset swapchain made again during flight (`d3d11_bridge` result 9) | 6 |
| Headset swapchain given back after 5 s unused (`d3d11_bridge` result 10) | 6 |
| Frame generation restarted (`continuity_reset`) | 7 |
| Generation refused as busy | 2 |
| Generated frame handed over before drawn (`presenter_transition` result 402) | 11,703 (nearly every one) |
| Busy history slots, adaptive switches, image waits over 2 ms | 0 |

DCS was capped at 36 FPS (27.8 ms frame slot). The engine optimizations' in-flight switch showed no measurable difference because the scene was light and the cap hid any gain.

## Changes

| ID | Change | Where (commit) | Switch to undo it | Status |
| --- | --- | --- | --- | --- |
| S1 | Overlay no longer holds its lock across the runtime's xrEndFrame | OFXR fork patch 0019 (8e10a49) | none (locking only) | In flight |
| S2 | Headset swapchains kept once made | OFXR fork patch 0020 (8e10a49) | Framegen → Advanced → **Keep headset images once made** | Confirmed (12 Oct 13:37) |
| S3 | OFXR flight log written by a background thread | OFXR fork patch 0021 (8e10a49) | none; `flush_each_event=1` makes it synchronous again | In flight |
| S4 | Spare image for in-place reading, and a count of the waits | OFXR fork patch 0022 (8e10a49) | Framegen → Advanced → **Spare image for in-place reading** | In flight: 0 waits |
| S5 | Terrain prefetch fix yields instead of sleeping | engine module, `prefetch_fix.h` (b72e738) | Engine Optimizations → Terrain prefetch fix → Observe (compares with no skipping) | In flight |
| S6 | Shadow-plan wait on DCS's render thread capped at 2 ms (was 20 ms) | engine module, `shadow_batch.h` (b72e738) | none | In flight (within S14's budget) |
| S7 | CPU Boost leaves affinity alone on a single-CCD CPU | `CpuTopology.cs` (8e10a49) | turn CPU Boost off to compare | Confirmed (12 Oct 13:37) |
| S8 | Flight helper asks for every process's GPU time every 30 s (was 5 s) | `FlightMonitor.cs` (8e10a49) | CPU Boost → **Record CPU and GPU use** off to compare | In flight |
| S9 | Late frame skip off in the profile | profile `vd-fg-boost-opt` (draft, not in git) | Framegen → Advanced → **Late frame skip** | Not tested (only acts in 3×) |
| M1 | Last flight shows **Hitch signals** | `LastFlight.cs`, overview card (8e10a49) | measurement only | Working |
| S10 | Synthesis waits on the GPU for the hand-over copy of a staging texture before writing it again (R7) | OFXR fork patch 0023 (d93c5e2) | none (ordering fix) | In flight: 2 waits, both outside steady flight |
| S11 | The queue the headset runtime judges frames by at HIGH priority (R2) | OFXR fork patch 0024 (d93c5e2) | Framegen → Advanced → **Headset queue at high priority** | Confirmed, partly (12 Oct 13:37) |
| S12 | No D3D11 GPU wait queued for an image read that has already completed (R13) | OFXR fork patch 0026 (d93c5e2) | none | In flight |
| S13 | Launch/Apply redeploys when this version writes a component differently, though the profile is unchanged (R4) | `ControlService.cs` (d93c5e2) | none (correctness) | Confirmed (the 13:37 flight ran the new OFXR: 406 records exist only with 0025) |
| S14 | One render-thread wait budget per frame for the engine module (R5), with every render-thread wait counted | engine module, `render_wait.h` (aed1158) | Engine Optimizations → Advanced → **Render-thread wait budget** (0 = off) | In flight: render-thread waits max 0.40 ms per frame |
| S15 | Lighter recorder threads (R6): 1 helper per job, below normal priority, no wake-up boost, no extra helper after a late job | engine module, app (aed1158) | Engine Optimizations → Advanced → **Lighter recorder threads** (off by default) | Installed, off: not flown |
| M2 | Last flight: steady-flight **Late frames**, **Missed refreshes**, **Hand-over** rows; generated frames shown late (406, patch 0025) and staging waits (705) in Hitch signals; each flight's summary kept in `%LOCALAPPDATA%\DcsControl\flights` | `LastFlight.cs`, OFXR fork patch 0025 (d93c5e2) | measurement only | Working; see M3 |
| M3 | Steady seconds leave out time the headset session was not FOCUSED (a headset or Virtual Desktop menu); S14's "frames spent the budget" counts only waits that ran to the budget's limit | `LastFlight.cs`, engine module `render_wait.h` (uncommitted) | measurement only | Checked on the 13:37 log |
| S16 | Frame limit **Very high (300 FPS)** instead of Match refresh, so OFXR alone paces DCS (R3); for the 406 bursts and slow hand-overs of the 13:37 flight, which did not follow GPU load | profile `vd-fg-boost-opt` (draft, not in git) | Frame limit → **Match refresh** | One run per side on the perf-test mission (14:16 Very high, 15:19 Match refresh): Very high better at the headset (missed refreshes 16.6 against 23.3/min, 13.8 against 19.3 without Virtual Desktop's display-time corrections (R20, M6), 406 0.3 against 5.7/s, no limiter beat); Last flight's late-frame count favours Match refresh (see the 15:19 section for why). Leaning Very high; a second run per side still to fly. Draft is now on Very high at 90 Hz 3× (15:36) |
| M4 | Perf-test mission times its scripts: every scheduled function and event handler call is bracketed by `PERFT>n name` / `PERFT<n` lines in dcs.log (the mission environment has no clock) | `scripts/make-perf-mission.py`, test `scripts/test-perf-mission-timing.py` | `--no-timing` rebuilds the mission without it (byte for byte the previous one) | Flown 12 Oct 15:19: 2,099 calls timed; RadioVoice and Civilianai calls of 27-45 ms behind several late frames |
| M5 | Late-frame report across OFXR's log, dcs.log (with M4's timings), the flight monitor and an optional WPR trace: each late frame split along DCS's main and render threads | `scripts/analyze-flight-spikes.py`, `tools/FlightTrace` (TraceProcessor), `scripts/record-flight-trace.ps1` | measurement only | Report checked on the 13:37, 14:16 and 15:19 logs; it times late frames from xrWaitFrame entries, Last flight from returns (the counts differ under Match refresh); trace analysis tested only up to loading a trace (needs a recorded CPU trace) |
| M7 | Last flight: a dip of up to 3 s under the steady rate (each second still 10 frames or more) between steady seconds stays in steady flight, so freezes are counted (R22); **Freezes** row (frames of 100 ms or more, the longest); Hitch signals counts the headset runtime's xrEndFrame over 30 ms (R19); the engine switch is compared only within the mission load it was used in, other loads' seconds reported as left out (R23) | `LastFlight.cs`, `app.js` (uncommitted) | measurement only | Re-read 17:36 (both missions): steady 717 → 852 s, late 24.1 → 23.7/min, missed 24.2 → 67/min, 38 freezes (longest 628 ms), 67 runtime blocks. Installed and flown 18:17: Freezes and runtime blocks counted the one freeze in the log, but the row also counts frames after the mission ended; the engine card was empty because OFXR's log had wrapped (R24) |
| M6 | Last flight: a frame is late past a quarter of its slot in 2× and 3× alike (as M5 counts), then split by refreshes; display-time steps under two refreshes and off the refresh grid by more than 0.15 counted as **display-time corrections**, not missed or doubled (R20); seconds out of FOCUSED are not flying (Rendered FPS, load) (R21) | `LastFlight.cs`, `app.js` (Missed refreshes row) | measurement only | Re-read: 2× late counts unchanged (14:16 59, 15:19 36); 15:36 late 77 → 40, missed 58 → 41, doubled 12 → 2, Rendered FPS 34.2 → 29.6; 14:16 missed 48 → 40, 15:19 75 → 62 |

### S1: overlay lock off the runtime's frame call (OFXR 0019)

- **What changed**: the in-headset counter and diagnostic panel (`openxr_fps_overlay.cpp`) held one lock across the runtime's `xrEndFrame`, across the quarter-second redraw (an ini read, the panel rasterized, the upload) and across every status setter. xrEndFrame now reads a copy of the overlay's layers made after each change and takes no lock the application thread holds; only the overlay's own swapchain calls are kept apart from an xrEndFrame that shows its images.
- **Why**: on the presenter thread, a slow Virtual Desktop frame call held up DCS's next frame (the setters run every frame on DCS's thread), and a redraw held up a generated frame's hand-over. Confirmed in code; its size in flight was not measured.
- **Expected**: fewer long `app_end_frame` times; smoother frame pacing with the panel shown.
- **Check**: Last flight → Spare time (the "ms handing each frame to frame generation" figure) and Frame pacing p99; compare with the panel shown and hidden.
- **Tests**: a new overlay test blocks the runtime's xrEndFrame on one thread and checks the application thread's overlay calls still return (fails on the old code).

### S2: keep headset swapchains (OFXR 0020)

- **What changed**: `lazy_runtime_reclaim_ms=0` now means never give a runtime swapchain back once made. The app writes it (switch on by default); off writes 5000, the old behaviour.
- **Why**: in the baseline flight the runtime swapchain was destroyed after 5 s unused and made again at the next frame DCS showed as its own (pause, menu, a hitch) 6 times: an `xrCreateSwapchain` plus a bounded image wait on DCS's frame thread each time.
- **Expected**: Hitch signals "headset images made" at most once per swapchain, "given back" 0. Costs the runtime images' VRAM after the first such frame (Last flight → Video memory).
- **Check**: Last flight → Hitch signals; Video memory peak.

### S3: flight log off DCS's frame threads (OFXR 0021)

- **What changed**: while a session runs, OFXR's log records are formatted and queued on the calling thread, and one background thread writes them (every 50 ms or 64 KB) and polls the ini's `logging_enabled` in place of DCS's xrEndFrame. Outside a session, and with `flush_each_event=1`, writes stay synchronous. The writer is stopped at xrDestroySession / xrDestroyInstance and writes everything queued first.
- **Why**: about 1,300 synchronous file writes a second on DCS's render and presenter threads, plus a stall for every thread when the 256 MB file wraps (truncated and rewritten under an exclusive lock).
- **Expected**: measured offline, the caller's cost per record from 2.5 µs to 0.5 µs; the wrap and any disk stall no longer reach DCS's threads. Up to 50 ms of records can be lost if DCS crashes.
- **Check**: compare a flight with the log on against one with it off (Framegen → In-headset diagnostics → OFXR log), using Virtual Desktop's overlay, since Last flight's per-frame figures need the log.

### S4: spare image for in-place reading (OFXR 0022)

- **What changed**: with `history_in_place`, DCS's swapchains get one image more than the runtime's (`in_place_spare_image=1`, written by the app; the layer's own default stays off). The bridge now reports when DCS's write of an image had to wait on the GPU for frame generation still reading it, logged as `d3d11_bridge` result 15.
- **Why**: with only the runtime's image count, the image DCS writes next can be the one the previous pair's synthesis is still reading; DCS's GPU work then waits for it.
- **Expected**: few or no in-place waits; one more image of VRAM per DCS swapchain.
- **Check**: Last flight → Hitch signals ("waits to reuse an image"): compare with the switch on and off. If waits are 0 with the switch off too, the spare is not needed.
- **Note**: covered by the bridge's unit test (`make_virtual(1)`); the layer's call-chain tests assume exactly three images, so the first flight with the switch on is its first run through the whole chain.
- **Review (12 Oct)**: result 15 is decided on the CPU, which runs ahead of the GPU, so it overcounts real GPU stalls; in 2× deep at 36 FPS the image DCS writes next was read by a pair that finished ~3 ms after a frame earlier, so the spare is likely unneeded (2 × 36 MB). Candidate to turn off by default once a flight confirms (R8).

### S5: prefetch spin guard yields (engine module)

- **What changed**: after 256 skipped prefetch calls in a row on one thread, the fix now calls `SwitchToThread()` (lets a ready thread run, never sleeps) instead of `Sleep(1)`.
- **Why**: the guard's 10 ms test used GetTickCount64 (15.6 ms resolution), so it almost always passed, parking one of DCS's terrain workers for a timer tick (1-15.6 ms) every 256 skips, at about 840,000 calls a second.
- **Expected**: less terrain streaming lag; DCS CPU unchanged or lower.
- **Check**: Last flight → Terrain prefetch fix (skipped %), Frame pacing p99 in terrain-heavy low flight; A/B against Observe.

### S6: shadow-plan wait capped at 2 ms (engine module)

- **What changed**: when the planner thread is still building a shadow cascade's plan, DCS's render thread waits at most 2 ms (was 20 ms), then builds it itself.
- **Check**: `Saved Games\DCS\Scripts\DcsQvCull\DcsQvCull.log`, line "shadow plan …: render-thread waits N (… max X ms, T timeouts)": the max should stay under about 2 ms plus one build; timeouts show how often the cap was reached.
- **Review (12 Oct)**: the cap is per cascade pass, so up to 4 × 2 ms per frame, and on a timeout the render thread builds the plan again while the planner still builds it. Follow-up R5.

### S7: CPU Boost on a single-CCD CPU

- **What changed**: when dcs.log says the CPU has one L3 cache and one efficiency class (9800X3D), CPU Boost no longer ranks cores: only DCS's priority changes.
- **Why**: DCS's "performance classes" there only rank identical cores by boost clock. CPU Boost confined Virtual Desktop's Streamer to the 4 "common" cores (mask 0xFC30) and about 40 background apps to logical CPUs 12-13, inside the Streamer's cores.
- **Check**: CPU Boost → What Boost will do (should say affinity is not changed); Last flight → PC during flight; Virtual Desktop's overlay (network/encode latency).

### S8: flight helper's GPU engine query every 30 s

- **What changed**: the "GPU Engine" performance counters (Windows asks the graphics kernel for every process's time on every engine) are read every 30 s instead of every 5 s. DCS's own GPU share is in OFXR's per-frame frame_load records anyway.
- **Check**: Last flight → PC during flight still names another busy GPU process when there is one.

### S9: late frame skip off

- **What changed**: `ofxrLateFrameSkip` set to false in the draft of profile `vd-fg-boost-opt` (it was on; the app's default is off). Takes effect at the next Apply.
- **Why**: in 3× it deliberately shows a real frame twice when the first generated frame is late: a visible judder by design.
- **Check**: in 3×, Headset FPS and frame pacing; it does nothing in 2×.

### M1: Hitch signals on Last flight

Counts over the flying seconds: headset images made (result 9) and given back (10), waits to reuse an image (15), frame generation restarts (continuity_reset), and generated frames handed over before they were drawn (402). The row is amber when an image was given back or there were more waits than seconds.

### S10: staging copy fence (OFXR 0023)

- **What changed**: the hand-over copier remembers the fence value of each staging texture's last copy (it reads the staging on the runtime's queue); before synthesis writes a staging texture again, its queue waits on the GPU for that value when the copy has not run yet. Logged as `presenter_transition` 705.
- **Why**: nothing ordered the copy before the next write of the same staging (reuse was decided on the CPU, the synthesizer's fence only covers its own writes); with the runtime's queue 15 ms behind (R2) and up to 311 ms in one hitch, a later frame could be copied into an earlier slot.
- **Expected**: 705 rare (only when the runtime's queue lags several frames); no time jumps in the headset.
- **Check**: Hitch signals "waits for a hand-over copy".

### S11: headset queue at high priority (OFXR 0024)

- **What changed**: with Separate submission queue on, the queue OFXR gives the runtime (which it judges each frame's completion by) is created at HIGH priority, like OFXR's bridge and synthesis queues (`binding_queue_high_priority=1`; layer default off, the app turns it on).
- **Why**: in the baseline flight the runtime's queue passed each generated frame's hand-over a median 15 ms later (p90 16.6 ms, 62% later than one refresh), although synthesis had finished ~19 ms before; its work is small (copies, joins, the runtime's waits).
- **Expected**: Hand-over row "headset queue past each generated frame … ms after it" well under one refresh; fewer missed refreshes.
- **Check**: Last flight → Hand-over and Missed refreshes, with the switch on and off.

### S12: no needless write waits (OFXR 0026)

- **What changed**: before DCS writes an image, the bridge queues a D3D11 fence wait only if the layer's last read of it has not completed.
- **Why**: queued every time (twice a frame), each wait could split DCS's command buffer on its render thread for nothing.
- **Check**: no direct flight figure; Last flight → Limited by (CPU time per frame) and Hand-over p99.

### S13: redeploy when components change (app)

- **What changed**: when the profile is unchanged and the installed files are intact, Launch/Apply now also compares the installed files with what this version would write (bytes for files it owns whole, owned values for shared inis) and applies again on any difference, naming the file.
- **Why**: a new OFXR build or ini key shipped with an update was never deployed while the profile stayed the same (seen on this PC: the 12 Oct OFXR DLL and the S2/S4 ini keys were not installed).
- **Tests**: an install from an older build (a launch.json this build writes differently) is applied again, then reported unchanged.

### M2: steady-flight figures (app; OFXR 0025)

- **Steady seconds**: at 90% or more of the rate OFXR paced DCS to, and so were the 2 s either side (pauses, loading screens and their edges left out).
- **Late frames per minute**, split by how many headset refreshes late (frame intervals fall into refresh-period steps, so this counts every late frame; "stutters" over twice the median counted about half).
- **Missed refreshes per minute** and doubled refreshes, from the display times OFXR submitted.
- **Hand-over**: DCS's xrEndFrame p99 / p99.9 / count over 30 ms, and the runtime queue's lag after each generated frame (log on).
- **Hitch signals** adds 406 (a generated frame still unwritten a refresh after its hand-over: the headset showed an older image), 705, display-time steps and clock clamps.
- **Baseline re-read with M2** (11 Oct 22:43): 254 steady seconds of 339 flying; 19.8 late frames/min (29 one refresh late, 30 two, 25 three or more); 41.1 missed refreshes/min; hand-over p99 21.3 ms, p99.9 25.4 ms, 5 over 30 ms; runtime queue lag 15.0 ms p50, 16.6 ms p90, 62% later than one refresh; no headset image rebuilds in steady flight (all six were in pauses).

### S14: render-thread wait budget (engine module, R5)

- **What changed**: every wait DCS's render thread makes for the module's worker threads (shadow and G-buffer recorder jobs at their pass and stale jobs at arming, G-buffer re-recorded segments, shadow plans still building) now also stops at what is left of one budget per frame (`[Model] RenderWaitBudgetUs`, 1000 µs by default; 0 = each keeps only its own cap). Past it, the pass is drawn as stock, which each already did on its own timeout. A frame is the time between two of DCS's collects.
- **Why**: each wait had a cap of its own (200 µs to 2 ms, the shadow-plan one per cascade) and none knew of the others: more than 13 ms in one frame at worst; the stale-job spins were not counted anywhere.
- **Expected**: no frame waits more than ~1 ms on the module; somewhat more passes drawn as stock in heavy frames (the recorders' "late" counts).
- **Check**: `DcsQvCull.log`, every 10 s: "render-thread waits 10 s: … per frame p50 / p99 / max; N frames spent the budget; time per site". Last flight → Late frames/min, Limited by (CPU). A/B: budget 1000 against 0.
- **Tests**: budget sharing, a new frame's budget, spins giving up at once when spent, statistics; the module's suites unchanged.

### S15: lighter recorder threads (engine module and app, R6)

- **What changed**: one switch writes `ShadowRecorderHelpers=1`, `GBufferRecorderHelpers=1`, `ShadowRecorderPriority=-1`, `GBufferRecorderPriority=-1` (new key), `RecorderPriorityBoost=0` (new: SetThreadPriorityBoost on every worker) and `ShadowRecorderBoostJobs=0` (new: was a fixed 240 jobs with one more helper after each late job).
- **Why**: 28 recorder threads at normal priority on 16 logical CPUs beside DCS's workers and Virtual Desktop's streamer; each wake-up boosted them past DCS's threads; a late job added helpers for ~6.7 s, which made the next jobs later (a feedback loop).
- **Expected**: either fewer late frames (less contention) or more passes drawn as stock (fewer helpers): the A/B decides the default.
- **Check**: Late frames/min and Limited by (CPU ms p90) with it on and off; `DcsQvCull.log` recorder "late" counts and the render-thread wait line.
- **Not changed yet**: the workers' own spin waits (up to 20 ms, with SwitchToThread) and the ~100 kernel calls per frame at arming (review R6/R5 follow-ups).

## Flight 12 October 2026 13:37 (first flight of the new build)

Same profile as the baseline, all changes above installed except S15 (off) and S9 (2× flight). Golan Valley of Tears v5, F-15E air start, about 5 minutes. **Not an A/B**: one run, a different route and a busier mission phase than the baseline, everything changed at once. It shows which changes do what they should; it cannot show which change made the difference.

| Figure (steady seconds) | Baseline 11 Oct | 12 Oct 13:37 |
| --- | --- | --- |
| Steady seconds | 254 | 158 (170 before M3) |
| Late frames / min | 19.8 | **12.2** (21 one refresh late, 6 two, 5 three or more) |
| Missed refreshes / min | 41.1 | **22.8** (121.8 before M3) |
| Hand-over p99 / p99.9 | 21.3 / 25.4 ms | 23.5 / **40.1 ms** |
| Hand-overs over 30 ms | 5 | **17** |
| Headset queue after each generated frame, p50 / p90 | 15.0 / 16.6 ms | **9.6** / 16.8 ms |
| ... later than one refresh | 62% | **22%** |
| Generated frame still unwritten a refresh after hand-over (406) | not logged | 1,187 (7.5/s, about 1 in 5 generated frames) |
| Headset images made / given back (whole flight) | 6 / 6 | 2 (the first two) / 0 |
| In-place waits (15), staging waits (705) | 0 / not logged | 0 / 2 (outside steady flight) |
| CPU Boost processes moved | 41 | 0 |

What the log shows:

- **Menu gap, not a stutter** (fixed in M3): from 96.2 s to 104.0 s the session was VISIBLE, not FOCUSED (dcs.log 12:39:10.5 to 12:39:18.3: a Quest or Virtual Desktop menu, or the headset lifted). DCS kept 36 FPS but sent no projection layer, so every frame restarted generation (278 `continuity_reset`, prepare reason 1 "empty mappings") and showed for two refreshes. Last flight counted those 7.7 s as steady flight: 278 restarts and about 285 of the 345 missed refreshes came from them.
- **Hand-over tail is worse**: the long DCS xrEndFrame calls come in two bursts, 130-135 s (up to 150 ms) and 189-194 s (up to 109 ms), plus single ones. The engine module's render-thread waits stayed under 0.4 ms per frame throughout, and its shader compiles were finished by 13:39:52 (122 s), so neither explains them. Next: R9 (presenter lock across Virtual Desktop's calls) and what DCS was doing then (both are after the battle's H-hour).
- **406 grows with the scene**: 0-5 per 20 s in the first 100 s, 63-203 per 20 s mid-flight, 333-359 per 20 s in the last minute (about half the generated frames). The headset then shows the older image (R11). This is the largest remaining visible effect after the late frames.
- **S14's counter was wrong**: every shadow-plan wait was counted as "spent the 1.00 ms budget" (49 of 49, 62 of 62 …) because the plan wait's own 2 ms cap is above the budget; the waits themselves stayed under 0.4 ms. Fixed in M3; the budget's behaviour is unchanged.

Next flights use **`Golan_Valley_of_Tears_v5_PERF_TEST.miz`** (made by `scripts/make-perf-mission.py`): the same F-15E, immortal with unlimited fuel, starting at 4000 m heading north over both kill boxes with the autopilot engaged by the mission (heading and altitude hold), and the helicopter dice fixed. Same throttle position every run; stay out of the headset menus while recording.

## Flight 12 October 2026 14:16 (S16: frame limit Very high)

First flight on `Golan_Valley_of_Tears_v5_PERF_TEST.miz`. Same build and profile as 13:37, except Frame limit **Very high (300 FPS)**. OFXR still pacing DCS at 36 FPS: interval p50 27.8 ms, 34.9 rendered FPS. 174 steady seconds, out of about 80-250 s of the log. The 14:11 launch left no OFXR log and still had Match refresh, so it is not counted.

**Not an A/B**: 13:37 was on the regular mission. GPU load was close (DCS GPU p50 19.0 against 18.3 ms), but the next flight has to be Match refresh on the perf-test mission before S16's Status can change.

| Figure (steady seconds) | 13:37 (Match refresh, regular mission) | 14:16 (Very high, perf-test mission) |
| --- | --- | --- |
| Late frames / min | 12.2 (21 one refresh late, 6 two, 5 three or more) | **20.3** (43 / 10 / 6) |
| Missed refreshes / min | 22.8 | **16.6** |
| Generated frames handed over unwritten (402) | about 5,500 (~33/s) | **224** (1.3/s) |
| ... still unwritten a refresh later (406) | 1,187 (7.5/s) | **56** (0.3/s) |
| Headset queue after each generated frame, p50 / p90 | 9.6 / 16.8 ms | **0.5 / 1.2 ms** |
| ... later than one refresh | 22% | **1%** |
| DCS xrWaitFrame p50 / p90 | 7.3 / 17.3 ms | 0.6 / 3.7 ms |
| DCS xrEndFrame p50 | 16.1 ms | 22.9 ms (see below) |
| CPU ms per frame p50 / p90 / p99 | 6.0 / 8.6 / 27.6 | 5.2 / 6.8 / **15.1** |
| Staging waits (705), continuity resets | 2 / 278 (menu gap) | 6 / 1 |

What the log shows:

- **R3's hypothesis holds for the hand-over**: with DCS no longer pacing itself, OFXR's xrEndFrame holds DCS (~22 ms) and releases it at a fixed point of the presenter's cycle. Each pair now arrives with time to be synthesized: almost no generated frame goes out unwritten, and the headset's queue passes it within a millisecond. In the 13:37 flight, 406 rose with the scene. This flight's GPU load was similar and 406 stayed low throughout.
- **Hand-over figures are not comparable between frame-limit modes**: under Very high, OFXR's pacing hold is inside DCS's xrEndFrame (it was in xrWaitFrame, plus DCS's own limiter), so "hand-overs over 30 ms" (104) and xrEndFrame p99 now mostly measure the hold. Last flight should report the hold separately (M2 follow-up).
- **Late frames, split along DCS's threads** (M5, corrected the same day: a first reading put them on DCS CPU spikes and on one unexplained OFXR stall). DCS's render thread runs a frame behind its main thread, and xrWaitFrame does not return before the previous frame is begun (OpenXR's rule). Every long xrWaitFrame of this flight returned within about 10 µs of the render thread entering xrBeginFrame: the main thread was waiting for the render thread, not for OFXR. Each late frame's time over the slot (49 frames, 823 ms in all) is therefore split into:

  | Part | Usual (median) | Grown in late frames | Largest part in |
  | --- | --- | --- | --- |
  | xrEndFrame: OFXR's pair hold, under Very high | 22.9 ms | 334 ms | **23** frames |
  | Render thread from xrBeginFrame to xrEndFrame (DCS's drawing, OFXR's swapchain calls) | 4.6 ms | 454 ms | 10 |
  | Main thread outside xrWaitFrame (simulation, scripts) | 27.2 ms | 130 ms | 8 |
  | Render thread between xrEndFrame and the next xrBeginFrame | 0.3 ms | 300 ms | 8 |

  The 13:37 flight (Match refresh, regular mission) splits the same way: xrEndFrame largest in 22 of 44.
- **Long xrEndFrame is OFXR's hold running long, after Virtual Desktop held up the presenter**: 25 of the 27 xrEndFrame calls over 30 ms were the pair hold (up to 61 ms), not the runtime. Around 234.8 s the presenter's call into Virtual Desktop's xrEndFrame took 7.4 ms, then its xrWaitFrame 20.8 ms, and the next predicted display time skipped a refresh; the pair is released a refresh or more later, and DCS with it: R19.
- **The 83.2 ms stall at 183.6 s** was the render thread spending 83.9 ms between xrEndFrame and xrBeginFrame, outside OpenXR's frame calls (DCS's own work, or a swapchain call OFXR does not log), with the GPU idle: R18.
- **Scripts**: the breach order (29 ms between its two dcs.log lines, a CHOREO step that gives Israeli artillery fire missions) made the late frame at 184.6 s; the "RADIO S" call at 234.56 s comes just before the 234.8 s cluster. dcs.log and OFXR's log line up to a few ms on the session's IDLE->READY change, which both record.
- **One refresh late doubled** (21 → 43): to judge only once Match refresh has flown the same mission.

Next flights: (1) Frame limit **Match refresh** on the perf-test mission (now with M4), no trace: the other side of S16. (2) One traced flight (M5, `scripts/record-flight-trace.ps1`, from an administrator PowerShell), for R18 and R19; tracing costs CPU of its own, so it is not an A/B side.

## Flight 12 October 2026 15:19 (S16: Match refresh, the other side)

Same perf-test mission (now with M4's script timing), build and profile as 14:16, except Frame limit **Match refresh**. Interval p50 27.8 ms, 35.3 rendered FPS, 193 steady seconds, no menu gaps (0 continuity resets).

| Figure (steady seconds) | 14:16 (Very high) | 15:19 (Match refresh) |
| --- | --- | --- |
| Late frames / min, Last flight (xrWaitFrame **returns**) | 20.3 (43 / 10 / 6) | **11.2** (21 / 9 / 6) |
| Late frames / min, M5 report (xrWaitFrame **entries**) | **16.9** | 25.8 |
| Missed refreshes / min | **16.6** | 23.3 |
| Generated frames handed over unwritten (402) | **224** (1.3/s) | 3,640 (18.9/s) |
| ... still unwritten a refresh later (406) | **56** (0.3/s) | 1,099 (5.7/s) |
| Headset queue after each generated frame, p50 / p90 | **0.5 / 1.2 ms** | 1.3 / 16.7 ms |
| ... later than one refresh | **1%** | 17% |
| DCS xrWaitFrame p50 / p90 | 0.6 / 3.7 ms | 10.6 / 21.2 ms |
| DCS xrEndFrame p50; over 30 ms | 22.9 ms; 104 (mostly the pair hold) | 13.5 ms; 4 |
| Display-time steps | 0 | 9 |
| CPU ms per frame p50 / p90 / p99 | 5.2 / 6.8 / 15.1 | 5.7 / 8.1 / 17.0 |
| DCS GPU ms p50 / p90 | 19.0 / 21.1 | 18.5 / 20.4 |

What the logs show:

- **The two late-frame counts time different edges.** Last flight measures from one xrWaitFrame *return* to the next, the M5 report from one *entry* to the next (both over 1.25 slots, same steady seconds; checked by recounting both on all three logs: 34/59/36 by return match Last flight, 55/49/83 by entry match the report). Under Match refresh xrWaitFrame keeps about 10 ms of slack, so a main thread that arrives late often just waits less: 27 late entries were followed by a short interval and never reached the return. Under Very high there is no slack there (the hold is in xrEndFrame), so the two counts are close. Neither is what the headset shows; the headset side is missed refreshes and 406.
- **The headset does better under Very high**: 40% more missed refreshes and about 20× more generated frames still unwritten a refresh later under Match refresh. That is the same 406 level as the 13:37 Match refresh flight on the regular mission (7.5/s), so it repeats across missions.
- **R3's beat is visible directly**: 40 of the 83 late entries are the render thread between xrEndFrame and the next xrBeginFrame (DCS's own limiter under Match refresh, usual 9.5 ms). 34 of them took 18-24 ms, the usual plus one 72 Hz period (13.9 ms): DCS's limiter waited one refresh too many. Under Very high there are 8 such frames with no cluster (20.9-83.9 ms).
- **The rest of the late entries**: main thread 23 (mostly scripts and AI, below), render thread's drawing 13 (several 80-90 ms around 68-69 s and 93 s, 226 s: texture loads and missile effects; 68.97 s logs "IC fail" for the CJS Super Hornet mod's AIM-120 textures), xrEndFrame 7.
- **Scripts (M4's first flight)**: 2,099 timed calls. Three kinds made late frames: `RadioVoice:423` (399 calls, mostly about 1 ms, four of 27-36 ms: 112.7, 187.7 (the breach order again), 212.7 and 226.5 s), `RadioVoice:298` (45 ms at 133.3 s) and `Civilianai:47` (33 ms at 130.4 s, 28 ms at 218.4 s, each with "No property record" errors for a spawned A_320 / B_737). The longest late frame (137.9 s, 116 ms on the main thread) has no timed script or log line near it.
- **Civilian spawns** (`Civilianai:47` creates the aircraft inside the call: its "Corrupt damage model" errors are logged before the call returns): first A_320 at 130.4 s, 33 ms, one frame 43 ms (one refresh late), no headset refresh missed; first B_737 at 218.4 s, 28 ms in the call and then 82 ms more on the main thread between the unit's birth (E15) and engine start (E18) events, one frame 125 ms, **5 refreshes missed at once** (the second-longest late frame of the flight); a second A_320 at 254.4 s, 6 ms, nothing late. So a type's first spawn costs, a repeat does not. Together about 7% of the late frames' time over the slot and 5 of the flight's 75 missed refreshes.

Next flights, to finish S16 under the protocol (B A, then A B): (1) **Match refresh** again, (2) **Very high** again, same mission. If the headset figures hold, make Very high the default for Virtual Desktop with frame generation (R3). Separately: build the perf-test mission without civilian traffic (`Civilianai`) so its spawns stop showing in the A/B, and the traced flight for R18/R19.

## Flight 12 October 2026 15:36 (90 Hz, 3×, Very high)

Same perf-test mission, build and profile as 14:16 (Frame limit **Very high**), except the headset at **90 Hz** and frame generation **3×**: DCS paced at 30 FPS (33.3 ms slot), interval p50 33.3 ms throughout steady flight. 177 steady seconds (66-250 s). **Not part of the S16 A/B**: three things changed at once (refresh, factor, DCS rate). It answers whether 90 Hz 3× holds up against 72 Hz 2× on this PC.

Late-frame counts are not comparable across the two modes as they stand (one refresh is 11.1 ms at 90 Hz, 13.9 ms at 72 Hz; DCS makes fewer frames a minute at 30 FPS), so the table adds time-based and share figures. Corrected missed refreshes leave out Virtual Desktop's once-a-second display-time corrections (R20), as Last flight now counts them (M6).

| Figure (steady seconds) | 14:16 (72 Hz 2×, Very high) | 15:19 (72 Hz 2×, Match refresh) | 15:36 (90 Hz 3×, Very high) |
| --- | --- | --- | --- |
| Late frames / min, M5 report (over 1.25 slots) | 16.9 | 25.8 | **10.9** |
| Late frames' time over the slot, per minute | 284 ms | 482 ms | **206 ms** |
| Missed refreshes / min, Last flight | 16.6 | 23.3 | 19.6 (and 12 doubled) |
| ... without Virtual Desktop's corrections (R20, M6) | 13.8 | 19.3 | 13.8 (2 doubled) |
| ... as a share of the headset's refreshes | 0.32% | 0.45% | **0.26%** |
| ... as time per minute | 191 ms | 268 ms | **153 ms** |
| Generated frame still unwritten a refresh later (406) | 0.3/s | 5.7/s | **0.16/s** |
| Headset queue after each generated frame, p50 / p90 | 0.5 / 1.2 ms | 1.3 / 16.7 ms | 0.6 / 1.2 ms |
| DCS GPU ms p50 / p90 | 19.0 / 21.1 | 18.5 / 20.4 | 19.3 / 21.1 |
| Frame generation GPU per pair | 2.10 ms | 1.72 ms | 2.29 ms |
| CPU ms per frame p50 / p90 / p99 | 5.2 / 6.8 / 15.1 | 5.7 / 8.1 / 17.0 | 5.3 / 6.8 / **11.9** |
| GPU power p50 | 311 W | 311 W | **279 W** |

What the logs show:

- **90 Hz 3× is the smoothest of the three on every headset figure**: fewest late frames and least time lost to them, fewest missed refreshes as a share and as time, and the lowest 406 rate yet. The trade is not in these figures: a real frame every 33.3 ms instead of 27.8 ms (more input-to-photon latency, two generated frames per real one, so more chance of visible generation artefacts). That part is the pilot's call.
- **GPU headroom**: DCS's GPU p90 21.1 ms in a 33.3 ms slot, 279 W against 311 W. Room to raise DCS's settings at 90 Hz 3×. 90 Hz 2× (45 FPS, 22.2 ms slot) would not fit: GPU p90 21.1 ms plus about 1.7 ms of generation per pair.
- **Where the late frames went** (M5, 32 late frames, 609 ms over the slot): OFXR's pair hold (xrEndFrame) largest in 18, DCS's drawing in 8, the render thread between frames in 3, the main thread in 3 (all three are script calls: `RadioVoice:423` 43 ms at 171.9 s on the breach order, 33 ms at 196.9 s; `RadioVoice:298` 38 ms at 117.6 s). The hold running long is R19 again. The two `Civilianai:47` spawns cost 13-14 ms this time and made no late frame.
- **Last flight's 26 late frames/min is mostly its threshold**: in 3× it counts an interval as one refresh late from 3.5 refreshes (38.9 ms, 1.17 slots); the report counts from 1.25 slots (41.6 ms). 39 of the 81 late returns fall between the two. At 72 Hz 2× both thresholds are 1.25 slots (R21).
- **3× release delay at its floor**: every 1-second window (801/802) had the delay at 0 µs, with the first generated frame written a median 2-4 ms after its hand-over and 20-30 of each window's 30 counted late. 406 stayed low, so Virtual Desktop's deadline is later than the hand-over and the copy waits on the GPU in time. Nothing to change yet; it means the delay cannot steer further in this mode.
- **Rendered FPS on Last flight reads 34.2 against a target of 30**: it averages the whole flight, including 253-270 s after the session left FOCUSED (DCS at 90 FPS while the flight ended). The steady figures already leave that time out. Cosmetic; folded into R21.

## Flight 12 October 2026 17:36 (Engine Optimizations switched off in flight)

Same build and profile as 15:36 (90 Hz, 3×, Very high). One DCS session, two missions: `FLIR_AB_Test_Night.miz` first (OFXR log 64-490 s, optimizations on), then the perf-test mission reloaded without restarting DCS (517-956 s). Engine Optimizations switched **off with the hotkey at 17:49:00 (760 s)**, 243 s into the perf-test mission; `DcsQvCull.log` confirms everything stopped (partition boost, slabs, texture dedupe, task-queue clock, prefetch fix, recorders). So the perf-test mission ran **on for its first 243 s and off for its last 196 s**. **Not an A/B**: the two sides are different phases of the mission, and the off side covers mission time (243-440 s) that no earlier flight reached.

| Figure (perf-test mission) | On (517-760 s) | Off (760-956 s) |
| --- | --- | --- |
| Steady seconds (Last flight's filter) | 208 of 243 | **111 of 196** |
| Late frames / min, steady seconds only | 13.0 | 15.7 |
| Missed refreshes / min, steady seconds only | 19.9 | 13.5 |
| Late frames / min, **every flying second** (from 540 s, after loading) | 15.0 | **24.6** |
| Late frames' time over the slot, every flying second | 523 ms/min | **1,597 ms/min** |
| Frames over 200 ms | 2 | **16** |
| Missed refreshes / min, every flying second (corrections left out) | 42 | **141** |
| Virtual Desktop's xrEndFrame over 30 ms (presenter thread) | 3 | **47** |
| DCS CPU ms per frame p50 / p90 / p99 (steady) | 6.1 / 7.8 / 13.0 | 5.9 / 7.3 / 11.6 |
| DCS GPU ms p50 / p90 (steady) | 20.5 / 22.2 | 20.0 / 21.6 |
| Video memory, every process (OFXR's vram_status) | 23.8-24.0 of 24.1 GB | 23.8-23.9 of 24.1 GB |

What the logs show:

- **A ~240 ms freeze came back every 2-50 s after the switch**, 16 in 196 s (6 in the first 20 s), against 1 in the 220 s before it (736 s). Each one is the same: DCS's xrEndFrame held 200-260 ms in OFXR's pair hold, its main thread waiting ~200 ms in xrWaitFrame behind it. Inside the hold, OFXR's presenter called Virtual Desktop's xrEndFrame and it took 91.5 ms, then 94.8 ms, then 34.9 ms in a row (usually 0.035 ms; 775.66-775.91 s). Generation and DCS's GPU work were on time. **Every one of the 23 freezes has the same shape**: two Virtual Desktop xrEndFrame calls of 88-102 ms back to back, often followed by one of about 34 ms. A block of fixed length looks like a timeout inside Virtual Desktop (its encoder or its link to the headset), not like GPU work, which would vary. This is R19 at a much larger size: Virtual Desktop's runtime blocks, and OFXR's hold passes the block on to DCS.
- **Last flight hid it**: a second with such a freeze has 22-24 frames of 30, below the steady filter's 90%, so the second and the 2 s either side are dropped. 86 of the off side's 196 s were dropped, and its steady figures look *better* than the on side's (missed 13.5 against 19.9/min). Over every flying second the off side is clearly worse. R22.
- **Last flight's Engine Optimizations card is wrong for this flight**: it shows DCS CPU 3.2 ms on against 5.9 ms off, but the on side is mostly the FLIR night mission (CPU p50 2.9 ms). On the same mission the switch changed DCS's CPU time per frame by 0.2 ms (6.1 on, 5.9 off) and GPU by 0.5 ms. At 30 FPS (33.3 ms slot, GPU p90 22 ms) neither side is near the limit, so the optimizations show no gain in frame time here. R23.
- **The same freeze happened with optimizations on**, 6 times in the FLIR mission (106-456 s, 237-283 ms each, the same two ~93 ms Virtual Desktop xrEndFrame calls) and once on the perf-test mission. So the switch did not create it. Something in the off state made it about 18 times more frequent (0.27/min on, 4.9/min off on the same mission), or the mission's later phase did. This one flight cannot tell which.
- **Video memory was full for the whole perf-test mission** (every process 23.8-24.0 GB of the 4090's 24.1 GB; DCS's own resident 15.4-16.0 GB). The FLIR mission ran at 16.5 GB; earlier flights peaked at 22.4-23.0 GB. Loading a second mission without restarting DCS left about 1-1.5 GB more in use. Texture dedupe is one of the things the switch turns off, which needs more memory when there is none to spare. Whether full memory is what makes Virtual Desktop block is not shown: the FLIR mission's six freezes came at 16.5 GB.
- **Scripts**: as before, `RadioVoice:423` (25-43 ms), `RadioVoice:298` (45 ms) and two `Civilianai:47` spawns (22-23 ms). The longest steady late frame (673.9 s, 122 ms on the main thread) was a B_737 spawn.

Next flight: restart DCS, load only the perf-test mission, and switch Engine Optimizations **off from the start** (or on and off every 60 s: on, off, off, on). If the 240 ms freezes come back early in the mission with them off, and not with them on, the switch is the cause; then switch the parts one at a time, texture dedupe first. Last flight's **Freezes** row and the runtime's long frame calls in Hitch signals (M7) count them; the engine card compares within the mission load only.

## Flight 12 October 2026 18:17 (Engine Optimizations off twice, early in the mission)

Same build and profile as 17:36 (90 Hz, 3×, Very high), with M7 installed. Again one DCS session and two missions, not restarted between them, so the conditions match 17:36: `FLIR_AB_Test_Night.miz` (18:19:05-18:24:28, on), then the perf-test mission (18:24:41-18:48:21, about 23.7 minutes, the longest run of it yet). Engine Optimizations switched **off twice**: 18:25:49-18:26:11 (22 s, mission time 68-90 s) and 18:27:52-18:28:20 (28 s, 191-219 s); `DcsQvCull.log` confirms both (boost, slabs, texture dedupe, task-queue clock off). On for the rest.

**OFXR's log covers only the last 6.4 minutes.** It reached its 256 MB limit at 1434 s (18:41:52) and started again; the records from 9.8 s to 1434 s are gone (`seq` jumps from 2,281 to 2,821,490). Both off windows are in the lost part. Last flight did not notice: it shows the flight as complete (`partial: false`) and the engine card empty (all 382 seconds left out, the switches being outside the log). R24.

**Freezes over the whole session**, from two sources that do not depend on OFXR's log: DCS's own `ModelTimeQuantizer: ANTIFREEZE ENABLED` line in dcs.log, and the engine module's frame rate every 2 s (`stats … calls/s`, one collect per frame). Both were checked on the one freeze OFXR did log (1753.9 s = 18:47:12, 268 ms): dcs.log has an ANTIFREEZE line at 18:47:12.281 and the 2 s rate drops to 25.5. The 113 ms late frame at 1533 s left neither, so they mark the ~240 ms kind only.

| Part of the session | Time | Freezes (ANTIFREEZE, and the 2 s rate under 27) |
| --- | --- | --- |
| FLIR mission, after its first 30 s (on) | 4.9 min | **0** (17:36: 6 in about 7 min) |
| Perf-test mission, off windows | 50 s | **0** (lowest 2 s rate 29.0) |
| Perf-test mission, on, after its first 30 s | 22.3 min | 6: mission time 303, 340, 734, 736, 936, 1351 s (0.27/min; 17:36's on side 0.27/min) |
| ... of them in 243-440 s, the mission time of 17:36's off side | 3.3 min | 2 (17:36, off: 16) |

What this shows:

- **Switching the optimizations off did not bring the freezes back this time.** At 17:36, 6 freezes came in the first 20 s after the switch; here two off windows of 22 s and 28 s, with video memory just as full (23.9-24.4 of 24.1 GB through the whole mission, both windows included), had none. At 17:36's off rate (4.9/min) about four were due in 50 s.
- **The mission phase alone does not reproduce 17:36 either**: the same mission time (243-440 s) with the optimizations on had 2 freezes, against 16 off at 17:36. Over the whole mission the on rate matches 17:36's on rate exactly (0.27/min).
- So neither the switch on its own (short windows, early in the mission) nor the later phase on its own explains the 17:36 burst. Left open: something that builds up over minutes off (the off side at 17:36 was 196 s in one go), or something at 17:36 this run did not have. The switch is a weaker suspect than it was; it is not cleared for a long off stretch.
- **The one freeze in the log has the same shape again** (1753.9 s, on): Virtual Desktop's xrEndFrame 94.2 ms, 94.3 ms, then 35.8 ms in a row on OFXR's presenter, DCS's xrEndFrame held ~260 ms in the pair hold (R19). So the shape is not tied to the switch: it happens with the optimizations on, at about one every 4 minutes on this mission.

The logged 6.4 minutes (perf-test mission time about 1045-1420 s, on; no earlier flight reached past 440 s), against 15:36 (same mode):

| Figure (steady seconds) | 15:36 (mission 66-250 s) | 18:17 (mission about 1045-1420 s) |
| --- | --- | --- |
| Steady seconds | 177 | 370 |
| Late frames / min, M5 report (over 1.25 slots) | 10.9 | **10.7** (65 late frames, 1,387 ms over the slot) |
| Late frames / min, Last flight (M6) | — | 13.0 (45 one refresh late, 18 two, 17 three or more) |
| Missed refreshes / min, corrections left out | 13.8 | **22.5** (139; about 24 of them in the one freeze), 86 display-time corrections counted apart |
| Generated frame still unwritten a refresh later (406) | 0.16/s | 0.18/s |
| Freezes (100 ms or more), longest | — | 3, 267 ms (see below) |
| Virtual Desktop's xrEndFrame over 30 ms | — | 3 (all in the one freeze) |
| CPU ms per frame p50 / p90 / p99 | 5.3 / 6.8 / 11.9 | **7.0 / 8.5** / 12.3 |
| DCS GPU ms p50 / p90 | 19.3 / 21.1 | 21.2 / 23.3 (10 ms of headroom in the 33.3 ms slot) |
| GPU power p50 | 279 W | 292 W |

- **Late frames held at the same rate in a heavier phase** (CPU per frame up 1.7 ms at p50, GPU up 2 ms). Where they went (M5): OFXR's pair hold largest in 31 (mostly xrEndFrame 33-48 ms, the hold a refresh long), DCS's drawing in 24 (the longest: 104, 84, 76, 72 ms at 1489-1640 s, with missile effects in dcs.log, `OParticleSystem: preset 'missile' not found`), the main thread in 6 (`RadioVoice:423` 52 ms at 1625.9 s, the longest script call of all flights; 3,066 calls of it in this run), between frames 4.
- **Last flight's Freezes row (3)** holds the 268 ms freeze, and at least one of the 113-228 ms frames after the mission ended at 1821 s: DCS's unloading and menu still count as steady flight (the caveat noted under R22). Only the 268 ms one was in flight.
- **Scripts over the whole run** (M4, 11,134 calls): `RadioVoice:423` 52, 43, 43, 39, 39 ms; `RadioVoice:298` 47 ms; `Civilianai:47` 41 ms. Same three as every flight.

Next flight: to settle the switch, Engine Optimizations off for one **long** stretch (5 minutes or more, through mission time 243-440 s), the rest on; restart DCS and load only the perf-test mission. Keep the run under about 20 minutes, or OFXR's log wraps again (R24); dcs.log's ANTIFREEZE lines count freezes either way.

## A/B protocol (from R1)

1. **Measure with M2**: Late frames/min and Missed refreshes/min are the primary figures; one per change as listed under Changes.
2. **Fix the scene**: replay one DCS track, or one short mission on autopilot over the same terrain, same time of day, weather and view. Restart DCS each run; one discarded warm-up run.
3. **Runs**: at least 5 minutes of steady flight each; order A B B A, two runs per side; switch the change back after its pair. An all-on control run at the start and end of a session catches drift.
4. **Decide**: Confirmed only if the median shift between sides is larger than the difference between the two runs of the same side. Set the OFXR log before launching (switching it in flight rebuilds frame generation, R15).
5. **Save**: each flight's summary is kept in `%LOCALAPPDATA%\DcsControl\flights\flight-*.json` with its profile, for side-by-side comparison.

## A/B order (one change per flight)

**Superseded by the protocol above** (R1). The list below stays as the order of what to test; start with the frame limit (R3) and the headset queue priority (S11).

1. New build as installed (all of the above on). Compare with the baseline.
2. Framegen → In-headset diagnostics → **OFXR log** off, to confirm S3 removed its cost (Last flight then has no per-frame figures).
3. Frame limit: **Very high (300 FPS)** instead of Match refresh, so OFXR alone paces DCS (two pacers at 36 FPS can beat).
4. Terrain prefetch fix: Observe instead of Skip repeats (S5).
5. Spare image for in-place reading off (S4), reading Hitch signals.
6. Keep headset images once made off (S2).
7. CPU Boost off (S7).
8. Record CPU and GPU use off (S8).
9. Framegen → Advanced → **Separate submission queue** (`own_binding_queue`) off: not measured on Virtual Desktop yet.

## Deferred: found, not changed yet

| Item | Why not yet |
| --- | --- |
| Generated frames handed over before drawn (402, nearly every frame) | Needs a flight log with the new build to see whether Virtual Desktop then shows an older image; changing the hold has its own cost. |
| 3× deep-pipeline depth (TripleDepth) only grows | Only in 3× with the deep pipeline (this profile runs 2×); giving a slot back drops a displayed frame, a hitch of its own. |
| Adaptive 2×/3× switch drains the queue on DCS's thread | Same: needs a 3× adaptive flight log first. |
| Engine module: 28 recorder threads at normal priority, recorder waits, the shadow instancing lock across CreateVertexShader, texture-table rebuild bursts, the 512 KB `partw` scan, dcs.log re-read every 2 s | Not seen in a flight yet; for the full review. |
| OFXR's transient refusals that block DCS in `wait_for_presenter_submission`, unbounded hand-over swapchain waits, two pacing clocks on Virtual Desktop | For the full review, with logs from the new build. |

## System: outside DCS Control (12 October 2026)

What this PC had when read on 12 October (read only; nothing changed). Each is a candidate for one A/B pair under the protocol above; the pilot makes the change, and the result goes in Status.

| ID | Item | Found | Candidate change | Check | Status |
| --- | --- | --- | --- | --- | --- |
| X1 | Memory Integrity (HVCI) and virtualization-based security | On | Off (Windows Security → Device security → Core isolation; reboot). A security trade-off: the pilot's decision | Late frames/min; Limited by (CPU ms per frame) | Not tried |
| X2 | Programs running beside DCS | Chrome ×35, Discord ×6, Edge ×6, Google Drive, Razer Synapse ×6, NordVPN service | Close before flight, or add to CPU Boost → Free VRAM before flight; VPN disconnected or excluding the local network | PC during flight (other CPU/GPU users); Late frames/min | Not tried |
| X3 | Defender real-time scanning of DCS's streamed files | On; exclusions unknown (need admin to read) | Exclude the DCS install, Saved Games\DCS.openbeta and %LOCALAPPDATA%\DcsControl | Late frames/min in low flight over terrain; PC during flight (MsMpEng) | Not tried |
| X4 | NVIDIA driver profile for DCS (read 12 Oct through Profile Inspector's NVAPI wrapper, read only; driver 617.42) | Power: prefer maximum performance; shader cache 10 GB (global unlimited); VSync forced off and frame limiter off (only the mirror window; OFXR paces the headset); max pre-rendered frames: application | Nothing to change | — | Already set |
| X11 | NVIDIA DLSS override in DCS's profile | Super-resolution override on: preset forced to **M** (transformer gen 2; DCS asks for K), quality level and scaling ratio not forced (DCS's DLAA stands) | One flight with the preset on "Use recommended" (K at DLAA): M is tuned for Performance mode | DCS GPU ms p50/p90 on Last flight; the pilot's eye for sharpness | Not tried |
| X12 | Resizable BAR forced on for DCS (rBAR - Enable v2 = Enabled) | Enabled in DCS's profile (DCS is not on NVIDIA's list) | Off for one flight | Late frames/min; Video memory peak | Not tried |
| X5 | DCS visibility range | Extreme | High | Late frames/min; Limited by (CPU ms) | Not tried |
| X6 | DCS shadows / clutter distance | 4 / 1000 | One step lower each (separate pairs) | Late frames/min; Limited by | Not tried |
| X7 | DCS preload radius | 150000 (maximum) | Keep: with 64 GB it prevents terrain-loading hitches | — | Kept |
| X8 | Hardware-accelerated GPU scheduling | Off (dxdiag 12 Oct: supported, Enabled:False); not needed by OFXR's frame generation (not DLSS-G) | On (optional; reboot) | Late frames/min; Missed refreshes/min | Not tried |
| X9 | Network to the headset | PC wired at 1 Gbps; Virtual Desktop H.264+, automatic bitrate off | 5/6 GHz Wi-Fi with the headset alone on it if possible | Virtual Desktop overlay (network latency) screenshots; OVR Metrics Tool CSV on the Quest | Not checked |
| X13 | NordVPN beside Virtual Desktop | VPN disconnected, but `nordsec-threatprotection-service`, `nordvpn-service` and the updater running; Virtual Desktop's NordVPN warning dismissed (`DontWarnApps` in `C:\ProgramData\Virtual Desktop\StreamerSettings.json`) | Quit NordVPN and stop its services for one flight | Freezes (dcs.log ANTIFREEZE); Virtual Desktop's xrEndFrame over 80 ms | Not tried |
| X14 | Windows network profile of the wired link | Ethernet ("MeshIOT 2") is **Public**; Virtual Desktop's network-profile warning dismissed | Private (Settings → Network → Ethernet) | as X13 | Not tried |
| X15 | Virtual Desktop codec and bitrate | H.264+ (codec 5), automatic bitrate off; DCS asked for 2920×3100 per eye; the Streamer's busiest GPU engine 34% p90 at 72 Hz, 42% at 90 Hz (encode, not saturated) | AV1 10-bit (or HEVC 10-bit) for one flight; a lower fixed bitrate for another | as X13, and Virtual Desktop's overlay (network, decode latency) | Not tried |
| X10 | Driver latency (DPC/ISR) | Not measured | LatencyMon for 10 minutes with DCS running | LatencyMon report | Not checked |

## Review findings (12 October 2026)

A fresh read-only review of OFXR (frame path; GPU and resources), the engine module, DCS Control and the performance metrics, after the changes above. Ranked by expected effect on what is seen in the headset; **Open** until acted on.

| ID | Finding | Evidence | Proposed action | Status |
| --- | --- | --- | --- | --- |
| R1 | Last flight cannot judge an A/B: stutter (> 2 × median) and on-time (1.5 × slot) thresholds fall on the 72 Hz steps and count about half the late frames (90 reported, 188 real in the baseline); missed headset slots are not counted (204 events, most within 150 ms of a late DCS frame); the flying window includes pauses and loading edges; Hitch signals over the whole span; only the latest flight is kept | baseline log, `LastFlight.cs` | Late frames/min by periods late; missed and doubled headset slots/min (from `internal_end_frame` display times); app_end_frame p99/p99.9; stricter flying window; save each flight's summary and compare two | Done (M2); compare view still to do |
| R2 | The headset's queue (binding queue, NORMAL priority) gets past each generated frame's hand-over 14.8 ms p50 later (p90 16.6), more than a 72 Hz period for 58% of them, although synthesis finished ~19 ms earlier | baseline log 913/916 → 914 probes; `openxr_layer.cpp:6084` | Binding queue at HIGH priority; probes to split the lag; Last flight row for it | Switch added (S11); to fly |
| R3 | DCS's 36 FPS cap (Match refresh) is a second pacer at OFXR's exact period: DCS arrives at a drifting offset, the generated frame gets anywhere from a full period to none, and the presenter can start late and put two frames in one headset slot | two reviews; 11 doubled slots, 24 display-time corrections in the baseline | A/B Very high (300) or a cap at 37-38 FPS; then change the default for Virtual Desktop with frame generation | Seen in the 15:19 flight (Match refresh): 34 late frames where DCS's render thread waited one extra 72 Hz period before xrBeginFrame; none under Very high (14:16). S16: a second run per side to fly |
| R4 | Apply does not redeploy when only DCS Control's own components changed (a new OFXR DLL, a new ini key); the deployed OFXR is still the 11 Oct build until a full Apply | `ControlService.cs:391`; deployed DLL hash ≠ built | Compare the installed files with what this build would write | Fixed (S13) |
| R5 | Engine module render-thread waits add up beyond the known 3.6 ms: shadow-plan cap per pass (S6) plus duplicate build, G-buffer redo waits; DropStale spins are not counted; a late job adds helpers for ~6.7 s (feedback) | `shadow_batch.h:620`, `gb_rec.h:3734`, `shadow_rec.h:1227` | One per-frame wait budget (~0.5 ms); DropStale never spins; plan wait 0.3-0.5 ms without the duplicate build; one counter for all render-thread stalls | Budget and counting done (S14); duplicate plan build and arming kernel calls still open |
| R6 | 28 recorder threads at NORMAL priority on 16 logical CPUs, worker spins up to 20 ms, about 100 kernel calls and 28 needless wakes per frame on the render thread | `shadow_rec.h:4939`, `gb_rec.h:5362` | Helpers 1/1, below normal without priority boost, short spins then WaitOnAddress; A/B helpers 3/2 vs 1/1 | Switch added (S15); worker spin waits still open |
| R7 | Staging textures can be overwritten by synthesis before the hand-over copy on the headset's queue has read them (no GPU-side order; reuse decided on the CPU) | `openxr_layer.cpp:2320`, `d3d12_frame_synthesizer.cpp:3784` | Per-slot copy fence the synthesis queue waits on (or reports busy) | Fixed (S10) |
| R8 | 2× deep pipeline gets 4 synthetic staging textures (0018 meant only 3×): ~145 MB unused; the S4 spare likely unneeded (2 × 36 MB) | `openxr_layer.cpp:3776` | 4 only when 3× is possible; spare off by default after a flight shows no waits | Not changed: a 2× deep session can switch to 3× live (triple_switchable) and nothing is created at the switch, so the four textures are needed from the start; allocating them at the switch would save ~145 MB of 24 GB at real complexity. Spare image (S4): decide after a flight |
| R9 | OFXR's presenter holds its content lock across Virtual Desktop's image wait and xrEndFrame; DCS's xrEndFrame needs it every frame (same pattern as S1) | `openxr_layer.cpp:10399-11663`, `16418` | Keep runtime calls outside the lock with single rings; log lock waits over 0.5 ms | Open |
| R10 | Presenter thread at default priority; runtime-entry gate held across Virtual Desktop's xrWaitFrame although DCS's bridged session does not need it | `openxr_layer.cpp:12104`, `2133` | THREAD_PRIORITY_HIGHEST + MMCSS for the presenter; skip the gate with the D3D11 bridge | Open |
| R11 | Generated frames handed over before drawn (402): no torn image (the copy waits on the GPU on the headset's queue), but an older image when generation finishes after Virtual Desktop's deadline; not logged in 2× | `openxr_layer.cpp:10597`, `10525` | Log "still unwritten a slot later" per second; then a bounded wait or the layer's repeat frame | Logged (406, patch 0025): 7.5/s in the 13:37 flight, about half the generated frames in its last minute; hold or repeat to do |
| R12 | Display-time correction steps DCS's predicted time by a whole period (13.9 ms pose jolt) | `latency_control.cpp:293` | Ramp over several frames | Open |
| R13 | `before_write` queues a D3D11 GPU wait every frame even when the read finished long ago | `d3d11_bridge.cpp:983` | Skip when the fence has completed | Fixed (S12) |
| R14 | Both eyes' synthesis run strictly one after the other (~3.0 ms per pair); PERFORMANCE.md's reason for not overlapping them (shared bridge queue) is wrong | baseline `synthesis_gpu_span` | Submit both packs and flows before both compositions | Open |
| R15 | Switching the OFXR log in flight drains the presenter and rebuilds the synthesizer (a hitch, and a different synthesizer): A/B step 2 tests more than logging | `openxr_layer.cpp:14296` | Set the log before launch; test S3 with `flush_each_event` instead | Open |
| R16 | Smaller: CPU Boost helper crash leaves affinity/priority changed; OFXR logs never pruned (170 MB); helper and app polling during flight; Engine page polls when hidden; 1 kHz ABOVE_NORMAL timer thread; toggles freeze all DCS threads; partw scan; shader lock across CreateVertexShader | app and engine reviews | Batch of small fixes | Open |
| R17 | The engine module's own A/B bench measures frame interval, which the cap pins at 27.8 ms; p95 of ~144 frames per block; fixed order; blocks alias with 10 s jobs | `bench.h:50` | Measure render-thread busy time; ABBA; longer blocks | Open |
| R18 | DCS's render thread spent 83.9 ms between xrEndFrame and xrBeginFrame (outside the frame calls; GPU idle), holding its main thread 83.2 ms in xrWaitFrame | 14:16 flight, 183.55-183.63 s; M5 | A WPR trace (M5): what the render thread ran, or what woke it; then OFXR's swapchain calls timed if it is one of them | Open |
| R19 | OFXR's pair hold in xrEndFrame runs a refresh or more long when Virtual Desktop's xrEndFrame / xrWaitFrame hold up the presenter (it misses a refresh): the largest part of the late frames in both flights | 14:16: 25 of 27 xrEndFrame over 30 ms; M5 split; 17:36: two Virtual Desktop xrEndFrame calls of ~93 ms back to back (a fixed length: a timeout?), DCS frozen ~240 ms each time (16 after Engine Optimizations off, 7 with them on); Virtual Desktop xrEndFrame calls of 80-108 ms in every build since 11 Oct, at 72 and 90 Hz: 16 (11 Oct 22:10), 5 (22:43), 5 (13:37), 0 (14:16), 0 (15:19), 2 (15:36), 51 (17:36), 3 (18:17), so the rate follows something outside DCS and OFXR (network, X13-X15) | A WPR trace with the presenter and Virtual Desktop's Streamer, with Virtual Desktop's own OpenXR trace profile (`C:\Program Files\Virtual Desktop Streamer\OpenXR\VirtualDesktopOpenXR.wprp`, `Capture-ETL.bat`) to see what its xrEndFrame waits on; R10 (presenter priority, the gate across Virtual Desktop's xrWaitFrame); fly with video memory below full | Open |
| R20 | Virtual Desktop moves its predicted display time by about 4 ms (up to 7) once a second, at the same point of every second within a flight, with OFXR's presenter and DCS on time. A step that crosses half a refresh is counted by Last flight as a missed or doubled refresh: rarely at 72 Hz (1 of 48 at 14:16, 14 of 75 at 15:19), more at 90 Hz (7 missed and 11 of 12 doubled at 15:36). Not OFXR's 3× release window (801), which drifts against it | 14:16, 15:19, 15:36 logs: `internal_wait_frame` display times off the refresh grid by 0.15 period or more, no DCS late frame within 3 frames | Last flight: count off-grid steps apart ("display-time corrections") instead of as missed/doubled; whether the headset shows anything at them needs Virtual Desktop's own overlay | Fixed (M6) |
| R21 | Last flight's late-frame steps in 3× start at 3.5 refreshes (1.17 slots), the report's at 1.25 slots, so the two disagree in 3× only (26 against 10.9/min at 15:36); Rendered FPS averages time outside FOCUSED | `LastFlight.cs:530`, 15:36 flight | One threshold for both (1.25 slots, or a whole refresh over the slot); Rendered FPS over steady seconds | Fixed (M6) |
| R22 | The steady filter drops any second under 90% of the paced rate and the 2 s either side, so a single 240 ms freeze (22-24 frames in that second) removes 5 s from every figure. The worst stalls are never counted, and a side with more of them can look better | 17:36: off side 111 steady seconds of 196, late 15.7 against 24.6/min over every flying second, missed 13.5 against 141/min | Keep short dips between steady seconds; report freezes over 100 ms as a row of their own | Fixed (M7): dips of up to 3 s with 10 frames a second or more stay in; DCS's menu after a mission (full rate) still counts as steady |
| R23 | Last flight's Engine Optimizations card compares all on-time against all off-time in one DCS session, across missions | 17:36: on 3.2 ms CPU (mostly the FLIR night mission) against off 5.9 ms; same mission 6.1 against 5.9 | Compare within one mission load only (or the minutes either side of each switch); say which mission each side covers | Fixed (M7): within the mission loads with a switch (loads split by 5 s or more without flight); mission names not shown |
| R24 | Last flight does not see that OFXR's log wrapped: the fork's log writer (0021) writes the kept session records again at the top of a wrapped log, so it still starts with `seq=1`, which is Last flight's test. At about 188 KB/s (3× at 90 Hz; 178 KB/s at 72 Hz 2×) the app's `max_file_mb=256` holds about 23 minutes | 18:17: log restarted at 1434 s, last 6.4 of 30 minutes read, shown as complete, engine card empty | Detect a wrap by the jump in `seq` after the kept records and show "last N min"; raise `max_file_mb` (with old logs pruned, R16); count dcs.log's `ANTIFREEZE ENABLED` lines as a freeze count for the whole flight, independent of OFXR's log | Open |
