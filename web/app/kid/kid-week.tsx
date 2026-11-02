import { useId, useRef, useState } from "react";
import { useI18n } from "../i18n";
import { problemKey } from "../i18n/problems";
import { avatarColor, initialsOf } from "../planner/avatar";
import { usePickOptions } from "../planner/use-pick-options";
import { usePlan, type Failure, type Images, type PlanEntry } from "../planner/use-plan";
import { RecipeImage } from "../recipes/recipe-image";
import type { RecipeSummary } from "../recipes/use-recipes";
import { addDays, addWeeks, dateOf, isoWeek, mondayOf, toIsoDate } from "../shopping/week";
import { ProfileAvatar } from "./avatar";
import { CrossIcon, PadlockIcon, PlusIcon, SwapIcon, TrashIcon } from "./icons";
import { useKidRecipes } from "./kid-recipes";
import { useModal } from "./use-modal";

const failureText = (t: ReturnType<typeof useI18n>["t"], failure: Failure) =>
  failure === "unreachable" ? t("problem.unreachable") : t(problemKey(failure.problemType));

/**
 * The kid's week (T-091): seven big day tiles. Tap an empty day and pick a dinner among the dishes the household rules still
 * allow (the others are not shown at all); tap a day with a pick to see it, and change or remove it if it is your own. A locked
 * week (an adult froze it) shows a padlock and cannot be changed. Almost no text: a refusal is a short shake, never an error.
 */
export function KidWeek({ householdId, profileId }: { householdId: string; profileId: string | null }) {
  const { t, locale, formatNumber } = useI18n();
  const [weekStart, setWeekStart] = useState(() => mondayOf(new Date()));
  const { plan, images, add, remove, reload } = usePlan(householdId, weekStart);
  const [dayOpen, setDayOpen] = useState<string | null>(null);
  const [picking, setPicking] = useState<string | null>(null);
  const [pulse, setPulse] = useState(0);
  const [busy, setBusy] = useState(false);

  const today = toIsoDate(new Date());
  const days = Array.from({ length: 7 }, (_, i) => addDays(weekStart, i));
  const dayName = (date: string) => new Intl.DateTimeFormat(locale, { weekday: "long" }).format(dateOf(date));
  const dayShort = (date: string) => new Intl.DateTimeFormat(locale, { weekday: "short" }).format(dateOf(date));
  const { week } = isoWeek(weekStart);
  const entries = plan.status === "ok" ? plan.entries : [];
  const locked = plan.status === "ok" && plan.locked;

  const openDay = (date: string, dayEntries: PlanEntry[]) => {
    if (dayEntries.length > 0) setDayOpen(date);
    else if (locked) setPulse((n) => n + 1); // a short pulse on the padlock tells why nothing opens
    else setPicking(date);
  };

  const removeOwn = async (entry: PlanEntry) => {
    if (busy) return false;
    setBusy(true);
    const result = await remove(entry);
    setBusy(false);
    return result.ok;
  };

  return (
    <section className="kid-week" aria-label={t("kid.week.title")} data-testid="kid-week">
      <div className="kid-week-head">
        <button type="button" className="kid-round" aria-label={t("planner.previousWeek")} onClick={() => setWeekStart(addWeeks(weekStart, -1))}>
          <span aria-hidden="true">&lsaquo;</span>
        </button>
        <p className="kid-week-number" data-testid="kid-week-number">
          {formatNumber(week, { useGrouping: false })}
        </p>
        <span
          key={pulse}
          className={`kid-lock${locked ? " kid-lock-on" : ""}${pulse > 0 ? " kid-shake" : ""}`}
          role={locked ? "img" : undefined}
          aria-label={locked ? t("kid.week.locked") : undefined}
          aria-hidden={locked ? undefined : true}
          data-testid="kid-week-lock"
          data-locked={locked}
        >
          {locked && <PadlockIcon />}
        </span>
        <button type="button" className="kid-round" aria-label={t("planner.nextWeek")} onClick={() => setWeekStart(addWeeks(weekStart, 1))}>
          <span aria-hidden="true">&rsaquo;</span>
        </button>
      </div>

      {plan.status === "loading" && <p role="status">{t("kid.week.loading")}</p>}
      {plan.status === "error" && (
        <div role="alert" className="planner-message">
          <p>{failureText(t, plan.failure)}</p>
          <button type="button" className="button kid-big-button" onClick={reload}>
            {t("planner.retry")}
          </button>
        </div>
      )}

      {plan.status === "ok" && (
        <ol className="kid-days">
          {days.map((date) => {
            const dayEntries = entries.filter((e) => e.date === date);
            const first = dayEntries[0];
            const image = first ? images[first.recipeId] : undefined;
            return (
              <li key={date}>
                <button
                  type="button"
                  className={`kid-day${first ? " kid-day-filled" : ""}${date === today ? " kid-day-today" : ""}`}
                  aria-label={first ? `${dayName(date)}: ${first.recipeTitle}` : t("kid.week.addFor", { day: dayName(date) })}
                  aria-current={date === today ? "date" : undefined}
                  aria-disabled={locked && !first ? true : undefined}
                  data-testid="kid-day"
                  data-date={date}
                  data-filled={first ? "true" : "false"}
                  onClick={() => openDay(date, dayEntries)}
                >
                  <span className="kid-day-name">{dayShort(date)}</span>
                  <span className="kid-day-photo">
                    {first ? (
                      <>
                        <RecipeImage title={first.recipeTitle} thumbUrl={image?.thumbUrl} cardUrl={image?.cardUrl} sizes="(min-width: 40rem) 20rem, 60vw" />
                        <span className="kid-day-who">
                          <PickerBadge entry={first} />
                        </span>
                      </>
                    ) : (
                      <span className={`kid-day-add${locked ? " kid-day-add-off" : ""}`} aria-hidden="true" data-testid="kid-day-add">
                        {locked ? <PadlockIcon /> : <PlusIcon />}
                      </span>
                    )}
                  </span>
                </button>
              </li>
            );
          })}
        </ol>
      )}

      {dayOpen !== null && (
        <DaySheet
          title={dayName(dayOpen)}
          entries={entries.filter((e) => e.date === dayOpen)}
          images={images}
          profileId={profileId}
          locked={locked}
          busy={busy}
          onClose={() => setDayOpen(null)}
          onRemove={async (entry) => {
            const ok = await removeOwn(entry);
            if (ok && entries.filter((e) => e.date === dayOpen).length <= 1) setDayOpen(null);
            return ok;
          }}
          onSwap={async (entry) => {
            const date = dayOpen;
            if (await removeOwn(entry)) {
              setDayOpen(null);
              setPicking(date);
              return true;
            }
            return false;
          }}
        />
      )}

      {picking !== null && (
        <PickSheet
          key={picking}
          householdId={householdId}
          weekStart={weekStart}
          date={picking}
          title={dayName(picking)}
          onClose={() => setPicking(null)}
          onPick={async (recipe) => {
            const result = await add(picking, recipe);
            if (result.ok) setPicking(null);
            return result.ok;
          }}
        />
      )}
    </section>
  );
}

/** Who picked it: the kid's avatar, or the account's initials when a grown-up did. */
function PickerBadge({ entry }: { entry: PlanEntry }) {
  const { t } = useI18n();
  if (entry.pickedByProfile) {
    return <ProfileAvatar profile={entry.pickedByProfile} size="sm" label={entry.pickedByProfile.displayName} />;
  }
  const who = entry.pickedBy.email ?? t("planner.unknownPicker");
  return (
    <span
      className="kid-avatar kid-avatar-sm kid-avatar-adult"
      role="img"
      aria-label={who}
      style={{ backgroundColor: avatarColor(entry.pickedBy.userId) }}
    >
      {initialsOf(entry.pickedBy.email, entry.pickedBy.userId)}
    </span>
  );
}

function DaySheet({
  title,
  entries,
  images,
  profileId,
  locked,
  busy,
  onClose,
  onRemove,
  onSwap,
}: {
  title: string;
  entries: PlanEntry[];
  images: Images;
  profileId: string | null;
  locked: boolean;
  busy: boolean;
  onClose: () => void;
  onRemove: (entry: PlanEntry) => Promise<boolean>;
  onSwap: (entry: PlanEntry) => Promise<boolean>;
}) {
  const { t } = useI18n();
  const sheet = useRef<HTMLDivElement>(null);
  const headingId = useId();
  const [refused, setRefused] = useState(0);
  useModal(sheet, onClose);

  const run = async (action: () => Promise<boolean>) => {
    if (!(await action())) setRefused((n) => n + 1);
  };

  return (
    <div className="kid-backdrop">
      <div ref={sheet} className="kid-sheet kid-daysheet" role="dialog" aria-modal="true" aria-labelledby={headingId} tabIndex={-1}>
        <div className="kid-picker-head">
          <h2 id={headingId}>{title}</h2>
          <button type="button" className="kid-close" aria-label={t("kid.close")} onClick={onClose}>
            <CrossIcon />
          </button>
        </div>
        <ul className="kid-dishes">
          {entries.map((entry) => {
            const image = images[entry.recipeId];
            const own = profileId !== null && entry.pickedByProfile?.id === profileId;
            return (
              <li key={entry.id} data-testid="kid-entry" data-own={own}>
                <div className="kid-dish-photo">
                  <RecipeImage title={entry.recipeTitle} thumbUrl={image?.thumbUrl} cardUrl={image?.cardUrl} sizes="(min-width: 40rem) 40rem, 100vw" />
                  <span className="kid-day-who kid-day-who-big">
                    <PickerBadge entry={entry} />
                  </span>
                </div>
                <p className="kid-dish-title">{entry.recipeTitle}</p>
                {locked ? (
                  <div className="kid-lock kid-lock-on kid-lock-inline" role="img" aria-label={t("kid.week.locked")}>
                    <PadlockIcon />
                  </div>
                ) : (
                  own && (
                    <div key={refused} className={refused > 0 ? "kid-entry-actions kid-shake" : "kid-entry-actions"}>
                      <button
                        type="button"
                        className="kid-action"
                        disabled={busy}
                        aria-label={t("kid.week.swap", { title: entry.recipeTitle })}
                        data-testid="kid-swap"
                        onClick={() => void run(() => onSwap(entry))}
                      >
                        <SwapIcon />
                      </button>
                      <button
                        type="button"
                        className="kid-action kid-action-danger"
                        disabled={busy}
                        aria-label={t("kid.week.remove", { title: entry.recipeTitle })}
                        data-testid="kid-remove"
                        onClick={() => void run(() => onRemove(entry))}
                      >
                        <TrashIcon />
                      </button>
                    </div>
                  )
                )}
              </li>
            );
          })}
        </ul>
      </div>
    </div>
  );
}

/** The dishes that still fit the day (T-090): the rest are not shown, so a child never meets a "no". */
function PickSheet({
  householdId,
  weekStart,
  date,
  title,
  onClose,
  onPick,
}: {
  householdId: string;
  weekStart: string;
  date: string;
  title: string;
  onClose: () => void;
  onPick: (recipe: RecipeSummary) => Promise<boolean>;
}) {
  const { t } = useI18n();
  const sheet = useRef<HTMLDivElement>(null);
  const headingId = useId();
  const { recipes, retry } = useKidRecipes(householdId);
  const { options, byRecipe, loading, refresh } = usePickOptions(householdId, weekStart, date);
  const [refused, setRefused] = useState<{ id: string; n: number } | null>(null);
  const [adding, setAdding] = useState(false);
  useModal(sheet, onClose);

  // Without an answer from the rules lookup nothing is known to be forbidden; the server still refuses a wrong pick (409).
  const allowed =
    recipes.status === "ok" ? recipes.items.filter((r) => options === null || byRecipe.get(r.id)?.allowed === true) : [];
  const ready = recipes.status === "ok" && !loading;

  const choose = async (recipe: RecipeSummary) => {
    if (adding) return;
    setAdding(true);
    const ok = await onPick(recipe);
    if (!ok) {
      setAdding(false);
      setRefused((prev) => ({ id: recipe.id, n: (prev?.n ?? 0) + 1 }));
      refresh(); // the rules or the week changed under us: show what is allowed now
    }
  };

  return (
    <div className="kid-backdrop">
      <div ref={sheet} className="kid-sheet kid-pickersheet" role="dialog" aria-modal="true" aria-labelledby={headingId} tabIndex={-1} data-testid="kid-pick-sheet">
        <div className="kid-picker-head">
          <h2 id={headingId}>{title}</h2>
          <button type="button" className="kid-close" aria-label={t("kid.close")} onClick={onClose}>
            <CrossIcon />
          </button>
        </div>
        {!ready && recipes.status !== "error" && <p role="status">{t("kid.week.loading")}</p>}
        {recipes.status === "error" && (
          <div role="alert" className="planner-message">
            <p>{recipes.unreachable ? t("problem.unreachable") : t(problemKey(recipes.problemType))}</p>
            <button type="button" className="button kid-big-button" onClick={retry}>
              {t("planner.retry")}
            </button>
          </div>
        )}
        {ready && allowed.length === 0 && (
          <p className="planner-message" data-testid="kid-nothing">
            {t("kid.week.nothingFits")}
          </p>
        )}
        {ready && allowed.length > 0 && (
          <ul className="kid-pick-grid" data-testid="kid-pick-grid">
            {allowed.map((recipe) => (
              <li key={recipe.id}>
                <button
                  key={refused?.id === recipe.id ? refused.n : 0}
                  type="button"
                  className={refused?.id === recipe.id ? "kid-pick kid-shake" : "kid-pick"}
                  aria-label={t("kid.week.pick", { title: recipe.title })}
                  disabled={adding}
                  data-testid="kid-pick"
                  onClick={() => void choose(recipe)}
                >
                  <RecipeImage title={recipe.title} thumbUrl={recipe.thumbUrl} cardUrl={recipe.cardUrl} sizes="(min-width: 40rem) 20rem, 50vw" />
                  <span className="kid-pick-title">{recipe.title}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
