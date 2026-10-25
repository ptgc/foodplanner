// The service worker (sw/sw.js) keeps recently viewed recipes and their pictures in caches named with this prefix.
// That is per-user data, so it is deleted on log out and on login: a shared device must not show the previous user's
// recipes. The precache (the app itself, "fp-precache-*") holds no user data and stays.
export const RUNTIME_CACHE_PREFIX = "fp-runtime-";

/** Deletes the runtime caches. Never throws (no Cache API, private mode, ...). */
export async function clearRuntimeCaches(): Promise<void> {
  try {
    if (typeof caches === "undefined") return;
    const names = await caches.keys();
    await Promise.all(names.filter((name) => name.startsWith(RUNTIME_CACHE_PREFIX)).map((name) => caches.delete(name)));
  } catch {
    // Nothing more can be done; the next log out tries again.
  }
}
