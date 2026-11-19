import { tileColor } from "../recipes/recipe-image";

/**
 * The fixed avatar set of a kid profile (T-026: the API accepts exactly these keys). An avatar is an emoji, so the app
 * needs no image assets; the profile's name is the accessible name wherever an avatar stands alone.
 */
export const AVATARS = [
  ["fox", "\u{1F98A}"],
  ["bear", "\u{1F43B}"],
  ["cat", "\u{1F431}"],
  ["dog", "\u{1F436}"],
  ["owl", "\u{1F989}"],
  ["frog", "\u{1F438}"],
  ["panda", "\u{1F43C}"],
  ["rabbit", "\u{1F430}"],
  ["lion", "\u{1F981}"],
  ["penguin", "\u{1F427}"],
  ["koala", "\u{1F428}"],
  ["whale", "\u{1F433}"],
] as const;

export type AvatarKey = (typeof AVATARS)[number][0];

const emojiByKey = new Map<string, string>(AVATARS);

/** The emoji of an avatar key; a key this app does not know yet (a newer server) shows a neutral face. */
export function avatarEmoji(key: string): string {
  return emojiByKey.get(key) ?? "\u{1F642}";
}

/** The tile colour behind an avatar: the same profile always gets the same colour. */
export function avatarTint(profileId: string): string {
  return tileColor(profileId);
}
