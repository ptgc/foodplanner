import { describe, expect, it } from "vitest";
import { loginPathFor, safeReturnTo } from "./auth-paths";

describe("safeReturnTo", () => {
  it("keeps same-origin paths with query and hash", () => {
    expect(safeReturnTo("/recipes?x=1#top")).toBe("/recipes?x=1#top");
  });

  it.each([null, "", "recipes", "//evil.example", "https://evil.example/x", "/\\evil.example", "/login"])(
    "falls back to / for %s",
    (value) => {
      expect(safeReturnTo(value)).toBe("/");
    },
  );
});

describe("loginPathFor", () => {
  it("encodes the original location", () => {
    expect(loginPathFor({ pathname: "/recipes", search: "?a=1", hash: "" })).toBe("/login?returnTo=%2Frecipes%3Fa%3D1");
    expect(loginPathFor({ pathname: "/", search: "", hash: "" })).toBe("/login");
  });
});
