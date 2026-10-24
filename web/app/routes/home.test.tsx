import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { t } from "../test-i18n";
import Home from "./home";

function json(body: unknown, init: ResponseInit & { type?: string } = {}) {
  return new Response(JSON.stringify(body), {
    status: init.status ?? 200,
    headers: { "content-type": init.type ?? "application/json" },
  });
}

const problem = (title: string, status: number, type?: string) =>
  json({ title, status, type }, { status, type: "application/problem+json" });

/** Answers each request by pathname; a handler may throw to simulate a network failure. */
function stubApi(routes: Record<string, () => Response>) {
  const fetchMock = vi.fn(async (request: Request) => {
    const handler = routes[new URL(request.url).pathname];
    if (!handler) throw new Error(`unexpected ${request.url}`);
    return handler();
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

const healthy = () => json({ status: "Healthy" });
const dbUp = () => json({ status: "Healthy", database: "Up" });
const dbDown = () => problem("Database unavailable", 503);

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Home health page", () => {
  it("shows loading, then API status and database Up", async () => {
    const fetchMock = stubApi({ "/api/health": healthy, "/api/health/ready": dbUp });

    render(<Home />);
    expect(screen.getByText(t("home.checkingApi"))).toBeInTheDocument();
    expect(await screen.findByText(t("home.apiStatus", { status: t("home.status.healthy") }))).toBeInTheDocument();
    expect(await screen.findByText(t("home.databaseUp"))).toBeInTheDocument();

    const paths = fetchMock.mock.calls.map(([request]) => new URL(request.url).pathname);
    expect(paths).toEqual(["/api/health", "/api/health/ready"]);
    expect(fetchMock.mock.calls.every(([request]) => request.method === "GET")).toBe(true);
  });

  it("shows database Down when readiness returns 503", async () => {
    stubApi({ "/api/health": healthy, "/api/health/ready": dbDown });
    render(<Home />);
    expect(await screen.findByText(t("home.apiStatus", { status: t("home.status.healthy") }))).toBeInTheDocument();
    expect(await screen.findByText(t("home.databaseDown"))).toBeInTheDocument();
  });

  it("shows a translated message for a known ProblemDetails type, never the server title", async () => {
    stubApi({
      "/api/health": () => problem("Boom", 429, "urn:foodplanner:problem:rate-limited"),
      "/api/health/ready": dbDown,
    });
    render(<Home />);
    expect(await screen.findByText(t("home.apiFailed", { reason: t("problem.rateLimited") }))).toBeInTheDocument();
    expect(screen.queryByText(/Boom/)).toBeNull();
  });

  it("falls back to the generic message for an unknown ProblemDetails type", async () => {
    stubApi({
      "/api/health": () => problem("Boom", 500, "urn:foodplanner:problem:something-new"),
      "/api/health/ready": dbDown,
    });
    render(<Home />);
    expect(await screen.findByText(t("home.apiFailed", { reason: t("problem.generic") }))).toBeInTheDocument();
  });

  it("shows an error when the API is unreachable, database Unknown, and retries", async () => {
    const fetchMock = vi
      .fn()
      .mockRejectedValueOnce(new TypeError("network"))
      .mockRejectedValueOnce(new TypeError("network"))
      .mockImplementation(async (request: Request) =>
        new URL(request.url).pathname === "/api/health" ? healthy() : dbUp(),
      );
    vi.stubGlobal("fetch", fetchMock);

    render(<Home />);
    expect(await screen.findByText(t("home.apiFailed", { reason: t("problem.unreachable") }))).toBeInTheDocument();
    expect(await screen.findByText(t("home.databaseUnknown"))).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: t("home.checkAgain") }));
    expect(await screen.findByText(t("home.apiStatus", { status: t("home.status.healthy") }))).toBeInTheDocument();
    expect(await screen.findByText(t("home.databaseUp"))).toBeInTheDocument();
  });
});
