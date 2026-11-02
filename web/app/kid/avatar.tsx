import { avatarEmoji, avatarTint } from "./avatars";
import type { Profile } from "./kid-api";

/**
 * A kid profile's avatar on a tinted disc. Decorative unless `label` is set (then it is an image with that name), because
 * the profile's name is normally next to it.
 */
export function ProfileAvatar({
  profile,
  size = "md",
  label,
}: {
  profile: Pick<Profile, "id" | "avatar">;
  size?: "xs" | "sm" | "md" | "lg";
  label?: string;
}) {
  return (
    <span
      className={`kid-avatar kid-avatar-${size}`}
      style={{ backgroundColor: avatarTint(profile.id) }}
      {...(label ? { role: "img", "aria-label": label } : { "aria-hidden": true })}
      data-testid="profile-avatar"
      data-avatar={profile.avatar}
    >
      {avatarEmoji(profile.avatar)}
    </span>
  );
}
