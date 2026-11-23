/** Longest side sent to the server (the server sends at most 2000 px to Claude and never relies on this, T-118). */
export const maxPhotoSide = 2000;
/** The server's limit per file (`Images:MaxUploadBytes`); an undecodable photo over it cannot be sent. */
export const maxPhotoBytes = 10 * 1024 * 1024;
const jpegQuality = 0.85;

function toBlob(canvas: HTMLCanvasElement): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", jpegQuality));
}

/** The photo as it is when the browser cannot decode it: sent as is when it fits the server's limit, else nothing. */
const asIs = (file: File): File | null => (file.size <= maxPhotoBytes ? file : null);

/**
 * Shrinks a photo to a JPEG with the longest side at most 2000 px before upload (T-119), so a 12 MP phone photo is a
 * few hundred KB on a mobile connection. The browser applies the EXIF orientation while decoding. A file the browser
 * cannot decode (HEIC on most desktops, a damaged file) is returned unchanged when it is under 10 MB, else `null`:
 * the caller names the file in a message. Reusable for any "photos in" screen (T-132).
 */
export async function downsizePhoto(file: File): Promise<File | null> {
  if (typeof createImageBitmap !== "function") return asIs(file);
  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file);
  } catch {
    return asIs(file);
  }
  try {
    const scale = Math.min(1, maxPhotoSide / Math.max(bitmap.width, bitmap.height));
    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext("2d");
    if (!context) return asIs(file);
    context.fillStyle = "#fff"; // a transparent PNG becomes white, not black, in a JPEG
    context.fillRect(0, 0, width, height);
    context.drawImage(bitmap, 0, 0, width, height);
    const blob = await toBlob(canvas);
    if (!blob) return asIs(file);
    return new File([blob], file.name.replace(/\.[^.]*$/, "") + ".jpg", { type: "image/jpeg" });
  } catch {
    return asIs(file);
  } finally {
    bitmap.close?.();
  }
}
