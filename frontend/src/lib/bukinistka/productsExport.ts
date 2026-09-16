import * as XLSX from 'xlsx';
import type { BukinistkaProduct } from '@/lib/api/bukinistka-products';

function sanitizeFileNamePart(value: string): string {
  return value
    .trim()
    .replace(/[<>:"/\\|?*]/g, '')
    .replace(/\s+/g, '-')
    .slice(0, 80);
}

export function exportBukinistkaProductsToExcel(
  rows: BukinistkaProduct[],
  options?: { publisherFilterLabel?: string }
): { exported: number } {
  const sheetRows: (string | number)[][] = [
    ['Назва', 'Аўтар', 'Выдавец', 'Колькасць', 'Цана'],
    ...rows.map((row) => [
      row.name,
      row.authorName?.trim() || '',
      row.supplierName?.trim() || '',
      Number.isFinite(row.quantityInStock) ? row.quantityInStock : 0,
      Number.isFinite(row.listPrice) ? row.listPrice : 0,
    ]),
  ];

  const worksheet = XLSX.utils.aoa_to_sheet(sheetRows);
  worksheet['!cols'] = [
    { wch: 48 },
    { wch: 28 },
    { wch: 28 },
    { wch: 12 },
    { wch: 12 },
  ];

  const workbook = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(workbook, worksheet, 'Прадукты');

  const datePart = new Date().toISOString().slice(0, 10);
  const filterPart = options?.publisherFilterLabel
    ? sanitizeFileNamePart(options.publisherFilterLabel)
    : 'усе';
  const fileName = `bukinistka-produkty-${filterPart}-${datePart}.xlsx`;

  XLSX.writeFile(workbook, fileName);

  return { exported: rows.length };
}
