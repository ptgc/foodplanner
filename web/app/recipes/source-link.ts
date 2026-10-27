/** Longest source URL the API accepts (Recipe.MaxSourceUrlLength). */
export const sourceUrlMax = 2000;

/**
 * The URL when it is an absolute http or https URL, otherwise null. Anything else (javascript:, data:, a relative path)
 * must never become a link, even if it were somehow stored.
 */
export function safeSourceUrl(value: string | null | undefined): string | null {
  if (!value || value.length > sourceUrlMax || /\p{Cc}/u.test(value)) return null;
  try {
    const url = new URL(value);
    return (url.protocol === "http:" || url.protocol === "https:") && url.hostname !== "" ? value : null;
  } catch {
    return null;
  }
}

/** What to call the source: the stored name, else the host without "www.". */
export function sourceLabel(url: string, name: string | null | undefined): string {
  if (name && name.trim() !== "") return name;
  return new URL(url).hostname.replace(/^www\./i, "");
}
