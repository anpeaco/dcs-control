# Stutter fixes: change log and validation

Every change made to remove a cause of delay or stutter, with what to look at after a flight to see whether it worked, and how to switch it back if performance gets worse. One change per row; the **Status** column is updated after each test flight. Keep the order of the A/B flights below: one change per flight, same mission, same route, Virtual Desktop's performance overlay on.

All of these are in release 0.5.6 preview as built on 12 October 2026 (OFXR layer with fork patches 0001-0022). Status values: **Not flown** (built and tested offline only), **Confirmed** (a flight showed the expected effect), **No effect** (flown, nothing measurable), **Regressed** (flown, worse: switched back, see the entry).

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
| S1 | Overlay no longer holds its lock across the runtime's xrEndFrame | OFXR fork patch 0019 (8e10a49) | none (locking only) | Not flown |
| S2 | Headset swapchains kept once made | OFXR fork patch 0020 (8e10a49) | Framegen → Advanced → **Keep headset images once made** | Not flown |
| S3 | OFXR flight log written by a background thread | OFXR fork patch 0021 (8e10a49) | none; `flush_each_event=1` makes it synchronous again | Not flown |
| S4 | Spare image for in-place reading, and a count of the waits | OFXR fork patch 0022 (8e10a49) | Framegen → Advanced → **Spare image for in-place reading** | Not flown |
| S5 | Terrain prefetch fix yields instead of sleeping | engine module, `prefetch_fix.h` (b72e738) | Engine Optimizations → Terrain prefetch fix → Observe (compares with no skipping) | Not flown |
| S6 | Shadow-plan wait on DCS's render thread capped at 2 ms (was 20 ms) | engine module, `shadow_batch.h` (b72e738) | none | Not flown |
| S7 | CPU Boost leaves affinity alone on a single-CCD CPU | `CpuTopology.cs` (8e10a49) | turn CPU Boost off to compare | Not flown |
| S8 | Flight helper asks for every process's GPU time every 30 s (was 5 s) | `FlightMonitor.cs` (8e10a49) | CPU Boost → **Record CPU and GPU use** off to compare | Not flown |
| S9 | Late frame skip off in the profile | profile `vd-fg-boost-opt` (draft, not in git) | Framegen → Advanced → **Late frame skip** | Not flown |
| M1 | Last flight shows **Hitch signals** | `LastFlight.cs`, overview card (8e10a49) | measurement only | Not flown |

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

## A/B order (one change per flight)

**Superseded by R1 (review, 12 October):** one flight per side cannot confirm a change smaller than about 40% (late frames per minute varied 10-54 between minutes of the baseline), and Last flight's stutter thresholds undercount late frames. Fix the measurement first, then fly a replayed track, A B B A, two runs per side. The list below stays as the order of what to test.

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

## Review findings (12 October 2026)

A fresh read-only review of OFXR (frame path; GPU and resources), the engine module, DCS Control and the performance metrics, after the changes above. Ranked by expected effect on what is seen in the headset; **Open** until acted on.

| ID | Finding | Evidence | Proposed action | Status |
| --- | --- | --- | --- | --- |
| R1 | Last flight cannot judge an A/B: stutter (> 2 × median) and on-time (1.5 × slot) thresholds fall on the 72 Hz steps and count about half the late frames (90 reported, 188 real in the baseline); missed headset slots are not counted (204 events, most within 150 ms of a late DCS frame); the flying window includes pauses and loading edges; Hitch signals over the whole span; only the latest flight is kept | baseline log, `LastFlight.cs` | Late frames/min by periods late; missed and doubled headset slots/min (from `internal_end_frame` display times); app_end_frame p99/p99.9; stricter flying window; save each flight's summary and compare two | Open |
| R2 | The headset's queue (binding queue, NORMAL priority) gets past each generated frame's hand-over 14.8 ms p50 later (p90 16.6), more than a 72 Hz period for 58% of them, although synthesis finished ~19 ms earlier | baseline log 913/916 → 914 probes; `openxr_layer.cpp:6084` | Binding queue at HIGH priority; probes to split the lag; Last flight row for it | Open |
| R3 | DCS's 36 FPS cap (Match refresh) is a second pacer at OFXR's exact period: DCS arrives at a drifting offset, the generated frame gets anywhere from a full period to none, and the presenter can start late and put two frames in one headset slot | two reviews; 11 doubled slots, 24 display-time corrections in the baseline | A/B Very high (300) or a cap at 37-38 FPS; then change the default for Virtual Desktop with frame generation | Open |
| R4 | Apply does not redeploy when only DCS Control's own components changed (a new OFXR DLL, a new ini key); the deployed OFXR is still the 11 Oct build until a full Apply | `ControlService.cs:391`; deployed DLL hash ≠ built | Compare the installed files with what this build would write | Open |
| R5 | Engine module render-thread waits add up beyond the known 3.6 ms: shadow-plan cap per pass (S6) plus duplicate build, G-buffer redo waits; DropStale spins are not counted; a late job adds helpers for ~6.7 s (feedback) | `shadow_batch.h:620`, `gb_rec.h:3734`, `shadow_rec.h:1227` | One per-frame wait budget (~0.5 ms); DropStale never spins; plan wait 0.3-0.5 ms without the duplicate build; one counter for all render-thread stalls | Open |
| R6 | 28 recorder threads at NORMAL priority on 16 logical CPUs, worker spins up to 20 ms, about 100 kernel calls and 28 needless wakes per frame on the render thread | `shadow_rec.h:4939`, `gb_rec.h:5362` | Helpers 1/1, below normal without priority boost, short spins then WaitOnAddress; A/B helpers 3/2 vs 1/1 | Open |
| R7 | Staging textures can be overwritten by synthesis before the hand-over copy on the headset's queue has read them (no GPU-side order; reuse decided on the CPU) | `openxr_layer.cpp:2320`, `d3d12_frame_synthesizer.cpp:3784` | Per-slot copy fence the synthesis queue waits on (or reports busy) | Open |
| R8 | 2× deep pipeline gets 4 synthetic staging textures (0018 meant only 3×): ~145 MB unused; the S4 spare likely unneeded (2 × 36 MB) | `openxr_layer.cpp:3776` | 4 only when 3× is possible; spare off by default after a flight shows no waits | Open |
| R9 | OFXR's presenter holds its content lock across Virtual Desktop's image wait and xrEndFrame; DCS's xrEndFrame needs it every frame (same pattern as S1) | `openxr_layer.cpp:10399-11663`, `16418` | Keep runtime calls outside the lock with single rings; log lock waits over 0.5 ms | Open |
| R10 | Presenter thread at default priority; runtime-entry gate held across Virtual Desktop's xrWaitFrame although DCS's bridged session does not need it | `openxr_layer.cpp:12104`, `2133` | THREAD_PRIORITY_HIGHEST + MMCSS for the presenter; skip the gate with the D3D11 bridge | Open |
| R11 | Generated frames handed over before drawn (402): no torn image (the copy waits on the GPU on the headset's queue), but an older image when generation finishes after Virtual Desktop's deadline; not logged in 2× | `openxr_layer.cpp:10597`, `10525` | Log "still unwritten a slot later" per second; then a bounded wait or the layer's repeat frame | Open |
| R12 | Display-time correction steps DCS's predicted time by a whole period (13.9 ms pose jolt) | `latency_control.cpp:293` | Ramp over several frames | Open |
| R13 | `before_write` queues a D3D11 GPU wait every frame even when the read finished long ago | `d3d11_bridge.cpp:983` | Skip when the fence has completed | Open |
| R14 | Both eyes' synthesis run strictly one after the other (~3.0 ms per pair); PERFORMANCE.md's reason for not overlapping them (shared bridge queue) is wrong | baseline `synthesis_gpu_span` | Submit both packs and flows before both compositions | Open |
| R15 | Switching the OFXR log in flight drains the presenter and rebuilds the synthesizer (a hitch, and a different synthesizer): A/B step 2 tests more than logging | `openxr_layer.cpp:14296` | Set the log before launch; test S3 with `flush_each_event` instead | Open |
| R16 | Smaller: CPU Boost helper crash leaves affinity/priority changed; OFXR logs never pruned (170 MB); helper and app polling during flight; Engine page polls when hidden; 1 kHz ABOVE_NORMAL timer thread; toggles freeze all DCS threads; partw scan; shader lock across CreateVertexShader | app and engine reviews | Batch of small fixes | Open |
| R17 | The engine module's own A/B bench measures frame interval, which the cap pins at 27.8 ms; p95 of ~144 frames per block; fixed order; blocks alias with 10 s jobs | `bench.h:50` | Measure render-thread busy time; ABBA; longer blocks | Open |
