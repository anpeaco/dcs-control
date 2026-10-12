// Render-thread waits across the module ([Model] RenderWaitBudgetUs, default
// 1000; 0 = each wait keeps only its own cap).
//
// Several features wait on DCS's render thread for work their own threads do
// for this frame: the shadow and G-buffer recorders for their jobs at the
// pass (TakeJob) and for a stale job's workers (DropStale), the G-buffer
// recorder for a re-recorded segment (RedoReady), shadow batching for a plan
// still being built (PlanForPass). Each had a cap of its own (200 us to 2 ms)
// and none knew about the others, so one frame could wait more than 13 ms in
// all (12 Oct review, R5). With a budget, every such wait is limited to what
// is left of the frame's budget as well: once it is spent, the waits of the
// rest of the frame give up at once and their passes are drawn as stock,
// which every one of them already does on its own timeout.
//
// A frame is the time between two collects (HookCollect: DCS's
// collectRenderables, once per frame); the render thread notices a new one
// by its generation, so nothing here depends on which thread calls Collect.
//
// Statistics, every 10 s in DcsQvCull.log: the render thread's wait per frame
// (p50, p99, max), frames that spent the whole budget, and the time per site.
//
// Included once from main.cpp inside its anonymous namespace.
#pragma once

namespace rtwait {

enum Site : int { kShadowTake = 0, kShadowDrop, kGbTake, kGbDrop, kGbRedo, kShadowPlan, kSites };
inline const char* SiteName(int s) {
  static const char* const kNames[kSites] = {"shadow job", "shadow stale", "gbuffer job", "gbuffer stale", "gbuffer redo",
                                             "shadow plan"};
  return s >= 0 && s < kSites ? kNames[s] : "?";
}

std::atomic<uint32_t> g_budgetUs{1000};
std::atomic<uint64_t> g_frameGen{0};  // HookCollect

// Render thread only.
uint64_t g_seenGen = 0;
double g_spentUs = 0;
bool g_exhausted = false;
bool g_waited = false;  // this frame has a wait to record
double g_limitUs = -1;  // the last Allow's limit when the budget cut it below the wait's own cap, else -1

// Per-frame totals: 50 us buckets up to 5 ms, then one for longer.
constexpr int kBucketUs = 50;
constexpr int kBuckets = 101;
std::atomic<uint64_t> g_hist[kBuckets];
std::atomic<uint64_t> g_frames{0}, g_exhaustedFrames{0}, g_maxUs{0};
std::atomic<uint64_t> g_siteUs[kSites], g_siteWaits[kSites];

inline void CloseFrame() {
  if (!g_waited) return;
  const uint64_t us = static_cast<uint64_t>(g_spentUs);
  g_hist[(std::min)(us / kBucketUs, static_cast<uint64_t>(kBuckets - 1))].fetch_add(1, std::memory_order_relaxed);
  g_frames.fetch_add(1, std::memory_order_relaxed);
  if (g_exhausted) g_exhaustedFrames.fetch_add(1, std::memory_order_relaxed);
  uint64_t cur = g_maxUs.load(std::memory_order_relaxed);
  while (us > cur && !g_maxUs.compare_exchange_weak(cur, us)) {
  }
}

// Render thread: starts the frame's accounting when a collect came since.
inline void Sync() {
  const uint64_t gen = g_frameGen.load(std::memory_order_acquire);
  if (gen == g_seenGen) return;
  CloseFrame();
  g_seenGen = gen;
  g_spentUs = 0;
  g_exhausted = false;
  g_waited = false;
}

// Render thread: the longest a wait with its own cap `capUs` may last now.
inline double Allow(double capUs) {
  Sync();
  const uint32_t budget = g_budgetUs.load(std::memory_order_relaxed);
  if (!budget) return capUs;
  const double left = static_cast<double>(budget) - g_spentUs;
  g_limitUs = -1;
  if (left <= 0) {
    g_exhausted = true;
    return 0;
  }
  if (left < capUs && capUs - left > 0.5) {
    g_limitUs = left;  // the budget, not the cap, decides this wait: spent if it runs to the end (Charge)
    return left;
  }
  return capUs;
}

// Render thread: a wait that lasted `us`.
inline void Charge(Site site, double us) {
  Sync();
  if (us < 0) us = 0;
  if (g_limitUs >= 0 && us >= g_limitUs) g_exhausted = true;
  g_limitUs = -1;
  g_spentUs += us;
  g_waited = true;
  g_siteUs[site].fetch_add(static_cast<uint64_t>(us), std::memory_order_relaxed);
  g_siteWaits[site].fetch_add(1, std::memory_order_relaxed);
}

// Spins (with _mm_pause) while busy() holds, at most Allow(capUs); charges
// the time to `site` and returns whether busy() still holds.
template <typename Busy>
inline bool SpinWhile(Site site, double capUs, Busy busy) {
  if (!busy()) return false;
  const double allowed = Allow(capUs);
  const int64_t t0 = defrec::Qpc();
  const double k = defrec::QpcToUs();
  while (busy() && (defrec::Qpc() - t0) * k < allowed) _mm_pause();
  Charge(site, (defrec::Qpc() - t0) * k);
  return busy();
}

inline uint64_t Quantile(const uint64_t (&hist)[kBuckets], uint64_t total, double q) {
  if (!total) return 0;
  const uint64_t want = static_cast<uint64_t>(q * static_cast<double>(total) + 0.999999);
  uint64_t seen = 0;
  for (int b = 0; b < kBuckets; ++b) {
    seen += hist[b];
    if (seen >= want) return static_cast<uint64_t>(b + 1) * kBucketUs;  // upper edge of the bucket
  }
  return static_cast<uint64_t>(kBuckets) * kBucketUs;
}

void LogCounters(const char* label) {
  uint64_t hist[kBuckets];
  for (int b = 0; b < kBuckets; ++b) hist[b] = g_hist[b].load(std::memory_order_relaxed);
  const uint64_t frames = g_frames.load();
  if (!frames) return;
  char sites[512] = {};
  int at = 0;
  for (int s = 0; s < kSites && at < static_cast<int>(sizeof(sites)) - 64; ++s) {
    const uint64_t n = g_siteWaits[s].load();
    if (!n) continue;
    at += std::snprintf(sites + at, sizeof(sites) - at, "%s%s %.3f ms/frame (%llu waits)", at ? ", " : "", SiteName(s),
                        g_siteUs[s].load() / 1000.0 / static_cast<double>(frames), static_cast<unsigned long long>(n));
  }
  Log("render-thread waits %s: %llu frames with a wait; per frame p50 <= %.2f ms, p99 <= %.2f ms, max %.2f ms; "
      "%llu frames spent the %.2f ms budget; %s",
      label, static_cast<unsigned long long>(frames), Quantile(hist, frames, .5) / 1000.0,
      Quantile(hist, frames, .99) / 1000.0, g_maxUs.load() / 1000.0,
      static_cast<unsigned long long>(g_exhaustedFrames.load()), g_budgetUs.load() / 1000.0, sites);
}

void ResetCounters() {
  for (auto& b : g_hist) b = 0;
  g_frames = g_exhaustedFrames = g_maxUs = 0;
  for (auto& s : g_siteUs) s = 0;
  for (auto& s : g_siteWaits) s = 0;
}

}  // namespace rtwait
