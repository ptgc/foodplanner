import { crc32, deflateSync } from "node:zlib";

function encodePng(width: number, height: number, rowAt: (y: number) => Buffer): Buffer {
  const chunk = (type: string, data: Buffer) => {
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const out = Buffer.alloc(body.length + 8);
    out.writeUInt32BE(data.length, 0);
    body.copy(out, 4);
    out.writeUInt32BE(crc32(body), body.length + 4);
    return out;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8); // 8-bit RGB
  const raw = Buffer.concat(Array.from({ length: height }, (_, y) => Buffer.concat([Buffer.from([0]), rowAt(y)])));
  const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  return Buffer.concat([signature, chunk("IHDR", header), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}

/** A valid 64x48 orange PNG built in code (no binary fixture); the API resizes it to the stored sizes. */
export function solidPng(): Buffer {
  const width = 64;
  const row = Buffer.from(Array.from({ length: width }, () => [255, 140, 60]).flat());
  return encodePng(width, 48, () => row);
}

/**
 * A valid square PNG of pixel noise that does not compress, so the file is about `bytes` big like a phone photo
 * (T-080) without a binary fixture. The noise is seeded, so every run sends the same bytes.
 */
export function noisyPng(bytes: number): Buffer {
  const side = Math.ceil(Math.sqrt(bytes / 3));
  let state = 12345;
  return encodePng(side, side, () => {
    const row = Buffer.alloc(side * 3);
    for (let i = 0; i < row.length; i++) {
      state = (Math.imul(state, 1103515245) + 12345) >>> 0;
      row[i] = state >>> 16;
    }
    return row;
  });
}
