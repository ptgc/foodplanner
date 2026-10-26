import { api } from "../api/client";

export type ExportPayload = { title: string; text: string };

/** Fetches the server-formatted text export of a week's list (Accept-Language is added by the client middleware). */
export async function fetchExport(householdId: string, weekStart: string): Promise<ExportPayload | "failed"> {
  const { data } = await api.GET("/api/households/{householdId}/shopping-lists/{weekStart}/export", {
    params: { path: { householdId, weekStart }, query: { format: "text" } },
  });
  if (!data || data.text == null) return "failed";
  return { title: data.title, text: data.text };
}

/** Copies with the async clipboard, then a hidden textarea + execCommand; false when neither works. */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // denied or unavailable: try the legacy path
  }
  const area = document.createElement("textarea");
  area.value = text;
  area.setAttribute("readonly", "");
  area.setAttribute("aria-hidden", "true");
  area.style.position = "fixed";
  area.style.top = "0";
  area.style.opacity = "0";
  document.body.appendChild(area);
  try {
    area.select();
    area.setSelectionRange(0, text.length);
    return typeof document.execCommand === "function" && document.execCommand("copy");
  } catch {
    return false;
  } finally {
    area.remove();
  }
}

/**
 * "shared": the share sheet took it. "cancelled": the user dismissed it (no-op).
 * "unavailable": no Web Share (or it cannot share this), or it failed otherwise: the caller copies instead.
 */
export async function shareText(payload: ExportPayload): Promise<"shared" | "cancelled" | "unavailable"> {
  if (typeof navigator.share !== "function") return "unavailable";
  const data = { title: payload.title, text: payload.text };
  if (navigator.canShare && !navigator.canShare(data)) return "unavailable";
  try {
    await navigator.share(data);
    return "shared";
  } catch (e) {
    if (e instanceof DOMException && e.name === "AbortError") return "cancelled";
    return "unavailable";
  }
}
