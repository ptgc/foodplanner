export type RecipeFilters = {
  /** Case-insensitive "contains" on the title (T-023). */
  search: string;
  /** Selected tag and category ids; a recipe must have ALL of them (AND, T-023). */
  tagIds: string[];
  /** Prep plus cook at most this many minutes, or null for any. */
  maxMinutes: number | null;
};

export const emptyFilters: RecipeFilters = { search: "", tagIds: [], maxMinutes: null };

/** Quick time limits offered as chips. */
export const maxMinuteOptions = [20, 30, 60] as const;

/** The API accepts at most 10 tag ids; the search length is capped to keep the URL sane. */
export const maxTagFilters = 10;
const maxSearchLength = 100;
const maxMinutesLimit = 2880;

/** URL query -> filters. Anything malformed is ignored, so a hand-edited URL never breaks the page. */
export function parseFilters(params: URLSearchParams): RecipeFilters {
  const search = (params.get("q") ?? "").slice(0, maxSearchLength);
  const tagIds = [...new Set(params.getAll("tag").filter((id) => id !== ""))].slice(0, maxTagFilters);
  const rawMax = params.get("max");
  const max = rawMax !== null && /^\d+$/.test(rawMax) ? Number(rawMax) : null;
  const maxMinutes = max !== null && max > 0 && max <= maxMinutesLimit ? max : null;
  return { search, tagIds, maxMinutes };
}

/** Filters -> URL query (`?q=..&tag=..&tag=..&max=30`); empty filters give an empty query. */
export function toSearchParams(filters: RecipeFilters): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.search !== "") params.set("q", filters.search);
  for (const id of filters.tagIds) params.append("tag", id);
  if (filters.maxMinutes !== null) params.set("max", String(filters.maxMinutes));
  return params;
}

export function toggleTag(filters: RecipeFilters, tagId: string): RecipeFilters {
  if (filters.tagIds.includes(tagId)) return { ...filters, tagIds: filters.tagIds.filter((id) => id !== tagId) };
  if (filters.tagIds.length >= maxTagFilters) return filters;
  return { ...filters, tagIds: [...filters.tagIds, tagId] };
}

export function toggleMaxMinutes(filters: RecipeFilters, minutes: number): RecipeFilters {
  return { ...filters, maxMinutes: filters.maxMinutes === minutes ? null : minutes };
}

export function hasFilters(filters: RecipeFilters): boolean {
  return filters.search.trim() !== "" || filters.tagIds.length > 0 || filters.maxMinutes !== null;
}

/** Query for GET .../recipes. Empty filters are left out; `search` is trimmed. */
export function toListQuery(filters: RecipeFilters, limit: number, offset: number) {
  const search = filters.search.trim();
  return {
    ...(search !== "" ? { search } : {}),
    ...(filters.tagIds.length > 0 ? { tagIds: filters.tagIds } : {}),
    ...(filters.maxMinutes !== null ? { maxMinutes: filters.maxMinutes } : {}),
    limit,
    offset,
  };
}
