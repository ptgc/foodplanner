import { afterEach, describe, expect, it, vi } from "vitest";
import { downsizePhoto, maxPhotoBytes, maxPhotoSide } from "./downsize";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** A canvas that records its size and the draw call, and encodes a small "JPEG". */
function fakeCanvas() {
  const drawn: { width: number; height: number }[] = [];
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockImplementation((() => ({
    fillStyle: "",
    fillRect: () => undefined,
    drawImage: (_bitmap: unknown, _x: number, _y: number, width: number, height: number) => drawn.push({ width, height }),
  })) as never);
  vi.spyOn(HTMLCanvasElement.prototype, "toBlob").mockImplementation(((callback: BlobCallback, type?: string) => {
    callback(new Blob(["jpeg"], { type }));
  }) as never);
  return drawn;
}

const photo = (name = "IMG_1.PNG", type = "image/png", size = 4) => {
  const file = new File(["x".repeat(Math.min(size, 16))], name, { type });
  if (size > 16) Object.defineProperty(file, "size", { value: size });
  return file;
};

describe("downsizePhoto", () => {
  it("shrinks a large photo to at most 2000 px on the longest side and sends a JPEG", async () => {
    const drawn = fakeCanvas();
    const close = vi.fn();
    vi.stubGlobal("createImageBitmap", vi.fn(async () => ({ width: 4000, height: 3000, close })));
    const result = await downsizePhoto(photo());
    expect(drawn).toEqual([{ width: maxPhotoSide, height: 1500 }]);
    expect(result?.type).toBe("image/jpeg");
    expect(result?.name).toBe("IMG_1.jpg");
    expect(close).toHaveBeenCalled();
  });

  it("does not enlarge a small photo", async () => {
    const drawn = fakeCanvas();
    vi.stubGlobal("createImageBitmap", vi.fn(async () => ({ width: 800, height: 600, close: vi.fn() })));
    await downsizePhoto(photo());
    expect(drawn).toEqual([{ width: 800, height: 600 }]);
  });

  it("sends a photo the browser cannot decode as it is when it is under 10 MB", async () => {
    vi.stubGlobal("createImageBitmap", vi.fn(async () => Promise.reject(new Error("cannot decode"))));
    const file = photo("IMG.heic", "image/heic");
    expect(await downsizePhoto(file)).toBe(file);
  });

  it("gives nothing back for an undecodable photo over 10 MB, so the screen can name it", async () => {
    vi.stubGlobal("createImageBitmap", vi.fn(async () => Promise.reject(new Error("cannot decode"))));
    expect(await downsizePhoto(photo("big.heic", "image/heic", maxPhotoBytes + 1))).toBeNull();
  });

  it("falls back to the file when the browser has no createImageBitmap", async () => {
    vi.stubGlobal("createImageBitmap", undefined);
    const file = photo();
    expect(await downsizePhoto(file)).toBe(file);
  });
});
