'use client';

import type { KirmashPriceTag } from '@/lib/api/kirmashes';
import KirmashQrWithLogo from '@/components/kirmashes/KirmashQrWithLogo';

type Props = {
  tags: KirmashPriceTag[];
  kirmashTitle: string;
};

/** A4 portrait, 2 columns × 7 rows. */
const PAGE_W_MM = 210;
const PAGE_H_MM = 297;
const COLS = 2;
const ROWS = 7;
const CELL_W_MM = PAGE_W_MM / COLS;
const CELL_H_MM = PAGE_H_MM / ROWS;
const TAGS_PER_PAGE = COLS * ROWS;

/**
 * Tag content stays inside the cell (no pre-transform overflow into next rows).
 * LTR on the sheet: price | QR | title (vertical text, readable top→bottom).
 */
const QR_MM = 30;
const PAD_X_MM = 2.5;
const PAD_Y_MM = 1.5;
const GAP_MM = 1.2;

export type KirmashPrintQrAssets = {
  qrSvgByTagId: Record<number, string>;
  logoDataUrl: string;
};

function formatPriceZl(value: number): string {
  const rounded = Math.round(value);
  if (Math.abs(value - rounded) < 0.001) {
    return `${rounded} зл`;
  }
  return `${value.toFixed(2).replace('.', ',')} зл`;
}

function chunkTags<T>(items: T[], size: number): T[][] {
  const pages: T[][] = [];
  for (let i = 0; i < items.length; i += size) {
    pages.push(items.slice(i, i + size));
  }
  return pages.length > 0 ? pages : [[]];
}

function escapeHtml(value: string): string {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

function normalizeQrSvg(svg: string): string {
  return svg
    .replace(/<\?xml[^>]*>/i, '')
    .replace(/<!DOCTYPE[^>]*>/i, '')
    .trim()
    .replace(
      /<svg\b([^>]*)>/i,
      '<svg$1 width="100%" height="100%" preserveAspectRatio="xMidYMid meet">'
    );
}

function tagInnerHtml(tag: KirmashPriceTag, qrSvg: string): string {
  return `
    <div class="price">${escapeHtml(formatPriceZl(tag.unitPrice))}</div>
    <div class="qr-wrap">
      <div class="qr-frame">
        ${qrSvg || '<div class="qr-fallback"></div>'}
        <div class="qr-logo" aria-hidden="true"></div>
      </div>
    </div>
    <div class="title-wrap">
      <div class="title">${escapeHtml(tag.title)}</div>
    </div>`;
}

export function buildKirmashPriceTagsPrintHtml(
  tags: KirmashPriceTag[],
  kirmashTitle: string,
  assets: KirmashPrintQrAssets
): string {
  const pages = chunkTags(tags, TAGS_PER_PAGE);
  const pageHtml = pages
    .map((pageTags) => {
      const cells = Array.from({ length: TAGS_PER_PAGE }, (_, index) => {
        const tag = pageTags[index];
        if (!tag) {
          return `<div class="cell empty"></div>`;
        }
        const qrSvg =
          assets.qrSvgByTagId[tag.id] ??
          assets.qrSvgByTagId[Number(tag.id)] ??
          '';
        return `
          <div class="cell">
            <div class="tag">${tagInnerHtml(tag, qrSvg)}</div>
          </div>`;
      }).join('');
      return `<section class="page">${cells}</section>`;
    })
    .join('');

  const logoCss = assets.logoDataUrl
    ? `background-image: url("${assets.logoDataUrl}");`
    : '';

  return `<!DOCTYPE html>
<html lang="be">
<head>
  <meta charset="utf-8" />
  <title>Цэннікі — ${escapeHtml(kirmashTitle)}</title>
  <style>
    @page { size: A4 portrait; margin: 0; }
    * { box-sizing: border-box; }
    html, body {
      margin: 0;
      padding: 0;
      background: #fff;
      color: #111;
      font-family: Arial, Helvetica, sans-serif;
      -webkit-print-color-adjust: exact;
      print-color-adjust: exact;
    }
    .page {
      width: ${PAGE_W_MM}mm;
      height: ${PAGE_H_MM}mm;
      display: grid;
      grid-template-columns: repeat(${COLS}, ${CELL_W_MM}mm);
      grid-template-rows: repeat(${ROWS}, ${CELL_H_MM}mm);
      page-break-after: always;
      break-after: page;
      overflow: hidden;
    }
    .page:last-child {
      page-break-after: auto;
      break-after: auto;
    }
    .cell {
      position: relative;
      width: ${CELL_W_MM}mm;
      height: ${CELL_H_MM}mm;
      overflow: hidden;
      border: 0.35pt solid #111;
    }
    .cell.empty { border-color: #ccc; }
    .tag {
      position: absolute;
      inset: 0;
      display: flex;
      flex-direction: row;
      align-items: center;
      justify-content: flex-start;
      gap: ${GAP_MM}mm;
      padding: ${PAD_Y_MM}mm ${PAD_X_MM}mm;
      overflow: hidden;
    }
    /* vertical-rl packs lines to the right of a wide box — wrap keeps title next to QR. */
    .title-wrap {
      flex: 1 1 auto;
      min-width: 0;
      height: 100%;
      display: flex;
      justify-content: flex-start;
      align-items: stretch;
      overflow: hidden;
    }
    .title {
      flex: 0 1 auto;
      max-width: 100%;
      height: 100%;
      overflow: hidden;
      writing-mode: vertical-rl;
      font-size: 7.5pt;
      font-weight: 600;
      line-height: 1.1;
      text-align: center;
      word-break: break-word;
    }
    .qr-wrap {
      flex: 0 0 ${QR_MM}mm;
      width: ${QR_MM}mm;
      height: ${QR_MM}mm;
      display: flex;
      align-items: center;
      justify-content: center;
    }
    .qr-frame {
      position: relative;
      width: ${QR_MM}mm;
      height: ${QR_MM}mm;
      /* Keep QR/logo upright on the sheet; only text uses writing-mode. */
      transform: none;
    }
    .qr-frame svg {
      width: 100%;
      height: 100%;
      display: block;
    }
    .qr-fallback {
      width: 100%;
      height: 100%;
      background: #f3f4f6;
    }
    .qr-logo {
      position: absolute;
      left: 50%;
      top: 50%;
      width: 30%;
      aspect-ratio: 2.2 / 1;
      transform: translate(-50%, -50%);
      background-color: #fff;
      background-repeat: no-repeat;
      background-position: center;
      background-size: contain;
      ${logoCss}
      -webkit-print-color-adjust: exact;
      print-color-adjust: exact;
    }
    .price {
      flex: 0 0 auto;
      max-width: 9mm;
      height: 100%;
      writing-mode: vertical-rl;
      font-size: 11pt;
      font-weight: 700;
      text-align: center;
      white-space: nowrap;
      line-height: 1.1;
    }
  </style>
</head>
<body>${pageHtml}</body>
</html>`;
}

async function loadSharedLogoDataUrl(): Promise<string> {
  const logo = new Image();
  logo.crossOrigin = 'anonymous';
  await new Promise<void>((resolve, reject) => {
    logo.onload = () => resolve();
    logo.onerror = () => reject(new Error('logo'));
    logo.src = '/kirma-logo.png';
  });

  const maxW = 240;
  const natW = Math.max(1, logo.naturalWidth || logo.width);
  const natH = Math.max(1, logo.naturalHeight || logo.height);
  const scale = Math.min(1, maxW / natW);
  const w = Math.max(1, Math.round(natW * scale));
  const h = Math.max(1, Math.round(natH * scale));
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (!ctx) return '';
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'high';
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(0, 0, w, h);
  ctx.drawImage(logo, 0, 0, w, h);
  return canvas.toDataURL('image/png');
}

export async function collectKirmashPrintQrAssets(
  tags: KirmashPriceTag[]
): Promise<KirmashPrintQrAssets> {
  const QRCode = (await import('qrcode')).default;
  const qrSvgByTagId: Record<number, string> = {};

  let logoDataUrl = '';
  try {
    logoDataUrl = await loadSharedLogoDataUrl();
  } catch {
    logoDataUrl = '';
  }

  const svgByUrl = new Map<string, string>();
  for (const tag of tags) {
    const url = tag.checkoutUrl;
    let svg = svgByUrl.get(url);
    if (!svg) {
      try {
        const raw = await QRCode.toString(url, {
          type: 'svg',
          errorCorrectionLevel: 'H',
          margin: 1,
          color: { dark: '#111111', light: '#ffffff' },
        });
        svg = normalizeQrSvg(raw);
      } catch {
        svg = '';
      }
      svgByUrl.set(url, svg);
    }
    qrSvgByTagId[tag.id] = svg;
  }

  return { qrSvgByTagId, logoDataUrl };
}

export default function KirmashPriceTagsPreview({ tags, kirmashTitle }: Props) {
  const pages = chunkTags(tags, TAGS_PER_PAGE);

  return (
    <div className="space-y-4">
      <div className="text-sm text-gray-600">
        {kirmashTitle}: {tags.length} цэннікаў · {pages.length} арк. A4 (2×7)
      </div>
      <div className="space-y-6 overflow-x-auto">
        {pages.map((pageTags, pageIndex) => (
          <div
            key={`page-${pageIndex}`}
            className="mx-auto bg-white shadow-sm"
            style={{
              width: `${PAGE_W_MM}mm`,
              height: `${PAGE_H_MM}mm`,
              display: 'grid',
              gridTemplateColumns: `repeat(${COLS}, ${CELL_W_MM}mm)`,
              gridTemplateRows: `repeat(${ROWS}, ${CELL_H_MM}mm)`,
            }}
          >
            {Array.from({ length: TAGS_PER_PAGE }, (_, index) => {
              const tag = pageTags[index];
              if (!tag) {
                return (
                  <div
                    key={`empty-${pageIndex}-${index}`}
                    style={{
                      width: `${CELL_W_MM}mm`,
                      height: `${CELL_H_MM}mm`,
                      border: '0.35pt solid #e5e7eb',
                    }}
                  />
                );
              }
              return (
                <div
                  key={tag.id}
                  className="relative overflow-hidden"
                  style={{
                    width: `${CELL_W_MM}mm`,
                    height: `${CELL_H_MM}mm`,
                    border: '0.35pt solid #111',
                  }}
                >
                  <div
                    className="absolute inset-0 flex flex-row items-center justify-start overflow-hidden"
                    style={{
                      gap: `${GAP_MM}mm`,
                      padding: `${PAD_Y_MM}mm ${PAD_X_MM}mm`,
                    }}
                  >
                    <div
                      className="h-full shrink-0 whitespace-nowrap text-center text-[11pt] font-bold leading-tight text-gray-900"
                      style={{
                        writingMode: 'vertical-rl',
                        maxWidth: '9mm',
                      }}
                    >
                      {formatPriceZl(tag.unitPrice)}
                    </div>
                    <div
                      className="flex shrink-0 items-center justify-center"
                      style={{ width: `${QR_MM}mm`, height: `${QR_MM}mm` }}
                    >
                      <KirmashQrWithLogo
                        url={tag.checkoutUrl}
                        size={160}
                        className="block size-full object-contain"
                      />
                    </div>
                    <div className="flex h-full min-w-0 flex-1 items-stretch justify-start overflow-hidden">
                      <div
                        className="h-full max-w-full overflow-hidden text-center text-[7.5pt] font-semibold leading-tight text-gray-900 [overflow-wrap:anywhere]"
                        style={{ writingMode: 'vertical-rl' }}
                      >
                        {tag.title}
                      </div>
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        ))}
      </div>
    </div>
  );
}

export function printKirmashPriceTagsHtml(html: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const iframe = document.createElement('iframe');
    iframe.setAttribute(
      'style',
      [
        'position:fixed',
        'left:-10000px',
        'top:0',
        'width:210mm',
        'height:297mm',
        'border:0',
        'opacity:0',
        'pointer-events:none',
      ].join(';')
    );
    document.body.appendChild(iframe);

    let settled = false;
    const cleanup = () => {
      window.setTimeout(() => iframe.remove(), 2000);
    };

    const doc = iframe.contentDocument || iframe.contentWindow?.document;
    const win = iframe.contentWindow;
    if (!doc || !win) {
      iframe.remove();
      reject(new Error('Не ўдалося адкрыць акно друку.'));
      return;
    }

    const runPrint = async () => {
      if (settled) return;
      settled = true;
      try {
        await new Promise<void>((res) => window.setTimeout(res, 120));
        await new Promise<void>((res) => {
          window.requestAnimationFrame(() => {
            window.requestAnimationFrame(() => res());
          });
        });
        win.focus();
        win.print();
        cleanup();
        resolve();
      } catch (err) {
        cleanup();
        reject(err instanceof Error ? err : new Error('Памылка друку'));
      }
    };

    doc.open();
    doc.write(html);
    doc.close();

    iframe.onload = () => {
      void runPrint();
    };
    window.setTimeout(() => {
      void runPrint();
    }, 50);
  });
}
