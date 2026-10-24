import { describe, expect, it } from "vitest";
import {
  emptyFilters,
  hasFilters,
  maxTagFilters,
  parseFilters,
  toListQuery,
  toSearchParams,
  toggleMaxMinutes,
  toggleTag,
} from "./filters";

const parse = (query: string) => parseFilters(new URLSearchParams(query));

describe("filters <-> URL", () => {
  it("empty URL means no filters, and no filters mean an empty URL", () => {
    expect(parse("")).toEqual(emptyFilters);
    expect(toSearchParams(emptyFilters).toString()).toBe("");
  });

  it("round-trips search, several tags and max minutes", () => {
    const filters = { search: "fish & chips", tagIds: ["a", "b"], maxMinutes: 30 };
    const query = toSearchParams(filters).toString();
    expect(query).toBe("q=fish+%26+chips&tag=a&tag=b&max=30");
    expect(parse(query)).toEqual(filters);
  });

  it("ignores malformed values instead of failing", () => {
    expect(parse("max=abc&tag=&q=")).toEqual(emptyFilters);
    expect(parse("max=-5").maxMinutes).toBeNull();
    expect(parse("max=0").maxMinutes).toBeNull();
    expect(parse("max=99999").maxMinutes).toBeNull();
    expect(parse("max=1.5").maxMinutes).toBeNull();
  });

  it("drops repeated tags and caps them at what the API accepts", () => {
    expect(parse("tag=a&tag=a&tag=b").tagIds).toEqual(["a", "b"]);
    const many = Array.from({ length: 15 }, (_, i) => `tag=t${i}`).join("&");
    expect(parse(many).tagIds).toHaveLength(maxTagFilters);
  });
});

describe("filter changes", () => {
  it("toggles a tag on and off, keeping the others (AND)", () => {
    const one = toggleTag(emptyFilters, "a");
    const two = toggleTag(one, "b");
    expect(two.tagIds).toEqual(["a", "b"]);
    expect(toggleTag(two, "a").tagIds).toEqual(["b"]);
  });

  it("does not select more tags than the API accepts", () => {
    let filters = emptyFilters;
    for (let i = 0; i < maxTagFilters + 3; i++) filters = toggleTag(filters, `t${i}`);
    expect(filters.tagIds).toHaveLength(maxTagFilters);
  });

  it("max time is one choice; choosing the same again clears it", () => {
    const thirty = toggleMaxMinutes(emptyFilters, 30);
    expect(thirty.maxMinutes).toBe(30);
    expect(toggleMaxMinutes(thirty, 60).maxMinutes).toBe(60);
    expect(toggleMaxMinutes(thirty, 30).maxMinutes).toBeNull();
  });

  it("knows when something is filtered", () => {
    expect(hasFilters(emptyFilters)).toBe(false);
    expect(hasFilters({ ...emptyFilters, search: "  " })).toBe(false);
    expect(hasFilters({ ...emptyFilters, search: "x" })).toBe(true);
    expect(hasFilters({ ...emptyFilters, tagIds: ["a"] })).toBe(true);
    expect(hasFilters({ ...emptyFilters, maxMinutes: 20 })).toBe(true);
  });
});

describe("list query", () => {
  it("leaves out empty filters and trims the search", () => {
    expect(toListQuery(emptyFilters, 12, 0)).toEqual({ limit: 12, offset: 0 });
    expect(toListQuery({ search: " pasta ", tagIds: ["a", "b"], maxMinutes: 30 }, 12, 24)).toEqual({
      search: "pasta",
      tagIds: ["a", "b"],
      maxMinutes: 30,
      limit: 12,
      offset: 24,
    });
  });
});
