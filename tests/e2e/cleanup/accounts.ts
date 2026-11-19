import { request, type APIRequestContext } from "@playwright/test";

/**
 * Deleting an e2e test ACCOUNT through the API (T-095). Only accounts the e2e run made: the address must look like
 * `e2e-<something>@example.test` and must not be the seeded admin. Anything else is refused before any request is made,
 * so a typo can never delete a real user.
 *
 * It signs in as the account (the API only deletes your own account, after the password is entered again), reads the
 * preview, confirms the households that go with the account, and deletes it. It does nothing about accounts nobody can
 * sign in to: `account-setup.spec.ts` registers addresses that never get a password (T-072), and the API removes those by
 * itself after Auth:ConfirmationTokenLifetimeHours (UnconfirmedAccountCleanup).
 */
export const testAccountPattern = /^e2e-[A-Za-z0-9._-]+@example\.test$/i;

export function assertDeletableTestAccount(email: string, adminEmail: string | undefined): void {
  if (!testAccountPattern.test(email)) throw new Error(`Refusing to delete ${email}: not an e2e test account (e2e-...@example.test).`);
  if (adminEmail && email.toLowerCase() === adminEmail.toLowerCase()) throw new Error("Refusing to delete the seeded admin account.");
}

async function csrf(api: APIRequestContext): Promise<Record<string, string>> {
  const res = await api.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

type Preview = { canDelete: boolean; householdsToDelete: { id: string; name: string }[]; blockingHouseholds: { id: string; name: string }[] };

/** Returns the names of the households deleted with the account. */
export async function deleteTestAccount(baseURL: string, email: string, password: string, adminEmail = process.env.E2E_ADMIN_EMAIL): Promise<string[]> {
  assertDeletableTestAccount(email, adminEmail);
  const api = await request.newContext({ baseURL });
  try {
    const login = await api.post("/api/auth/login", { headers: await csrf(api), data: { email, password } });
    if (!login.ok()) throw new Error(`Login of ${email} answered ${login.status()}`);
    const preview = (await (await api.get("/api/account/deletion-preview")).json()) as Preview;
    if (!preview.canDelete) throw new Error(`${email} is the only Owner of: ${preview.blockingHouseholds.map((h) => h.name).join(", ")}`);
    const res = await api.post("/api/account/delete", {
      headers: await csrf(api),
      data: { password, confirmation: email, householdIds: preview.householdsToDelete.map((h) => h.id) },
    });
    if (res.status() !== 204) throw new Error(`Deleting ${email} answered ${res.status()}: ${await res.text()}`);
    return preview.householdsToDelete.map((h) => h.name);
  } finally {
    await api.dispose();
  }
}
