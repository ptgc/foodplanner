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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Home health page", () => {
  it("shows loading, then the health result from GET /api/health", async () => {
    const fetchMock = vi.fn(async () => json({ status: "Healthy" }));
    vi.stubGlobal("fetch", fetchMock);

    render(<Home />);
    expect(screen.getByText(/checking the api/i)).toBeInTheDocument();
    expect(await screen.findByText("API is Healthy.")).toBeInTheDocument();

    const request = (fetchMock.mock.calls[0] as unknown as [Request])[0];
    expect(new URL(request.url).pathname).toBe("/api/health");
    expect(request.method).toBe("GET");
  });

  it("shows an error from a ProblemDetails response", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => json({ title: "Boom", status: 500 }, { status: 500, type: "application/problem+json" })),
    );
    render(<Home />);
    expect(await screen.findByText(/API check failed: Boom/)).toBeInTheDocument();
  });

  it("shows an error when the API is unreachable and retries", async () => {
    const fetchMock = vi
      .fn()
      .mockRejectedValueOnce(new TypeError("network"))
      .mockResolvedValueOnce(json({ status: "Healthy" }));
    vi.stubGlobal("fetch", fetchMock);

    render(<Home />);
    expect(await screen.findByText(/could not reach the api/i)).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: /check again/i }));
    expect(await screen.findByText("API is Healthy.")).toBeInTheDocument();
  });
});
