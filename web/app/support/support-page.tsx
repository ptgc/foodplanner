import { Link } from "react-router";
import { useCallback, useEffect, useId, useState, type FormEvent } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call, type Outcome } from "../household/call";
import { useLoaded } from "../household/use-loaded";
import "../household/household.css";
import { roleKeys } from "../household/roles";
import { useI18n, type MessageKey } from "../i18n";
import { useSession } from "../session";
import { ActiveSession } from "./active-session";
import { categories, categoryKeys, type Category, type SupportSession } from "./shared";
import "./support.css";

type HouseholdPage = components["schemas"]["SupportHouseholdPage"];
type UserItem = components["schemas"]["SupportUserItem"];
type Mode = components["schemas"]["SupportSessionMode"];

type Chosen = { id: string; name: string };

/**
 * The system admin support pages (T-096), at /support. There is no menu link, and the page shows itself only when
 * /api/auth/me says the account is a system admin; anybody else sees a plain "not found". That is only a UI hint: the API
 * refuses every support route to a non-admin on its own, and checks the session, the mode and the household on every call.
 */
export default function SupportPage() {
  const { session } = useSession();
  const { t } = useI18n();
  if (session.status !== "signedIn" || !session.user.isSystemAdmin) {
    return (
      <section className="household" aria-labelledby="support-notfound">
        <h1 id="support-notfound">{t("support.notFound.title")}</h1>
        <p>{t("support.notFound.body")}</p>
      </section>
    );
  }

  return <Support />;
}

type Current = { kind: "none" } | { kind: "active"; session: SupportSession };

function Support() {
  const { t } = useI18n();
  const loaded = useLoaded<Current>("current", async (): Promise<Outcome<Current | undefined>> => {
    const result = await call(() => api.GET("/api/support/sessions/current"));
    if (result.ok) return { ok: true, data: result.data ? { kind: "active", session: result.data } : { kind: "none" }, status: result.status };
    // 404: no active session, which is the normal state before one is started.
    if (result.status === 404) return { ok: true, data: { kind: "none" }, status: 404 };
    return result;
  });
  // A session started or ended on this page replaces what was loaded.
  const [local, setLocal] = useState<Current | null>(null);
  const current = local ?? loaded.data;

  return (
    <section className="household support" aria-labelledby="support-title">
      <h1 id="support-title">{t("support.title")}</h1>
      <Link className="button button-outline" to="/status">
        {t("support.statusLink")}
      </Link>
      {!current && !loaded.error && <p role="status">{t("support.loading")}</p>}
      {!current && loaded.error && (
        <div className="household-card">
          <p role="alert" className="form-error">
            {t("support.loadFailed")} {t(loaded.error)}
          </p>
          <button type="button" className="button" onClick={loaded.reload}>
            {t("support.retry")}
          </button>
        </div>
      )}
      {current?.kind === "none" && <StartPanel onStarted={(session) => setLocal({ kind: "active", session })} />}
      {current?.kind === "active" && (
        <ActiveSession key={current.session.id} session={current.session} onEnded={() => setLocal({ kind: "none" })} />
      )}
    </section>
  );
}

function StartPanel({ onStarted }: { onStarted: (session: SupportSession) => void }) {
  const [chosen, setChosen] = useState<Chosen | null>(null);
  return (
    <>
      <StartForm chosen={chosen} onStarted={onStarted} />
      <HouseholdPicker chosen={chosen} onChoose={setChosen} />
      <UserFinder onChoose={setChosen} />
    </>
  );
}

function StartForm({ chosen, onStarted }: { chosen: Chosen | null; onStarted: (session: SupportSession) => void }) {
  const { t } = useI18n();
  const ids = { mode: useId(), category: useId(), note: useId(), password: useId() };
  const [mode, setMode] = useState<Mode>("Read");
  const [category, setCategory] = useState<Category>("LostOwner");
  const [note, setNote] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!chosen || busy) return;
    setBusy(true);
    setError(null);
    const result = await call(
      () =>
        api.POST("/api/support/sessions", {
          body: { householdId: chosen.id, mode, reasonCategory: category, reasonNote: note, password: mode === "Write" ? password : null },
        }),
      { 400: "support.problem.validation" },
    );
    setBusy(false);
    setPassword("");
    if (result.ok && result.data) onStarted(result.data);
    else if (!result.ok) setError(result.key);
  }

  return (
    <form className="household-card" onSubmit={(e) => void submit(e)} aria-labelledby="support-start-title" aria-busy={busy}>
      <h2 id="support-start-title">{t("support.start.title")}</h2>
      <p data-testid="support-chosen">{chosen ? `${t("support.start.household")}: ${chosen.name}` : t("support.start.none")}</p>
      <fieldset className="support-fieldset">
        <legend>{t("support.start.mode")}</legend>
        {(["Read", "Write"] as const).map((value) => (
          <label key={value} className="support-radio">
            <input type="radio" name={ids.mode} value={value} checked={mode === value} onChange={() => setMode(value)} />
            {t(value === "Read" ? "support.start.read" : "support.start.write")}
          </label>
        ))}
      </fieldset>
      <div className="field">
        <label htmlFor={ids.category}>{t("support.start.category")}</label>
        <select id={ids.category} value={category} onChange={(e) => setCategory(e.target.value as Category)}>
          {categories.map((c) => (
            <option key={c} value={c}>
              {t(categoryKeys[c])}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor={ids.note}>{t("support.start.note")}</label>
        <textarea id={ids.note} rows={3} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
      </div>
      {mode === "Write" && (
        <div className="field">
          <label htmlFor={ids.password}>{t("support.start.password")}</label>
          <input
            id={ids.password}
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <small>{t("support.start.passwordHint")}</small>
        </div>
      )}
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      <button type="submit" className="button" disabled={busy || !chosen || (mode === "Write" && password === "")}>
        {busy ? t("support.start.starting") : t("support.start.button")}
      </button>
    </form>
  );
}

function useDate() {
  const { locale } = useI18n();
  return useCallback((iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(iso)), [locale]);
}

function HouseholdPicker({ chosen, onChoose }: { chosen: Chosen | null; onChoose: (household: Chosen) => void }) {
  const { t } = useI18n();
  const formatDate = useDate();
  const searchId = useId();
  const [draft, setDraft] = useState("");
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<HouseholdPage | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const result = await call(() =>
        api.GET("/api/support/households", { params: { query: { ...(query ? { search: query } : {}), page, pageSize: 20 } } }),
      );
      if (cancelled) return;
      if (result.ok && result.data) {
        setData(result.data);
        setError(null);
      } else if (!result.ok) setError(result.key);
    })();
    return () => {
      cancelled = true;
    };
  }, [query, page]);

  const pages = data ? Math.max(1, Math.ceil(Number(data.total) / Number(data.pageSize))) : 1;
  const search = (event: FormEvent) => {
    event.preventDefault();
    setPage(1);
    setQuery(draft.trim());
  };

  return (
    <section className="household-card" aria-labelledby="support-households-title">
      <h2 id="support-households-title">{t("support.households.title")}</h2>
      <form className="support-search" onSubmit={search}>
        <div className="field">
          <label htmlFor={searchId}>{t("support.households.search")}</label>
          <input id={searchId} type="search" value={draft} onChange={(e) => setDraft(e.target.value)} />
          <small>{t("support.households.hint")}</small>
        </div>
        <button type="submit" className="button button-outline">
          {t("support.households.button")}
        </button>
      </form>
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      {!data && !error && <p role="status">{t("support.loading")}</p>}
      {data && data.items.length === 0 && <p data-testid="support-households-empty">{t("support.households.empty")}</p>}
      {data && data.items.length > 0 && (
        <>
          <p className="household-note">{t("support.households.total", { count: Number(data.total) })}</p>
          <ul className="household-list">
            {data.items.map((h) => (
              <li key={h.id} className="household-row" data-testid="support-household-row">
                <div className="household-row-main">
                  <span className="household-row-title">{h.name}</span>
                  <span className="household-row-sub">{t("support.households.created", { date: formatDate(h.createdAt) })}</span>
                </div>
                <button
                  type="button"
                  className="button button-outline"
                  aria-pressed={chosen?.id === h.id}
                  aria-label={t("support.households.choose", { name: h.name })}
                  onClick={() => onChoose({ id: h.id, name: h.name })}
                >
                  {t("support.households.choose", { name: h.name })}
                </button>
              </li>
            ))}
          </ul>
          <div className="household-actions">
            <button type="button" className="button button-outline" disabled={page <= 1} onClick={() => setPage(page - 1)}>
              {t("support.households.previous")}
            </button>
            <span role="status">{t("support.households.page", { page, pages })}</span>
            <button type="button" className="button button-outline" disabled={page >= pages} onClick={() => setPage(page + 1)}>
              {t("support.households.next")}
            </button>
          </div>
        </>
      )}
    </section>
  );
}

function UserFinder({ onChoose }: { onChoose: (household: Chosen) => void }) {
  const { t } = useI18n();
  const searchId = useId();
  const [draft, setDraft] = useState("");
  const [users, setUsers] = useState<UserItem[] | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);

  async function find(event: FormEvent) {
    event.preventDefault();
    const result = await call(() => api.GET("/api/support/users", { params: { query: { email: draft.trim() } } }), {
      400: "support.users.hint",
    });
    if (result.ok) {
      setUsers(result.data ?? []);
      setError(null);
    } else setError(result.key);
  }

  return (
    <section className="household-card" aria-labelledby="support-users-title">
      <h2 id="support-users-title">{t("support.users.title")}</h2>
      <form className="support-search" onSubmit={(e) => void find(e)}>
        <div className="field">
          <label htmlFor={searchId}>{t("support.users.search")}</label>
          <input id={searchId} type="search" value={draft} onChange={(e) => setDraft(e.target.value)} />
          <small>{t("support.users.hint")}</small>
        </div>
        <button type="submit" className="button button-outline">
          {t("support.users.button")}
        </button>
      </form>
      {error && (
        <p role="alert" className="form-error">
          {t(error)}
        </p>
      )}
      {users && users.length === 0 && <p>{t("support.users.empty")}</p>}
      {users && users.length > 0 && (
        <ul className="household-list">
          {users.map((u) => (
            <li key={u.userId} className="household-row" data-testid="support-user-row">
              <div className="household-row-main">
                <span className="household-row-title">{u.email}</span>
                <span className="household-row-sub">{t(u.emailConfirmed ? "support.users.confirmed" : "support.users.notConfirmed")}</span>
                {u.households.length === 0 && <span className="household-row-sub">{t("support.users.noHousehold")}</span>}
              </div>
              <div className="household-row-actions">
                {u.households.map((h) => (
                  <button
                    key={h.id}
                    type="button"
                    className="button button-outline"
                    onClick={() => onChoose({ id: h.id, name: h.name })}
                  >
                    {t("support.users.choose", { name: h.name, role: t(roleKeys[h.role]) })}
                  </button>
                ))}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
