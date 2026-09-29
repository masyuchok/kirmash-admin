/** Export an already-rendered <img> if it is a data URL or CORS-clean. */
export function dataUrlFromImgElement(
  img: HTMLImageElement | null | undefined
): string | null {
  if (!img?.src) return null;
  if (img.src.startsWith('data:')) return img.src;
  try {
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth || img.width;
    canvas.height = img.naturalHeight || img.height;
    if (canvas.width < 1 || canvas.height < 1) return null;
    const ctx = canvas.getContext('2d');
    if (!ctx) return null;
    ctx.drawImage(img, 0, 0);
    return canvas.toDataURL('image/jpeg', 0.92);
  } catch {
    return null;
  }
}
