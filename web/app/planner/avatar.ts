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
