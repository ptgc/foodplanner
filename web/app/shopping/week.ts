/** ISO week helpers. Weeks are identified by their Monday as `yyyy-MM-dd`, the key the API uses. */

const pad = (n: number) => String(n).padStart(2, "0");

export function toIsoDate(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function parse(iso: string): Date {
  const [y = 0, m = 1, d = 1] = iso.split("-").map(Number);
  return new Date(y, m - 1, d);
}

/** The Monday on or before the given date. */
export function mondayOf(d: Date): string {
  const copy = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const sinceMonday = (copy.getDay() + 6) % 7;
  copy.setDate(copy.getDate() - sinceMonday);
  return toIsoDate(copy);
}

export function isMonday(iso: string | null | undefined): iso is string {
  if (!iso || !/^\d{4}-\d{2}-\d{2}$/.test(iso)) return false;
  const d = parse(iso);
  return toIsoDate(d) === iso && d.getDay() === 1;
}

export function addWeeks(iso: string, weeks: number): string {
  const d = parse(iso);
  d.setDate(d.getDate() + weeks * 7);
  return toIsoDate(d);
}

/** ISO 8601 week number and week-year of the week starting on the given Monday. */
export function isoWeek(monday: string): { week: number; year: number } {
  const thursday = parse(monday);
  thursday.setDate(thursday.getDate() + 3);
  const year = thursday.getFullYear();
  const jan1 = new Date(year, 0, 1);
  const dayOfYear = Math.round((thursday.getTime() - jan1.getTime()) / 86_400_000);
  return { week: Math.floor(dayOfYear / 7) + 1, year };
}

export function weekRange(monday: string, format: (date: Date) => string): string {
  const start = parse(monday);
  const end = parse(monday);
  end.setDate(end.getDate() + 6);
  return `${format(start)} – ${format(end)}`;
}
