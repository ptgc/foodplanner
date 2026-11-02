import { vi } from "vitest";
import type { components } from "../api/schema";

/** A fake API for the kid tests: profiles, recipes with likes, one household's week plan, pick options, activate and unlock. */
type Summary = components["schemas"]["RecipeSummaryResponse"];
type Entry = components["schemas"]["MealPlanEntryResponse"];
type Profile = components["schemas"]["ProfileResponse"];
type Tag = components["schemas"]["TagResponse"];

export const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json", ...headers },
  });
export const problem = (status: number, code: string, extra: Record<string, unknown> = {}) =>
  json({ status, title: "x", type: `urn:foodplanner:problem:${code}`, ...extra }, status);

export const adultUser = { userId: "11111111-1111-1111-1111-111111111111", email: "anne.berit@example.test" };
export const barnevennlig: Tag = { id: "tag-kid", name: "Barnevennlig", kind: "Tag", isGlobal: true };

export const recipe = (id: string, title: string, tags: Tag[] = [], liked = false): Summary => ({
  id,
  title,
  servings: 4,
  totalMinutes: 30,
  tags,
  thumbUrl: null,
  cardUrl: null,
  likedBy: [],
  liked,
  foodType: "Unknown",
});

export type Fake = {
  role: "Owner" | "Adult" | "Child";
  kidMode: { profileId: string | null; householdId: string } | null;
  profiles: Profile[];
  recipes: Summary[];
  liked: Set<string>;
  entries: Entry[];
  locked: boolean;
  pinSet: boolean;
  /** The PIN that unlocks; a wrong one counts down from 5. */
  pin: string;
  wrongPins: number;
  /** /api/auth/me answers 500 (the read after a switch fails). */
  meFails: boolean;
  /** Recipe ids the rules allow per date; a date missing allows all. */
  allowed: Record<string, string[]>;
  /** Forces the next entry POST to answer with this. */
  postError: Response | null;
  /** Forces the next like write to answer with this. */
  likeError: Response | null;
  calls: string[];
};

export function newFake(): Fake {
  return {
    role: "Owner",
    kidMode: null,
    profiles: [],
    recipes: [],
    liked: new Set(),
    entries: [],
    locked: false,
    pinSet: true,
    pin: "4827",
    wrongPins: 0,
    meFails: false,
    allowed: {},
    postError: null,
    likeError: null,
    calls: [],
  };
}

let nextId = 1;

export function install(fake: Fake) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const path = url.pathname;
      const method = request.method;
      fake.calls.push(`${method} ${path}`);
      if (path === "/api/auth/me" && fake.meFails) return json({ status: 500, title: "x" }, 500);
      if (path === "/api/auth/me") return json({ ...adultUser, kidMode: fake.kidMode });
      if (path === "/api/households") {
        return json([{ id: "h1", name: "Home", role: fake.kidMode ? "Child" : fake.role }]);
      }
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households/h1/tags") return json([barnevennlig]);
      if (path === "/api/households/h1/profiles" && method === "GET") return json(fake.profiles);
      const activate = path.match(/^\/api\/households\/h1\/profiles\/([^/]+)\/activate$/);
      if (activate && method === "POST") {
        if (!fake.kidMode && !fake.pinSet) return problem(409, "pin-not-set");
        fake.kidMode = { profileId: activate[1]!, householdId: "h1" };
        return new Response(null, { status: 204 });
      }
      if (path === "/api/auth/pin/unlock" && method === "POST") {
        if (!fake.kidMode) return problem(409, "not-in-kid-mode");
        const body = (await request.json()) as { pin: string };
        if (body.pin === fake.pin) {
          fake.kidMode = null;
          return new Response(null, { status: 204 });
        }
        fake.wrongPins++;
        return problem(403, "pin-incorrect", { attemptsRemaining: 5 - fake.wrongPins });
      }
      if (path === "/api/auth/pin" && method === "PUT") {
        fake.pinSet = true;
        return new Response(null, { status: 204 });
      }
      if (path === "/api/households/h1/recipes" && method === "GET") {
        const offset = Number(url.searchParams.get("offset") ?? 0);
        const limit = Number(url.searchParams.get("limit") ?? 12);
        const all = fake.recipes.map((r) => ({ ...r, liked: fake.kidMode ? fake.liked.has(r.id) : false }));
        return json({ items: all.slice(offset, offset + limit), total: all.length, limit, offset });
      }
      const like = path.match(/^\/api\/households\/h1\/recipes\/([^/]+)\/like$/);
      if (like) {
        if (fake.likeError) {
          const failure = fake.likeError;
          fake.likeError = null;
          return failure;
        }
        if (method === "PUT") fake.liked.add(like[1]!);
        else fake.liked.delete(like[1]!);
        return new Response(null, { status: 204 });
      }
      const detail = path.match(/^\/api\/households\/h1\/recipes\/([^/]+)$/);
      if (detail) return json({ id: detail[1], title: "x", image: null });
      if (path.endsWith("/pick-options")) {
        const date = url.searchParams.get("date")!;
        const only = fake.allowed[date];
        return json({
          date,
          openDinnerDays: 1,
          allowedFoodTypes: ["Fish", "Vegetarian"],
          recipes: fake.recipes.map((r) => ({
            recipeId: r.id,
            allowed: only ? only.includes(r.id) : true,
            reason: only && !only.includes(r.id) ? "breaks-mix" : null,
          })),
        });
      }
      const lock = path.match(/^\/api\/households\/h1\/meal-plans\/([^/]+)\/lock$/);
      if (lock) {
        fake.locked = method === "PUT";
        return method === "PUT"
          ? json({ locked: true, lockedAt: "2031-03-01T10:00:00Z", lockedBy: adultUser })
          : new Response(null, { status: 204 });
      }
      const plan = path.match(/^\/api\/households\/h1\/meal-plans\/([^/]+)(?:\/entries(?:\/([^/]+))?)?$/);
      if (plan) {
        const [, weekStart, entryId] = plan;
        if (method === "GET") {
          return json({ id: null, weekStart, entries: fake.entries, locked: fake.locked, lockedAt: null, lockedBy: null });
        }
        if (method === "POST") {
          if (fake.postError) {
            const failure = fake.postError;
            fake.postError = null;
            return failure;
          }
          const body = (await request.json()) as { date: string; recipeId: string };
          const r = fake.recipes.find((x) => x.id === body.recipeId)!;
          const profile = fake.profiles.find((p) => p.id === fake.kidMode?.profileId) ?? null;
          const created: Entry = {
            id: `e${nextId++}`,
            date: body.date,
            recipeId: r.id,
            recipeTitle: r.title,
            servings: 4,
            pickedBy: adultUser,
            pickedByProfile: profile,
          };
          fake.entries.push(created);
          return json(created, 201);
        }
        if (method === "DELETE") {
          const own = fake.entries.find((e) => e.id === entryId);
          if (fake.locked) return problem(409, "week-locked");
          if (own && fake.kidMode && own.pickedByProfile?.id !== fake.kidMode.profileId) return problem(403, "forbidden");
          fake.entries = fake.entries.filter((e) => e.id !== entryId);
          return new Response(null, { status: 204 });
        }
      }
      throw new Error(`unexpected ${method} ${path}`);
    }),
  );
}

export const pickedEntry = (id: string, date: string, r: Summary, by: { profile?: Profile | null } = {}): Entry => ({
  id,
  date,
  recipeId: r.id,
  recipeTitle: r.title,
  servings: 4,
  pickedBy: adultUser,
  pickedByProfile: by.profile ?? null,
});
