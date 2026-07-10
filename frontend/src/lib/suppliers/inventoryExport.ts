import * as XLSX from 'xlsx';
import { calcGrossLineTotal, roundMoney } from '@/lib/suppliers/inventoryPricing';
import { formatInventoryLineName } from '@/lib/suppliers/inventoryTree';
import type { SupplierInventoryRow } from '@/types/supplier-inventory';

function formatLineName(row: SupplierInventoryRow): string {
  return formatInventoryLineName(row);
}

function grossLineTotal(row: SupplierInventoryRow, unpaidQuantity: number): number {
  return calcGrossLineTotal(
    row.supplierPrice,
    row.vatRatePercent,
    row.supplierIsVatPayer,
    unpaidQuantity
  );
}

function sanitizeFileNamePart(value: string): string {
  return value
    .trim()
    .replace(/[<>:"/\\|?*]/g, '')
    .replace(/\s+/g, '-')
    .slice(0, 80);
}

export function exportUnpaidSupplierInventoryToExcel(
  rows: SupplierInventoryRow[],
  options?: { supplierName?: string }
): { exported: number } {
  const unpaidRows = rows
    .filter((row) => row.quantityToPay > 0)
    .map((row) => {
      const unpaidQuantity = row.quantityToPay;
      return {
        name: formatLineName(row),
        netUnitPrice: roundMoney(row.supplierPrice),
        soldQuantity: unpaidQuantity,
        grossTotal: grossLineTotal(row, unpaidQuantity),
      };
    })
    .sort((a, b) => a.name.localeCompare(b.name, 'be'));

  const totalQuantity = unpaidRows.reduce((sum, row) => sum + row.soldQuantity, 0);
  const totalGross = roundMoney(unpaidRows.reduce((sum, row) => sum + row.grossTotal, 0));

  const sheetRows: (string | number)[][] = [
    ['Назва', 'Кошт нета адзінкі', 'Колькасць прададзенага', 'Сума брута'],
    ...unpaidRows.map((row) => [
      row.name,
      row.netUnitPrice,
      row.soldQuantity,
      row.grossTotal,
    ]),
    ['Усяго', '', totalQuantity, totalGross],
  ];

  const worksheet = XLSX.utils.aoa_to_sheet(sheetRows);
  worksheet['!cols'] = [{ wch: 48 }, { wch: 18 }, { wch: 22 }, { wch: 16 }];

  const workbook = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(workbook, worksheet, 'Не аплочана');

  const datePart = new Date().toISOString().slice(0, 10);
  const supplierPart = options?.supplierName
    ? sanitizeFileNamePart(options.supplierName)
    : 'пастаўшчыкі';
  const fileName = `інвентарызацыя-${supplierPart}-неаплочана-${datePart}.xlsx`;

  XLSX.writeFile(workbook, fileName);

  return { exported: unpaidRows.length };
}
