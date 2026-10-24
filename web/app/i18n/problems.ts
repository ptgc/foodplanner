import type { MessageKey } from "./index";

/**
 * ProblemDetails `type` codes (urn:foodplanner:problem:*, RFC 9457) -> translated message keys. The server's
 * `title`/`detail` are English and never shown; add a code here when a screen starts to rely on it.
 */
const problemKeys: Record<string, MessageKey> = {
  "urn:foodplanner:problem:login-failed": "problem.loginFailed",
  "urn:foodplanner:problem:rate-limited": "problem.rateLimited",
  "urn:foodplanner:problem:unauthenticated": "problem.unauthenticated",
  "urn:foodplanner:problem:forbidden": "problem.forbidden",
  "urn:foodplanner:problem:forbidden-origin": "problem.forbiddenOrigin",
  "urn:foodplanner:problem:antiforgery-invalid": "problem.antiforgeryInvalid",
  "urn:foodplanner:problem:already-member": "problem.alreadyMember",
  "urn:foodplanner:problem:invite-invalid": "problem.inviteInvalid",
  "urn:foodplanner:problem:confirmation-invalid": "problem.confirmationInvalid",
};

/** The `type` of a ProblemDetails body, if the value looks like one. */
export function problemTypeOf(body: unknown): string | undefined {
  if (typeof body !== "object" || body === null) return undefined;
  const type = (body as { type?: unknown }).type;
  return typeof type === "string" ? type : undefined;
}

/** Message key for a problem type; unknown or missing types give the generic message. */
export function problemKey(type: string | undefined): MessageKey {
  return (type !== undefined ? problemKeys[type] : undefined) ?? "problem.generic";
}
