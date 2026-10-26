import type { MessageKey } from "../i18n";
import { problemKey, problemTypeOf } from "../i18n/problems";

export type StatusMessages = Partial<Record<number, MessageKey>>;

/**
 * The message for a failed API call. Order: a message chosen for this status by the caller (for example the export
 * rate limit), then the ProblemDetails `type`, then the status alone (a proxy may answer without a body). The server's
 * English `title`/`detail` are never shown.
 */
export function failureKey(status: number, body: unknown, byStatus?: StatusMessages): MessageKey {
  const chosen = byStatus?.[status];
  if (chosen) return chosen;
  const typed = problemKey(problemTypeOf(body));
  if (typed !== "problem.generic") return typed;
  if (status === 401) return "problem.unauthenticated";
  if (status === 403) return "problem.forbidden";
  if (status === 429) return "problem.rateLimited";
  return "problem.generic";
}

export type Outcome<D> = { ok: true; data: D; status: number } | { ok: false; key: MessageKey; status: number };

/** Runs one generated-client call and folds success, ProblemDetails failures and network errors into an Outcome. */
export async function call<R extends { response: Response; data?: unknown; error?: unknown }>(
  run: () => Promise<R>,
  byStatus?: StatusMessages,
): Promise<Outcome<R["data"]>> {
  try {
    const result = await run();
    if (result.response.ok) return { ok: true, data: result.data, status: result.response.status };
    return { ok: false, key: failureKey(result.response.status, result.error, byStatus), status: result.response.status };
  } catch {
    return { ok: false, key: "problem.unreachable", status: 0 };
  }
}
