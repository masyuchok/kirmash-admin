/** Compress phone photos to small JPEG before upload (avoids 413 / HEIC issues). */
export async function compressImageForUpload(
  file: File,
  options?: { maxEdge?: number; quality?: number; maxBytes?: number }
): Promise<File> {
  const maxEdge = options?.maxEdge ?? 1280;
  const quality = options?.quality ?? 0.72;
  const maxBytes = options?.maxBytes ?? 550_000;

  const bitmap = await loadBitmap(file);
  try {
    const srcW = bitmap.width;
    const srcH = bitmap.height;
    let edge = Math.min(maxEdge, Math.max(srcW, srcH));
    let q = quality;
    let blob: Blob | null = null;

    for (let attempt = 0; attempt < 8; attempt++) {
      const scale = edge / Math.max(srcW, srcH);
      const width = Math.max(1, Math.round(srcW * scale));
      const height = Math.max(1, Math.round(srcH * scale));

      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      const ctx = canvas.getContext('2d');
      if (!ctx) {
        throw new Error('Не ўдалося апрацаваць фота ў браўзеры.');
      }
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, width, height);
      ctx.drawImage(bitmap, 0, 0, width, height);

      blob = await new Promise<Blob | null>((resolve) =>
        canvas.toBlob((b) => resolve(b), 'image/jpeg', q)
      );

      if (blob && blob.size <= maxBytes) {
        break;
      }

      if (attempt % 2 === 0) {
        q = Math.max(0.4, q - 0.08);
      } else {
        edge = Math.max(720, Math.round(edge * 0.82));
      }
    }

    if (!blob || blob.size > maxBytes * 1.2) {
      throw new Error(
        'Фота занадта вялікае нават пасля сціскання. Зрабіце здымак бліжэй або захавайце як JPG.'
      );
    }

    const baseName = file.name.replace(/\.[^.]+$/, '') || 'photo';
    return new File([blob], `${baseName}.jpg`, {
      type: 'image/jpeg',
      lastModified: Date.now(),
    });
  } finally {
    bitmap.close();
  }
}

async function loadBitmap(file: File): Promise<ImageBitmap> {
  try {
    return await createImageBitmap(file);
  } catch {
    const url = URL.createObjectURL(file);
    try {
      const img = await loadHtmlImage(url);
      return await createImageBitmap(img);
    } finally {
      URL.revokeObjectURL(url);
    }
  }
}

function loadHtmlImage(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () =>
      reject(
        new Error(
          'Не ўдалося прачытаць фота з тэлефона. Захавайце як JPG/PNG і паспрабуйце зноў.'
        )
      );
    img.src = url;
  });
}
