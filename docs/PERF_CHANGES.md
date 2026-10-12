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
| S16 | Frame limit **Very high (300 FPS)** instead of Match refresh, so OFXR alone paces DCS (R3); for the 406 bursts and slow hand-overs of the 13:37 flight, which did not follow GPU load | profile `vd-fg-boost-opt` (draft, not in git) | Frame limit → **Match refresh** | One run per side on the perf-test mission (14:16 Very high, 15:19 Match refresh): Very high better at the headset (missed refreshes 16.6 against 23.3/min, 406 0.3 against 5.7/s, no limiter beat); Last flight's late-frame count favours Match refresh (see the 15:19 section for why). Leaning Very high; a second run per side still to fly. Draft is on Match refresh |
| M4 | Perf-test mission times its scripts: every scheduled function and event handler call is bracketed by `PERFT>n name` / `PERFT<n` lines in dcs.log (the mission environment has no clock) | `scripts/make-perf-mission.py`, test `scripts/test-perf-mission-timing.py` | `--no-timing` rebuilds the mission without it (byte for byte the previous one) | Flown 12 Oct 15:19: 2,099 calls timed; RadioVoice and Civilianai calls of 27-45 ms behind several late frames |
| M5 | Late-frame report across OFXR's log, dcs.log (with M4's timings), the flight monitor and an optional WPR trace: each late frame split along DCS's main and render threads | `scripts/analyze-flight-spikes.py`, `tools/FlightTrace` (TraceProcessor), `scripts/record-flight-trace.ps1` | measurement only | Report checked on the 13:37, 14:16 and 15:19 logs; it times late frames from xrWaitFrame entries, Last flight from returns (the counts differ under Match refresh); trace analysis tested only up to loading a trace (needs a recorded CPU trace) |

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
| X4 | NVIDIA power management for DCS | Unknown (control panel) | Prefer maximum performance; shader cache 10 GB or unlimited | Limited by (GPU ms p90); PC during flight (GPU clock) | Not tried |
| X5 | DCS visibility range | Extreme | High | Late frames/min; Limited by (CPU ms) | Not tried |
| X6 | DCS shadows / clutter distance | 4 / 1000 | One step lower each (separate pairs) | Late frames/min; Limited by | Not tried |
| X7 | DCS preload radius | 150000 (maximum) | Keep: with 64 GB it prevents terrain-loading hitches | — | Kept |
| X8 | Hardware-accelerated GPU scheduling | Off | On (optional; reboot) | Late frames/min; Missed refreshes/min | Not tried |
| X9 | Network to the headset | PC wired at 1 Gbps; Virtual Desktop H.264+, automatic bitrate off | 5/6 GHz Wi-Fi with the headset alone on it if possible | Virtual Desktop overlay (network latency) screenshots; OVR Metrics Tool CSV on the Quest | Not checked |
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
| R19 | OFXR's pair hold in xrEndFrame runs a refresh or more long when Virtual Desktop's xrEndFrame / xrWaitFrame hold up the presenter (it misses a refresh): the largest part of the late frames in both flights | 14:16: 25 of 27 xrEndFrame over 30 ms; M5 split | A WPR trace with the presenter and Virtual Desktop's Streamer; R10 (presenter priority, the gate across Virtual Desktop's xrWaitFrame) | Open |
