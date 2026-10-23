import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import Home from "./home";

function json(body: unknown, init: ResponseInit & { type?: string } = {}) {
  return new Response(JSON.stringify(body), {
    status: init.status ?? 200,
    headers: { "content-type": init.type ?? "application/json" },
  });
}

const problem = (title: string, status: number) =>
  json({ title, status }, { status, type: "application/problem+json" });

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
    expect(screen.getByText(/checking the api/i)).toBeInTheDocument();
    expect(await screen.findByText("API is Healthy.")).toBeInTheDocument();
    expect(await screen.findByText("Database: Up")).toBeInTheDocument();

    const paths = fetchMock.mock.calls.map(([request]) => new URL(request.url).pathname);
    expect(paths).toEqual(["/api/health", "/api/health/ready"]);
    expect(fetchMock.mock.calls.every(([request]) => request.method === "GET")).toBe(true);
  });

  it("shows database Down when readiness returns 503", async () => {
    stubApi({ "/api/health": healthy, "/api/health/ready": dbDown });
    render(<Home />);
    expect(await screen.findByText("API is Healthy.")).toBeInTheDocument();
    expect(await screen.findByText("Database: Down")).toBeInTheDocument();
  });

  it("shows an error from a ProblemDetails response", async () => {
    stubApi({ "/api/health": () => problem("Boom", 500), "/api/health/ready": dbDown });
    render(<Home />);
    expect(await screen.findByText(/API check failed: Boom/)).toBeInTheDocument();
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
    expect(await screen.findByText(/could not reach the api/i)).toBeInTheDocument();
    expect(await screen.findByText("Database: Unknown")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: /check again/i }));
    expect(await screen.findByText("API is Healthy.")).toBeInTheDocument();
    expect(await screen.findByText("Database: Up")).toBeInTheDocument();
  });
});
