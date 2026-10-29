# Sourced by verify.sh and e2e.sh. Sets FP_TEST_PARALLEL to "low" or "full" (T-097).
#   low   little memory: .NET test projects one after another, 2 test classes at a time, Vitest 2 workers,
#         MSBuild 2 nodes, Playwright 1 worker. Slower, but the 16 GB developer machine does not run out of memory.
#   full  tool defaults (what CI uses).
# Default: full when CI is set (CI=true), otherwise low. Override either way: FP_TEST_PARALLEL=full|low.
case "${FP_TEST_PARALLEL:-}" in
  low | full) ;;
  "") if [ -n "${CI:-}" ]; then FP_TEST_PARALLEL=full; else FP_TEST_PARALLEL=low; fi ;;
  *) echo "FP_TEST_PARALLEL must be 'low' or 'full', got '${FP_TEST_PARALLEL}'" >&2; exit 2 ;;
esac
export FP_TEST_PARALLEL
