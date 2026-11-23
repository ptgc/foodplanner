import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, resetAntiforgeryToken } from "../api/client";
import type { components } from "../api/schema";
import type { MessageKey } from "../i18n";
import { photoEn } from "../i18n/photo-en";
import { photoNb } from "../i18n/photo-nb";
import { SessionProvider } from "../session";
import { t } from "../test-i18n";
import Recipes from "./recipes";
import RecipeImportPhoto from "./recipe-import-photo";

type Role = components["schemas"]["HouseholdRole"];
type Usage = components["schemas"]["PhotoUsageStatusResponse"];

const me = { userId: "11111111-1111-1111-1111-111111111111", email: "adult@example.test" };
const urn = (code: string) => `urn:foodplanner:problem:${code}`;
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" } });

let role: Role;
let usage: Usage | "fail";
let usageCalls: number;

beforeEach(() => {
  resetAntiforgeryToken();
  role = "Adult";
  usage = { enabled: true, unlimited: false, dailyLimit: 2, remainingToday: 2 };
  usageCalls = 0;
  URL.createObjectURL = vi.fn(() => "blob:preview");
  URL.revokeObjectURL = vi.fn();
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === "/api/auth/me") return json(me);
      if (path === "/api/auth/antiforgery") return json({ token: "t" });
      if (path === "/api/households") return json([{ id: "h1", name: "Home", role }]);
      if (path === "/api/households/h1/imports/photo-usage") {
        usageCalls++;
        return usage === "fail" ? json({ type: urn("forbidden") }, 403) : json(usage);
      }
      if (path === "/api/households/h1/recipes") return json({ items: [], total: 0 });
      if (path === "/api/households/h1/tags") return json([]);
      if (path === "/api/households/h1/kid-profiles") return json([]);
      throw new Error(`unexpected ${request.method} ${path}`);
    }),
  );
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

function renderScreen() {
  return render(
    <MemoryRouter initialEntries={["/recipes/import/photo"]}>
      <SessionProvider>
        <Routes>
          <Route path="/recipes/import/photo" element={<RecipeImportPhoto />} />
          <Route path="*" element={<Where />} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  );
}

const jpeg = (name = "page.jpg") => new File(["x"], name, { type: "image/jpeg" });
const draft = (patch: Partial<components["schemas"]["RecipeImportResponse"]> = {}) =>
  ({ id: "d1", source: "Photo", photoUsage: { remainingToday: 1, dailyLimit: 2, unlimited: false }, ...patch }) as components["schemas"]["RecipeImportResponse"];

/** Replaces the generated client's POST for the photo call; `answer` decides the result. Returns what was sent. */
function fakePhotoPost(answer: (init: { signal?: AbortSignal }) => Promise<unknown>) {
  const sent: { photos: File[]; sourceName: string | null }[] = [];
  const spy = vi.spyOn(api, "POST").mockImplementation((async (path: string, init: { bodySerializer?: (b: unknown) => FormData; body?: unknown; signal?: AbortSignal }) => {
    if (!path.endsWith("/imports/photo")) throw new Error(`unexpected POST ${path}`);
    const form = init.bodySerializer!(init.body);
    sent.push({ photos: form.getAll("photos") as File[], sourceName: (form.get("sourceName") as string | null) ?? null });
    return answer(init);
  }) as never);
  return { sent, spy };
}

const reply = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  Promise.resolve({
    data: status < 300 ? body : undefined,
    error: status >= 300 ? body : undefined,
    response: new Response(null, { status, headers }),
  });

async function open() {
  renderScreen();
  await screen.findByRole("heading", { level: 1, name: t("photo.title") });
  await screen.findByTestId("photo-privacy");
}
const send = () => screen.getByRole("button", { name: t("photo.send") });
const addPhotos = async (user: ReturnType<typeof userEvent.setup>, ...files: File[]) =>
  user.upload(screen.getByTestId("photo-files-input"), files);

describe("photo import screen", () => {
  it("offers a camera input and a multi-file picker, both as 44 px buttons, and the send button needs a photo", async () => {
    await open();
    const camera = screen.getByTestId("photo-camera-input");
    expect(camera).toHaveAttribute("accept", "image/*");
    expect(camera).toHaveAttribute("capture", "environment");
    const files = screen.getByTestId("photo-files-input");
    expect(files).toHaveAttribute("accept", "image/jpeg,image/png,image/webp");
    expect(files).toHaveAttribute("multiple");
    expect(screen.getByRole("button", { name: t("photo.picker.camera") })).toBeEnabled();
    expect(screen.getByRole("button", { name: t("photo.picker.files") })).toBeEnabled();
    expect(send()).toBeDisabled();
    expect(screen.getByText(t("photo.sendNeedsPhoto"))).toBeInTheDocument();
  });

  it("shows the privacy sentence before anything is sent", async () => {
    await open();
    expect(screen.getByTestId("photo-privacy")).toHaveTextContent(t("photo.privacy"));
    expect(t("photo.privacy")).toMatch(/Anthropic/);
  });

  it("shows thumbnails with a labelled remove button, and refuses the fifth photo with a message", async () => {
    const user = userEvent.setup();
    await open();
    await addPhotos(user, jpeg("a.jpg"), jpeg("b.jpg"), jpeg("c.jpg"), jpeg("d.jpg"));
    expect(screen.getAllByTestId("photo-thumb")).toHaveLength(4);
    expect(screen.getByRole("heading", { name: t("photo.picker.heading", { n: 4, max: 4 }) })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: t("photo.picker.camera") })).toBeDisabled();

    await user.click(screen.getByRole("button", { name: t("photo.picker.remove", { n: 2 }) }));
    expect(screen.getAllByTestId("photo-thumb")).toHaveLength(3);

    await addPhotos(user, jpeg("e.jpg"), jpeg("f.jpg"));
    expect(screen.getAllByTestId("photo-thumb")).toHaveLength(4);
    expect(screen.getByTestId("photo-too-many")).toHaveTextContent(t("photo.picker.tooMany", { max: 4 }));
    expect(send()).toBeEnabled();
  });

  it("shows the count left today and, with no limit on the plan, no counter", async () => {
    await open();
    expect(screen.getByTestId("photo-left")).toHaveTextContent(t("photo.left", { remaining: 2, limit: 2 }));
  });

  it("shows no counter when the plan is unlimited", async () => {
    usage = { enabled: true, unlimited: true, dailyLimit: null, remainingToday: null };
    await open();
    expect(screen.queryByTestId("photo-left")).toBeNull();
    const user = userEvent.setup();
    await addPhotos(user, jpeg());
    expect(send()).toBeEnabled();
  });

  it("at zero left disables the send button and says why, without any request", async () => {
    usage = { enabled: true, unlimited: false, dailyLimit: 2, remainingToday: 0 };
    const { spy } = fakePhotoPost(() => reply(201, draft()));
    const user = userEvent.setup();
    await open();
    expect(screen.getByTestId("photo-limit")).toHaveTextContent(t("photo.limitNone"));
    await addPhotos(user, jpeg());
    expect(send()).toBeDisabled();
    expect(spy).not.toHaveBeenCalled();
  });

  it("says the feature is not available when the server has it off", async () => {
    usage = { enabled: false, unlimited: false, dailyLimit: 2, remainingToday: 0 };
    renderScreen();
    expect(await screen.findByTestId("photo-unavailable")).toHaveTextContent(t("photo.unavailable"));
    expect(screen.queryByTestId("photo-privacy")).toBeNull();
  });

  it("sends the photos as multipart parts `photos` with the book name and opens the review on 201", async () => {
    const { sent } = fakePhotoPost(() => reply(201, draft({ id: "d9" })));
    const user = userEvent.setup();
    await open();
    await addPhotos(user, jpeg("one.jpg"), jpeg("two.jpg"));
    await user.type(screen.getByLabelText(t("photo.sourceName")), "Kaptein Sabeltann");
    await user.click(send());

    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/recipes/import?draft=d9"));
    expect(sent).toHaveLength(1);
    expect(sent[0]!.photos.map((f) => f.name)).toEqual(["one.jpg", "two.jpg"]);
    expect(sent[0]!.sourceName).toBe("Kaptein Sabeltann");
  });

  it("shows progress, blocks a second submit and the form while it runs, and Cancel aborts and keeps the photos", async () => {
    const { spy } = fakePhotoPost(
      (init) =>
        new Promise((_resolve, reject) => {
          init.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
        }),
    );
    const user = userEvent.setup();
    await open();
    await addPhotos(user, jpeg());
    await user.click(send());

    await waitFor(() => expect(screen.getByTestId("photo-progress")).toHaveTextContent(t("photo.reading")));
    const form = screen.getByTestId("photo-progress").closest("form")!;
    expect(form).toHaveAttribute("aria-busy", "true");
    expect(send()).toBeDisabled();
    expect(screen.getByRole("button", { name: t("photo.picker.remove", { n: 1 }) })).toBeDisabled();
    expect(spy).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole("button", { name: t("photo.cancel") }));
    expect(await screen.findByTestId("photo-cancelled")).toHaveTextContent(t("photo.cancelled"));
    expect(screen.getAllByTestId("photo-thumb")).toHaveLength(1);
    expect(send()).toBeEnabled();
    expect(screen.queryByTestId("where")).toBeNull();
  });

  const problems: [string, number, string, Record<string, string>, MessageKey][] = [
    ["limit reached", 429, urn("photo-import-limit-reached"), {}, "photo.problem.limitReached"],
    ["capacity", 503, urn("photo-import-capacity"), {}, "photo.problem.capacity"],
    ["not a recipe", 422, urn("photo-import-not-a-recipe"), {}, "photo.problem.notARecipe"],
    ["unreadable", 422, urn("photo-import-unreadable"), {}, "photo.problem.unreadable"],
    ["refused", 422, urn("photo-import-refused"), {}, "photo.problem.refused"],
    ["failed", 502, urn("photo-import-failed"), {}, "photo.problem.failed"],
    ["timeout", 504, urn("photo-import-timeout"), {}, "photo.problem.timeout"],
    ["busy", 503, urn("photo-import-busy"), {}, "photo.problem.busy"],
    ["disabled", 404, urn("photo-import-disabled"), {}, "photo.problem.disabled"],
    ["too many drafts", 409, urn("import-limit"), {}, "import.problem.limit"],
    ["forbidden", 403, urn("forbidden"), {}, "problem.forbidden"],
    ["kid mode", 403, urn("kid-mode"), {}, "kid.problem.kidMode"],
  ];
  it.each(problems)("%s: shows its own alert, focuses it, keeps the photos and the counter follows remainingToday", async (_name, status, type, headers, textKey) => {
    fakePhotoPost(() => reply(status, { type, remainingToday: 1, dailyLimit: 2 }, headers));
    const user = userEvent.setup();
    await open();
    await addPhotos(user, jpeg());
    await user.click(send());

    const alert = await screen.findByTestId("photo-problem");
    expect(alert).toHaveAttribute("role", "alert");
    expect(alert).toHaveTextContent(t(textKey));
    await waitFor(() => expect(alert).toHaveFocus());
    expect(screen.getAllByTestId("photo-thumb")).toHaveLength(1);
    expect(screen.getByTestId("photo-left")).toHaveTextContent(t("photo.left", { remaining: 1, limit: 2 }));
    expect(send()).toBeEnabled();
  });

  it.each(["photo-import-not-a-recipe", "photo-import-unreadable", "photo-import-refused", "photo-import-failed", "photo-import-timeout"])(
    "%s offers the text import as the next step",
    async (code) => {
      fakePhotoPost(() => reply(422, { type: urn(code), remainingToday: 1 }));
      const user = userEvent.setup();
      await open();
      await addPhotos(user, jpeg());
      await user.click(send());
      const alert = await screen.findByTestId("photo-problem");
      expect(within(alert).getByRole("link", { name: t("photo.textInstead") })).toHaveAttribute("href", "/recipes/import");
    },
  );

  it("limit reached drops the counter to zero and disables the button", async () => {
    fakePhotoPost(() => reply(429, { type: urn("photo-import-limit-reached"), remainingToday: 0, dailyLimit: 2 }, { "Retry-After": "3600" }));
    const user = userEvent.setup();
    await open();
    await addPhotos(user, jpeg());
    await user.click(send());
    await screen.findByTestId("photo-problem");
    expect(screen.getByTestId("photo-limit")).toBeInTheDocument();
    expect(send()).toBeDisabled();
  });

  it("429 rate-limited says how long to wait, from Retry-After", async () => {
    fakePhotoPost(() => reply(429, { type: urn("rate-limited") }, { "Retry-After": "240" }));
    const user = userEvent.setup();
    await open();
    await addPhotos(user, jpeg());
    await user.click(send());
    expect((await screen.findByTestId("photo-problem")).textContent).toBe(
      t("photo.problem.rateLimited", { wait: t("photo.wait.minutes", { n: 4 }) }),
    );
  });

  it("a 400 names the photo the server could not use", async () => {
    fakePhotoPost(() => reply(400, { errors: { "photos[1]": ["bad"] } }));
    const user = userEvent.setup();
    await open();
    await addPhotos(user, jpeg("a.jpg"), jpeg("b.jpg"));
    await user.click(send());
    expect(await screen.findByTestId("photo-problem")).toHaveTextContent(t("photo.problem.invalid", { n: 2 }));
  });

  it("a network error has its own message and keeps the photos", async () => {
    fakePhotoPost(() => Promise.reject(new TypeError("Failed to fetch")));
    const user = userEvent.setup();
    await open();
    await addPhotos(user, jpeg());
    await user.click(send());
    expect(await screen.findByTestId("photo-problem")).toHaveTextContent(t("problem.unreachable"));
    expect(screen.getAllByTestId("photo-thumb")).toHaveLength(1);
  });

  it("a photo the browser cannot shrink and that is over 10 MB is named, and nothing is sent", async () => {
    const { spy } = fakePhotoPost(() => reply(201, draft()));
    const user = userEvent.setup();
    await open();
    const big = jpeg("huge.heic");
    Object.defineProperty(big, "size", { value: 11 * 1024 * 1024 });
    await addPhotos(user, big);
    await user.click(send());
    expect(await screen.findByTestId("photo-problem")).toHaveTextContent("huge.heic");
    expect(spy).not.toHaveBeenCalled();
  });

  it("shows a message instead of the form for a Child", async () => {
    role = "Child";
    renderScreen();
    expect(await screen.findByTestId("no-access")).toHaveTextContent(t("import.noAccess"));
    expect(usageCalls).toBe(0);
  });
});

describe("the entry on the recipes page", () => {
  const renderRecipes = () =>
    render(
      <MemoryRouter initialEntries={["/recipes"]}>
        <SessionProvider>
          <Recipes />
        </SessionProvider>
      </MemoryRouter>,
    );

  it("is a 44 px-class link for an Adult when the feature is on", async () => {
    renderRecipes();
    const link = await screen.findByRole("link", { name: t("import.photoEntry") });
    expect(link).toHaveAttribute("href", "/recipes/import/photo");
    expect(link).toHaveClass("button");
  });

  it("is absent when the feature is off", async () => {
    usage = { enabled: false, unlimited: false, dailyLimit: 2, remainingToday: 0 };
    renderRecipes();
    await screen.findByTestId("recipe-import-link");
    await waitFor(() => expect(usageCalls).toBe(1));
    expect(screen.queryByRole("link", { name: t("import.photoEntry") })).toBeNull();
  });

  it("is absent when the usage request fails", async () => {
    usage = "fail";
    renderRecipes();
    await screen.findByTestId("recipe-import-link");
    await waitFor(() => expect(usageCalls).toBe(1));
    expect(screen.queryByRole("link", { name: t("import.photoEntry") })).toBeNull();
  });

  it("is absent for a Child, who does not even ask", async () => {
    role = "Child";
    renderRecipes();
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("link", { name: t("import.photoEntry") })).toBeNull();
    expect(usageCalls).toBe(0);
  });
});

describe("messages", () => {
  it("has every photo message in nb and en, with the same placeholders", () => {
    const params = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
    expect(Object.keys(photoNb).sort()).toEqual(Object.keys(photoEn).sort());
    for (const [key, text] of Object.entries(photoEn)) expect(params(photoNb[key as keyof typeof photoNb]), key).toEqual(params(text));
  });
});
