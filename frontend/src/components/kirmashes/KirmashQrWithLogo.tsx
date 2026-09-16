'use client';

import { useEffect, useState } from 'react';
import QRCode from 'qrcode';

type Props = {
  url: string;
  /** Display size in CSS pixels (canvas is rendered sharper at higher DPI). */
  size?: number;
  className?: string;
};

/** Internal QR bitmap size — high enough to stay sharp after CSS rotate/scale to mm. */
export const KIRMA_QR_RENDER_SIZE = 512;

/**
 * Draw logo into QR center preserving aspect ratio, with high-quality downscale
 * so wide PNGs (e.g. 2k) do not look mushy at small sizes.
 */
export function drawLogoCentered(
  ctx: CanvasRenderingContext2D,
  logo: HTMLImageElement,
  canvasSize: number
) {
  const maxLogo = Math.round(canvasSize * 0.3);
  const natW = Math.max(1, logo.naturalWidth || logo.width || 1);
  const natH = Math.max(1, logo.naturalHeight || logo.height || 1);
  const aspect = natW / natH;
  let drawW: number;
  let drawH: number;
  if (aspect >= 1) {
    drawW = maxLogo;
    drawH = Math.max(1, Math.round(maxLogo / aspect));
  } else {
    drawH = maxLogo;
    drawW = Math.max(1, Math.round(maxLogo * aspect));
  }

  const pad = Math.max(4, Math.round(Math.max(drawW, drawH) * 0.1));
  const box = Math.max(drawW, drawH) + pad * 2;
  const boxX = (canvasSize - box) / 2;
  const boxY = (canvasSize - box) / 2;
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(boxX, boxY, box, box);

  const logoX = boxX + (box - drawW) / 2;
  const logoY = boxY + (box - drawH) / 2;

  const source = downscaleLogo(logo, drawW * 2, drawH * 2);
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(source, logoX, logoY, drawW, drawH);
}

/** Stepwise halve until near target — sharper than one big drawImage downscale. */
function downscaleLogo(
  logo: HTMLImageElement,
  targetW: number,
  targetH: number
): CanvasImageSource {
  let width = Math.max(1, logo.naturalWidth || logo.width || 1);
  let height = Math.max(1, logo.naturalHeight || logo.height || 1);
  if (width <= targetW * 1.5 && height <= targetH * 1.5) {
    return logo;
  }

  let src: HTMLCanvasElement | HTMLImageElement = logo;
  while (width > targetW * 1.5 || height > targetH * 1.5) {
    width = Math.max(targetW, Math.floor(width / 2));
    height = Math.max(targetH, Math.floor(height / 2));
    const step = document.createElement('canvas');
    step.width = width;
    step.height = height;
    const stepCtx = step.getContext('2d');
    if (!stepCtx) return src;
    stepCtx.imageSmoothingEnabled = true;
    stepCtx.imageSmoothingQuality = 'high';
    stepCtx.drawImage(src, 0, 0, width, height);
    src = step;
  }

  const out = document.createElement('canvas');
  out.width = Math.max(1, Math.round(targetW));
  out.height = Math.max(1, Math.round(targetH));
  const outCtx = out.getContext('2d');
  if (!outCtx) return src;
  outCtx.imageSmoothingEnabled = true;
  outCtx.imageSmoothingQuality = 'high';
  outCtx.drawImage(src, 0, 0, out.width, out.height);
  return out;
}

export async function renderKirmashQrDataUrl(
  url: string,
  options?: { size?: number }
): Promise<string> {
  const renderSize = options?.size ?? KIRMA_QR_RENDER_SIZE;
  const canvas = document.createElement('canvas');
  await QRCode.toCanvas(canvas, url, {
    errorCorrectionLevel: 'H',
    margin: 1,
    width: renderSize,
    color: { dark: '#111111', light: '#ffffff' },
  });

  const ctx = canvas.getContext('2d');
  if (ctx) {
    try {
      const logo = new Image();
      logo.crossOrigin = 'anonymous';
      await new Promise<void>((resolve, reject) => {
        logo.onload = () => resolve();
        logo.onerror = () => reject(new Error('logo load failed'));
        logo.src = '/kirma-logo.png';
      });
      drawLogoCentered(ctx, logo, renderSize);
    } catch {
      // keep QR without logo
    }
  }

  return canvas.toDataURL('image/png');
}

export default function KirmashQrWithLogo({
  url,
  size = 128,
  className,
}: Props) {
  const [dataUrl, setDataUrl] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    const render = async () => {
      try {
        const rendered = await renderKirmashQrDataUrl(url);
        if (!cancelled) setDataUrl(rendered);
      } catch {
        try {
          const plain = await QRCode.toDataURL(url, {
            errorCorrectionLevel: 'H',
            margin: 1,
            width: KIRMA_QR_RENDER_SIZE,
          });
          if (!cancelled) setDataUrl(plain);
        } catch {
          if (!cancelled) setDataUrl(null);
        }
      }
    };

    void render();
    return () => {
      cancelled = true;
    };
  }, [url]);

  if (!dataUrl) {
    return (
      <div
        className={className}
        style={{ width: size, height: size, background: '#f3f4f6' }}
      />
    );
  }

  return (
    // eslint-disable-next-line @next/next/no-img-element
    <img
      src={dataUrl}
      alt=""
      width={size}
      height={size}
      className={className}
      decoding="async"
      style={{
        aspectRatio: '1 / 1',
        objectFit: 'contain',
        imageRendering: 'auto',
      }}
    />
  );
}
