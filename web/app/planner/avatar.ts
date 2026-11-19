import { tileColor } from "../recipes/recipe-image";

/** Up to two initials from an email's name part ("anne.berit@x.no" gives "AB"); "?" when nothing usable. */
export function initialsOf(email: string | null | undefined, userId: string): string {
  const source = (email ?? "").split("@")[0] ?? "";
  const parts = source.split(/[^\p{L}\p{N}]+/u).filter((p) => p !== "");
  const letters = parts.length > 1 ? parts.slice(0, 2).map((p) => [...p][0]) : [...(parts[0] ?? "")].slice(0, 2);
  const text = letters.join("").toLocaleUpperCase();
  return text !== "" ? text : userId !== "" ? [...userId][0]!.toLocaleUpperCase() : "?";
}

/** Same person, same colour everywhere; keyed by user id so it survives an email change. */
export function avatarColor(userId: string): string {
  return tileColor(userId);
}

/** Who a pick belongs to, as the API sends it (`PickedByResponse`). Both fields are null once the account was deleted (T-095). */
export type PickedByUser = { userId: string | null; email: string | null };

/** True for the person behind a pick whose account was deleted. */
export function isDeletedUser(user: PickedByUser): boolean {
  return user.userId === null;
}

/** The name shown for a pick: the email, "Deleted user" for a deleted account, "someone" when only the email is missing. */
export function pickerName(user: PickedByUser, t: (key: "common.deletedUser" | "planner.unknownPicker") => string): string {
  if (isDeletedUser(user)) return t("common.deletedUser");
  return user.email ?? t("planner.unknownPicker");
}

/** The text in the avatar: initials of the email; a deleted user has no initials, a neutral dash. */
export function pickerInitials(user: PickedByUser): string {
  return isDeletedUser(user) ? "-" : initialsOf(user.email, user.userId ?? "");
}
