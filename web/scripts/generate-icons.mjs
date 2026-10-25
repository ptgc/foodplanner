// Generates the placeholder PWA icons into public/icons (no external assets, no dependencies): a green tile with a
// white plate (a ring around a disc), the idea of "a meal". Run `node scripts/generate-icons.mjs` and commit the PNGs;
// the build does not run it. `maskable` keeps the plate inside the 80% safe zone and fills the whole square.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { deflateSync } from "node:zlib";

const out = resolve(dirname(fileURLToPath(import.meta.url)), "..", "public", "icons");
mkdirSync(out, { recursive: true });

const green = [0x1f, 0x6f, 0x43];
const white = [0xff, 0xff, 0xff];
const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}
function png(size, pixel) {
  const stride = size * 3 + 1;
  const raw = Buffer.alloc(size * stride);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) raw.set(pixel(x, y), y * stride + 1 + x * 3);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0);
  header.writeUInt32BE(size, 4);
  header.set([8, 2, 0, 0, 0], 8); // 8-bit RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", header),
    chunk("IDAT", deflateSync(raw)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

/** `scale` is the plate's outer radius as a share of half the width. `rounded` cuts the corners (non-maskable icons). */
function icon(size, scale, rounded) {
  const c = (size - 1) / 2;
  const outer = c * scale;
  const ringInner = outer * 0.8;
  const disc = outer * 0.62;
  const corner = size * 0.18;
  return png(size, (x, y) => {
    if (rounded) {
      const dx = Math.max(Math.abs(x - c) - (c - corner), 0);
      const dy = Math.max(Math.abs(y - c) - (c - corner), 0);
      if (Math.hypot(dx, dy) > corner) return white; // outside the rounded tile
    }
    const d = Math.hypot(x - c, y - c);
    if (d <= disc) return white;
    if (d >= ringInner && d <= outer) return white;
    return green;
  });
}

const files = {
  "icon-192.png": icon(192, 0.72, true),
  "icon-512.png": icon(512, 0.72, true),
  "maskable-512.png": icon(512, 0.6, false),
  "apple-touch-icon.png": icon(180, 0.72, false), // iOS rounds the corners itself
};
for (const [name, data] of Object.entries(files)) writeFileSync(join(out, name), data);
console.log(`wrote ${Object.keys(files).join(", ")} to ${out}`);
