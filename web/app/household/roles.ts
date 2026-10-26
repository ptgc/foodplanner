import type { components } from "../api/schema";
import type { MessageKey } from "../i18n";

export type Role = components["schemas"]["HouseholdRole"];

export const roleKeys: Record<Role, MessageKey> = {
  Owner: "household.role.Owner",
  Adult: "household.role.Adult",
  Child: "household.role.Child",
};
export const allRoles: Role[] = ["Owner", "Adult", "Child"];
/** The API only invites Adults and Children (an Owner is made by promoting a member). */
export const invitableRoles: Role[] = ["Adult", "Child"];

/** Who may see which controls. The API enforces the same rules (HouseholdOwner / HouseholdAdult policies). */
export function powers(role: Role) {
  return {
    manage: role === "Owner", // members, roles, invitations, delete
    export: role === "Owner" || role === "Adult",
  };
}

/** The link an invitee opens: the app's own /invite/{token} page, which asks them to sign in first if needed. */
export function inviteLink(token: string): string {
  return `${window.location.origin}/invite/${encodeURIComponent(token)}`;
}
