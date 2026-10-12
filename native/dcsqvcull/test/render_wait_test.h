// Offline tests for render_wait.h (12 Oct review R5): the render thread's
// waits share one budget per frame; a new collect starts a new one; the
// statistics count frames with a wait.
#pragma once

namespace rwtest {

void Run() {
  rtwait::ResetCounters();
  const uint32_t savedBudget = rtwait::g_budgetUs.load();
  rtwait::g_budgetUs = 1000;
  rtwait::g_frameGen.fetch_add(1);  // a frame
  Check(rtwait::Allow(200.0) == 200.0, "render wait: a wait keeps its own cap while the budget has room");
  rtwait::Charge(rtwait::kShadowTake, 900.0);
  const double left = rtwait::Allow(200.0);
  Check(left > 99.0 && left < 101.0, "render wait: a wait gets only what is left of the frame's budget");
  rtwait::Charge(rtwait::kGbTake, 150.0);
  Check(rtwait::Allow(200.0) == 0.0, "render wait: a spent budget allows no wait");
  // SpinWhile: never spins past the budget, and reports whether the condition still holds.
  int calls = 0;
  const bool still = rtwait::SpinWhile(rtwait::kGbDrop, 200.0, [&] { ++calls; return true; });
  Check(still && calls < 100, "render wait: a spin with a spent budget gives up at once");
  rtwait::g_frameGen.fetch_add(1);  // the next frame
  Check(rtwait::Allow(200.0) == 200.0, "render wait: a new collect starts a new budget");
  const int64_t t0 = defrec::Qpc();
  const bool busy = rtwait::SpinWhile(rtwait::kShadowDrop, 300.0, [] { return true; });
  const double us = (defrec::Qpc() - t0) * defrec::QpcToUs();
  Check(busy && us >= 290.0 && us < 5000.0, "render wait: a spin lasts its cap within the budget");
  Check(!rtwait::SpinWhile(rtwait::kShadowDrop, 300.0, [] { return false; }), "render wait: nothing to wait for, no wait");
  rtwait::g_frameGen.fetch_add(1);
  rtwait::Allow(0);  // closes the previous frame into the statistics
  Check(rtwait::g_frames.load() == 2 && rtwait::g_exhaustedFrames.load() == 1 && rtwait::g_maxUs.load() >= 1050,
        "render wait: frames with a wait, frames that spent the budget and the longest are counted");
  // A cap above what is left (the shadow plan's 2 ms): the frame spent the budget only if the wait ran to the
  // budget's limit. Counting every such wait put each shadow-plan wait in the count (12 Oct flight).
  rtwait::ResetCounters();
  rtwait::g_frameGen.fetch_add(1);
  rtwait::Allow(2000.0);
  rtwait::Charge(rtwait::kShadowPlan, 50.0);
  rtwait::g_frameGen.fetch_add(1);
  rtwait::Charge(rtwait::kShadowPlan, rtwait::Allow(2000.0));
  rtwait::g_frameGen.fetch_add(1);
  rtwait::Allow(0);
  Check(rtwait::g_frames.load() == 2 && rtwait::g_exhaustedFrames.load() == 1,
        "render wait: a wait the budget cut short counts as spent only when it ran to the budget's limit");
  // Budget 0: every wait keeps its own cap.
  rtwait::g_budgetUs = 0;
  rtwait::Charge(rtwait::kGbRedo, 50000.0);
  Check(rtwait::Allow(2000.0) == 2000.0, "render wait: budget 0 leaves each wait its own cap");
  rtwait::g_budgetUs = savedBudget;
  rtwait::g_frameGen.fetch_add(1);
  rtwait::Allow(0);
  rtwait::ResetCounters();
}

}  // namespace rwtest
