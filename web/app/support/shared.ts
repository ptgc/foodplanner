import type { components } from "../api/schema";
import type { MessageKey } from "../i18n";

export type SupportSession = components["schemas"]["SupportSessionResponse"];
export type Category = components["schemas"]["SupportReasonCategory"];

export const categories: Category[] = ["LostOwner", "StuckInvite", "PinLockout", "LockedWeek", "DataProblem", "Other"];

export const categoryKeys: Record<Category, MessageKey> = {
  LostOwner: "support.category.LostOwner",
  StuckInvite: "support.category.StuckInvite",
  PinLockout: "support.category.PinLockout",
  LockedWeek: "support.category.LockedWeek",
  DataProblem: "support.category.DataProblem",
  Other: "support.category.Other",
};

/** The Monday of the week of a local date, as yyyy-MM-dd (the API's week key). */
export function weekStartOf(date: Date): string {
  const monday = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  monday.setDate(monday.getDate() - ((monday.getDay() + 6) % 7));
  return isoDate(monday);
}

export function isoDate(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${date.getFullYear()}-${month}-${day}`;
}

/** The week key seven days before or after another one. */
export function shiftWeek(weekStart: string, weeks: number): string {
  const [year, month, day] = weekStart.split("-").map(Number);
  return isoDate(new Date(year ?? 1970, (month ?? 1) - 1, (day ?? 1) + 7 * weeks));
}
