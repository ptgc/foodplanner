import { useCallback, useState, type ReactNode } from "react";
import { api } from "../api/client";
import type { components } from "../api/schema";
import { call } from "../household/call";
import { ConfirmDialog } from "../household/confirm-dialog";
import { roleKeys } from "../household/roles";
import { useLoaded } from "../household/use-loaded";
import { useI18n, type MessageKey } from "../i18n";
import { categoryKeys, FIELD_ERRORS, shiftWeek, weekStartOf, type Action } from "./shared";
import { Subscription } from "./support-subscription";

export type PanelId = "overview" | "members" | "invites" | "profiles" | "plan" | "subscription" | "shopping" | "recipes" | "log";

type Member = components["schemas"]["SupportMemberItem"];
type Invite = components["schemas"]["SupportInviteItem"];

type Props = { panel: PanelId; householdId: string; householdName: string; write: boolean };

function useFormats() {
  const { locale } = useI18n();
  return {
    date: (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(iso)),
    dateTime: (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso)),
    time: (iso: string) => new Intl.DateTimeFormat(locale, { timeStyle: "short" }).format(new Date(iso)),
  };
}

export function SupportPanels({ panel, householdId, householdName, write }: Props) {
  const { t } = useI18n();
  const [confirming, setConfirming] = useState<Action | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<MessageKey | null>(null);
  const [done, setDone] = useState(false);

  // Every write goes through one confirmation that names the household, the person and the effect.
  const confirm = useCallback((action: Action) => {
    setFailure(null);
    setDone(false);
    setConfirming(action);
  }, []);

  async function run() {
    if (!confirming) return;
    setBusy(true);
    const key = await confirming.run();
    setBusy(false);
    if (key === FIELD_ERRORS) {
      setConfirming(null); // the form beside the field shows what to fix
      return;
    }

    if (key) {
      setFailure(key);
      return;
    }
    setConfirming(null);
    setDone(true);
    confirming.onDone();
  }

  return (
    <div className="household-card" data-testid={`support-panel-${panel}`}>
      {done && <p role="status">{t("support.done")}</p>}
      {panel === "overview" && <Overview householdId={householdId} />}
      {panel === "members" && <Members householdId={householdId} householdName={householdName} write={write} confirm={confirm} />}
      {panel === "invites" && <Invites householdId={householdId} householdName={householdName} write={write} confirm={confirm} />}
      {panel === "profiles" && <Profiles householdId={householdId} />}
      {panel === "plan" && <Plan householdId={householdId} householdName={householdName} write={write} confirm={confirm} />}
      {panel === "subscription" && <Subscription householdId={householdId} householdName={householdName} write={write} confirm={confirm} />}
      {panel === "shopping" && <Shopping householdId={householdId} />}
      {panel === "recipes" && <Recipes householdId={householdId} />}
      {panel === "log" && <AccessLog householdId={householdId} />}
      {confirming && (
        <ConfirmDialog
          title={t("support.confirm.title")}
          body={confirming.body}
          confirmLabel={t("support.confirm.yes")}
          tone="primary"
          busy={busy}
          error={failure}
          onConfirm={() => void run()}
          onCancel={() => setConfirming(null)}
        />
      )}
    </div>
  );
}

function Loaded<T>({
  state,
  children,
}: {
  state: { data: T | null; error: MessageKey | null; reload: () => void };
  children: (data: T) => ReactNode;
}) {
  const { t } = useI18n();
  if (state.error && !state.data) {
    return (
      <div>
        <p role="alert" className="form-error">
          {t("support.loadFailed")} {t(state.error)}
        </p>
        <button type="button" className="button" onClick={state.reload}>
          {t("support.retry")}
        </button>
      </div>
    );
  }

  if (!state.data) return <p role="status">{t("support.loading")}</p>;
  return <>{children(state.data)}</>;
}

function Overview({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const f = useFormats();
  const state = useLoaded<components["schemas"]["SupportHouseholdOverview"]>(`overview:${householdId}`, () =>
    call(() => api.GET("/api/support/households/{targetHouseholdId}", { params: { path: { targetHouseholdId: householdId } } })),
  );
  return (
    <Loaded state={state}>
      {(o) => (
        <>
          <h2>{o.name}</h2>
          <dl className="household-facts">
            <div>
              <dt>{t("support.overview.created")}</dt>
              <dd>{f.date(o.createdAt)}</dd>
            </div>
            <div>
              <dt>{t("support.overview.plan")}</dt>
              <dd data-testid="support-plan">{o.plan ?? t("support.overview.noPlan")}</dd>
            </div>
            <div>
              <dt>{t("support.overview.members")}</dt>
              <dd>{Number(o.memberCount)}</dd>
            </div>
            <div>
              <dt>{t("support.overview.owners")}</dt>
              <dd>{Number(o.ownerCount)}</dd>
            </div>
            <div>
              <dt>{t("support.overview.openInvites")}</dt>
              <dd>{Number(o.openInviteCount)}</dd>
            </div>
            <div>
              <dt>{t("support.overview.profiles")}</dt>
              <dd>{Number(o.profileCount)}</dd>
            </div>
            <div>
              <dt>{t("support.overview.recipes")}</dt>
              <dd>{Number(o.recipeCount)}</dd>
            </div>
            <div>
              <dt>{t("support.overview.mealPlans")}</dt>
              <dd>{Number(o.mealPlanCount)}</dd>
            </div>
          </dl>
        </>
      )}
    </Loaded>
  );
}

function Members({ householdId, householdName, write, confirm }: { householdId: string; householdName: string; write: boolean; confirm: (a: Action) => void }) {
  const { t } = useI18n();
  const f = useFormats();
  const state = useLoaded<Member[]>(`members:${householdId}`, () =>
    call(() => api.GET("/api/support/households/{targetHouseholdId}/members", { params: { path: { targetHouseholdId: householdId } } })),
  );
  const label = (m: Member) => m.email ?? t("support.members.unknownEmail");

  const promote = (m: Member) =>
    confirm({
      body: t("support.confirm.promote", { email: label(m), household: householdName }),
      run: async () => {
        const result = await call(() =>
          api.PUT("/api/support/households/{targetHouseholdId}/members/{userId}/role", {
            params: { path: { targetHouseholdId: householdId, userId: m.userId } },
            body: { role: "Owner" },
          }),
        );
        return result.ok ? null : result.key;
      },
      onDone: state.reload,
    });
  const clearPin = (m: Member) =>
    confirm({
      body: t("support.confirm.clearPin", { email: label(m), household: householdName }),
      run: async () => {
        const result = await call(() =>
          api.POST("/api/support/households/{targetHouseholdId}/members/{userId}/pin-unlock", {
            params: { path: { targetHouseholdId: householdId, userId: m.userId } },
          }),
        );
        return result.ok ? null : result.key;
      },
      onDone: state.reload,
    });

  return (
    <Loaded state={state}>
      {(members) =>
        members.length === 0 ? (
          <p>{t("support.members.empty")}</p>
        ) : (
          <ul className="household-list">
            {members.map((m) => (
              <li key={m.userId} className="household-row" data-testid="support-member-row">
                <div className="household-row-main">
                  <span className="household-row-title">{label(m)}</span>
                  <span className="household-row-sub">{t("support.members.joined", { date: f.date(m.joinedAt) })}</span>
                  <span className="household-row-sub">
                    {m.pinLocked && m.pinLockedUntil ? t("support.members.pinLocked", { time: f.time(m.pinLockedUntil) }) : t("support.members.pinOk")}
                  </span>
                </div>
                <span className="household-row-role">{t(roleKeys[m.role])}</span>
                {write && (
                  <div className="household-row-actions">
                    {m.role !== "Owner" && (
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("support.members.makeOwnerFor", { email: label(m) })}
                        onClick={() => promote(m)}
                      >
                        {t("support.members.makeOwner")}
                      </button>
                    )}
                    {m.pinLocked && (
                      <button
                        type="button"
                        className="button button-outline"
                        aria-label={t("support.members.clearPinFor", { email: label(m) })}
                        onClick={() => clearPin(m)}
                      >
                        {t("support.members.clearPin")}
                      </button>
                    )}
                  </div>
                )}
              </li>
            ))}
          </ul>
        )
      }
    </Loaded>
  );
}

function Invites({ householdId, householdName, write, confirm }: { householdId: string; householdName: string; write: boolean; confirm: (a: Action) => void }) {
  const { t } = useI18n();
  const f = useFormats();
  const state = useLoaded<Invite[]>(`invites:${householdId}`, () =>
    call(() => api.GET("/api/support/households/{targetHouseholdId}/invites", { params: { path: { targetHouseholdId: householdId } } })),
  );
  const cancel = (invite: Invite) =>
    confirm({
      body: t("support.confirm.cancelInvite", { role: t(roleKeys[invite.role]), household: householdName }),
      run: async () => {
        const result = await call(() =>
          api.DELETE("/api/support/households/{targetHouseholdId}/invites/{inviteId}", {
            params: { path: { targetHouseholdId: householdId, inviteId: invite.id } },
          }),
        );
        return result.ok ? null : result.key;
      },
      onDone: state.reload,
    });

  return (
    <Loaded state={state}>
      {(invites) =>
        invites.length === 0 ? (
          <p>{t("support.invites.empty")}</p>
        ) : (
          <ul className="household-list">
            {invites.map((i) => {
              const text = t("support.invites.item", { role: t(roleKeys[i.role]), date: f.date(i.createdAt) });
              return (
                <li key={i.id} className="household-row" data-testid="support-invite-row">
                  <div className="household-row-main">
                    <span className="household-row-title">{text}</span>
                    <span className="household-row-sub">{t(`support.invites.status.${i.status as "Open" | "Accepted" | "Expired"}`)}</span>
                  </div>
                  {write && i.status === "Open" && (
                    <button
                      type="button"
                      className="button button-outline"
                      aria-label={t("support.invites.cancelFor", { role: t(roleKeys[i.role]), date: f.date(i.createdAt) })}
                      onClick={() => cancel(i)}
                    >
                      {t("support.invites.cancel")}
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
        )
      }
    </Loaded>
  );
}

function Profiles({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const state = useLoaded<components["schemas"]["SupportProfileItem"][]>(`profiles:${householdId}`, () =>
    call(() => api.GET("/api/support/households/{targetHouseholdId}/profiles", { params: { path: { targetHouseholdId: householdId } } })),
  );
  return (
    <Loaded state={state}>
      {(profiles) =>
        profiles.length === 0 ? (
          <p>{t("support.profiles.empty")}</p>
        ) : (
          <ul className="household-list">
            {profiles.map((p) => (
              <li key={p.id} className="household-row">
                <span className="household-row-title">{p.displayName}</span>
                <span className="household-row-sub">{p.avatar}</span>
              </li>
            ))}
          </ul>
        )
      }
    </Loaded>
  );
}

function WeekPicker({ week, onChange }: { week: string; onChange: (week: string) => void }) {
  const { t } = useI18n();
  return (
    <div className="household-actions support-week">
      <button type="button" className="button button-outline" onClick={() => onChange(shiftWeek(week, -1))}>
        {t("support.plan.previous")}
      </button>
      <span role="status">
        {t("support.plan.week")} {week}
      </span>
      <button type="button" className="button button-outline" onClick={() => onChange(shiftWeek(week, 1))}>
        {t("support.plan.next")}
      </button>
    </div>
  );
}

function Plan({ householdId, householdName, write, confirm }: { householdId: string; householdName: string; write: boolean; confirm: (a: Action) => void }) {
  const { t } = useI18n();
  const f = useFormats();
  const [week, setWeek] = useState(() => weekStartOf(new Date()));
  const state = useLoaded<components["schemas"]["MealPlanResponse"]>(`plan:${householdId}:${week}`, () =>
    call(() =>
      api.GET("/api/support/households/{targetHouseholdId}/meal-plans/{weekStart}", {
        params: { path: { targetHouseholdId: householdId, weekStart: week } },
      }),
    ),
  );
  const unlock = () =>
    confirm({
      body: t("support.confirm.unlockWeek", { week, household: householdName }),
      run: async () => {
        const result = await call(() =>
          api.DELETE("/api/support/households/{targetHouseholdId}/meal-plans/{weekStart}/lock", {
            params: { path: { targetHouseholdId: householdId, weekStart: week } },
          }),
        );
        return result.ok ? null : result.key;
      },
      onDone: state.reload,
    });

  return (
    <>
      <WeekPicker week={week} onChange={setWeek} />
      <Loaded state={state}>
        {(plan) => (
          <>
            {plan.locked && (
              <p data-testid="support-week-locked">
                {t("support.plan.locked")}
                {write && (
                  <>
                    {" "}
                    <button type="button" className="button button-outline" onClick={unlock}>
                      {t("support.plan.unlock")}
                    </button>
                  </>
                )}
              </p>
            )}
            {plan.entries.length === 0 ? (
              <p>{t("support.plan.empty")}</p>
            ) : (
              <ul className="household-list">
                {plan.entries.map((e) => (
                  <li key={e.id} className="household-row">
                    <span className="household-row-title">
                      {t("support.plan.entry", { date: f.date(e.date), title: e.recipeTitle, servings: Number(e.servings) })}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </Loaded>
    </>
  );
}

function Shopping({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const [week, setWeek] = useState(() => weekStartOf(new Date()));
  const state = useLoaded<components["schemas"]["ShoppingListResponse"]>(`shopping:${householdId}:${week}`, () =>
    call(() =>
      api.GET("/api/support/households/{targetHouseholdId}/shopping-lists/{weekStart}", {
        params: { path: { targetHouseholdId: householdId, weekStart: week } },
      }),
    ),
  );
  return (
    <>
      <WeekPicker week={week} onChange={setWeek} />
      <Loaded state={state}>
        {(list) =>
          list.main.length + list.checkYouHave.length === 0 ? (
            <p>{t("support.shopping.empty")}</p>
          ) : (
            <>
              <h3>{t("support.shopping.main")}</h3>
              <ul className="household-list">
                {list.main.map((i) => (
                  <li key={i.id} className="household-row">
                    <span className="household-row-title">{i.name}</span>
                  </li>
                ))}
              </ul>
              {list.checkYouHave.length > 0 && (
                <>
                  <h3>{t("support.shopping.check")}</h3>
                  <ul className="household-list">
                    {list.checkYouHave.map((i) => (
                      <li key={i.id} className="household-row">
                        <span className="household-row-title">{i.name}</span>
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </>
          )
        }
      </Loaded>
    </>
  );
}

function Recipes({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const [page, setPage] = useState(1);
  const state = useLoaded<components["schemas"]["SupportRecipePage"]>(`recipes:${householdId}:${page}`, () =>
    call(() =>
      api.GET("/api/support/households/{targetHouseholdId}/recipes", { params: { path: { targetHouseholdId: householdId }, query: { page } } }),
    ),
  );
  return (
    <Loaded state={state}>
      {(data) => {
        const pages = Math.max(1, Math.ceil(Number(data.total) / Number(data.pageSize)));
        return data.items.length === 0 ? (
          <p>{t("support.recipes.empty")}</p>
        ) : (
          <>
            <ul className="household-list">
              {data.items.map((r) => (
                <li key={r.id} className="household-row">
                  <span className="household-row-title">{r.title}</span>
                </li>
              ))}
            </ul>
            <div className="household-actions">
              <button type="button" className="button button-outline" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                {t("support.recipes.previous")}
              </button>
              <span role="status">{t("support.recipes.page", { page, pages })}</span>
              <button type="button" className="button button-outline" disabled={page >= pages} onClick={() => setPage(page + 1)}>
                {t("support.recipes.next")}
              </button>
            </div>
          </>
        );
      }}
    </Loaded>
  );
}

function AccessLog({ householdId }: { householdId: string }) {
  const { t } = useI18n();
  const f = useFormats();
  const state = useLoaded<components["schemas"]["SupportAdminAccessPage"]>(`log:${householdId}`, () =>
    call(() => api.GET("/api/support/households/{targetHouseholdId}/access-log", { params: { path: { targetHouseholdId: householdId } } })),
  );
  return (
    <Loaded state={state}>
      {(data) =>
        data.items.length === 0 ? (
          <p>{t("support.log.empty")}</p>
        ) : (
          <ul className="household-list">
            {data.items.map((row, index) => (
              <li key={`${row.at}-${index}`} className="household-row" data-testid="support-log-row">
                <div className="household-row-main">
                  <span className="household-row-title">{t("support.log.row", { action: row.action, outcome: row.outcome })}</span>
                  <span className="household-row-sub">
                    {f.dateTime(row.at)}
                    {row.reasonCategory ? `, ${t("support.log.by", { email: row.adminEmail, category: t(categoryKeys[row.reasonCategory]) })}` : `, ${row.adminEmail}`}
                  </span>
                  {row.reasonNote && <span className="household-row-sub">{t("support.log.note", { note: row.reasonNote })}</span>}
                </div>
              </li>
            ))}
          </ul>
        )
      }
    </Loaded>
  );
}
