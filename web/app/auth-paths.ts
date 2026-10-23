/** Accepts only same-origin paths ("/recipes?x=1"), so a crafted returnTo cannot send the user elsewhere. */
export function safeReturnTo(value: string | null | undefined): string {
  const fallback = "/";
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.includes("\\")) return fallback;
  try {
    const base = "http://placeholder.invalid";
    const url = new URL(value, base);
    if (url.origin !== base) return fallback;
    if (url.pathname === "/login") return fallback;
    return url.pathname + url.search + url.hash;
  } catch {
    return fallback;
  }
}

export function loginPathFor(location: { pathname: string; search: string; hash: string }): string {
  const target = location.pathname + location.search + location.hash;
  return target === "/" ? "/login" : `/login?returnTo=${encodeURIComponent(target)}`;
}
