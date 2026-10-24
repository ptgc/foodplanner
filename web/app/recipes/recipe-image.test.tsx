import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { RecipeImage, cardWidth, initialOf, thumbWidth, tileColor } from "./recipe-image";

const thumb = "https://img.test/r1/thumb.webp";
const card = "https://img.test/r1/card.webp";

describe("RecipeImage placeholder (no urls yet)", () => {
  it("is a decorative coloured tile with the initial, and no <img>", () => {
    const { container } = render(<RecipeImage title="pannekaker" />);
    const tile = container.querySelector(".recipe-image-placeholder");
    expect(tile).not.toBeNull();
    expect(tile).toHaveAttribute("aria-hidden", "true");
    expect(tile).toHaveTextContent("P");
    expect(container.querySelector("img")).toBeNull();
  });

  it("gives the same title the same colour, and handles empty or unusual titles", () => {
    expect(tileColor("Taco")).toBe(tileColor("Taco"));
    expect(initialOf("  øl")).toBe("Ø");
    expect(initialOf("")).toBe("?");
    expect(initialOf("🍕 pizza")).toBe("🍕");
  });

  it("falls back to the placeholder when the image fails to load", () => {
    const { container } = render(<RecipeImage title="Taco" thumbUrl={thumb} cardUrl={card} />);
    fireEvent.error(container.querySelector("img")!);
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector(".recipe-image-placeholder")).not.toBeNull();
  });
});

describe("RecipeImage with urls", () => {
  it("lazy-loads, decodes async, reserves its size and offers thumb and card in srcset", () => {
    const { container } = render(<RecipeImage title="Taco" thumbUrl={thumb} cardUrl={card} />);
    const img = container.querySelector("img")!;
    expect(img).toHaveAttribute("src", card);
    expect(img).toHaveAttribute("srcset", `${thumb} ${thumbWidth}w, ${card} ${cardWidth}w`);
    expect(img).toHaveAttribute("sizes");
    expect(img).toHaveAttribute("loading", "lazy");
    expect(img).toHaveAttribute("decoding", "async");
    expect(img).toHaveAttribute("width", String(cardWidth));
    expect(img).toHaveAttribute("height", String((cardWidth * 3) / 4));
    expect(img).toHaveAttribute("alt", "");
    expect(container.querySelector(".recipe-image-placeholder")).toBeNull();
  });

  it("with only one url uses it as src without a srcset", () => {
    const { container } = render(<RecipeImage title="Taco" thumbUrl={thumb} />);
    const img = container.querySelector("img")!;
    expect(img).toHaveAttribute("src", thumb);
    expect(img).not.toHaveAttribute("srcset");
    expect(img).not.toHaveAttribute("sizes");
  });
});
