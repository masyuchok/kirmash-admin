import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { SupplyListItem } from '@/types/supply';

type Props = {
  supplies: SupplyListItem[];
  sortDirection: 'asc' | 'desc';
  onToggleDateSort: () => void;
  supplierFilters: string[];
  selectedSuppliers: string[];
  onToggleSupplierFilter: (name: string) => void;
  onOpenSupply: (supply: SupplyListItem) => void;
};

function formatSupplyDate(iso: string): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString('be-BY', {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
  });
}

export default function SuppliesTable({
  supplies,
  sortDirection,
  onToggleDateSort,
  supplierFilters,
  selectedSuppliers,
  onToggleSupplierFilter,
  onOpenSupply,
}: Props) {
  const [supplierMenuOpen, setSupplierMenuOpen] = useState(false);
  const [menuMounted, setMenuMounted] = useState(false);
  const [menuPosition, setMenuPosition] = useState({ top: 0, left: 0 });
  const supplierTriggerRef = useRef<HTMLButtonElement | null>(null);
  const supplierMenuRef = useRef<HTMLDivElement | null>(null);

  const updateMenuPosition = () => {
    if (!supplierTriggerRef.current) return;
    const rect = supplierTriggerRef.current.getBoundingClientRect();
    setMenuPosition({
      top: rect.bottom + 8,
      left: rect.left,
    });
  };

  useEffect(() => {
    setMenuMounted(true);
  }, []);

  useEffect(() => {
    if (!supplierMenuOpen) return;
    updateMenuPosition();

    const onDocClick = (event: MouseEvent) => {
      const target = event.target as Node;
      const clickedMenu = supplierMenuRef.current?.contains(target);
      const clickedTrigger = supplierTriggerRef.current?.contains(target);
      if (!clickedMenu && !clickedTrigger) {
        setSupplierMenuOpen(false);
      }
    };
    const onViewportChange = () => updateMenuPosition();

    document.addEventListener('mousedown', onDocClick);
    window.addEventListener('resize', onViewportChange);
    window.addEventListener('scroll', onViewportChange, true);
    return () => {
      document.removeEventListener('mousedown', onDocClick);
      window.removeEventListener('resize', onViewportChange);
      window.removeEventListener('scroll', onViewportChange, true);
    };
  }, [supplierMenuOpen]);

  if (supplies.length === 0) {
    return (
      <div className="px-6 py-16 text-center">
        <p className="text-sm font-medium text-gray-900">Паставак пакуль няма</p>
        <p className="mt-1 text-sm text-gray-500">Дадайце пастаўку ў сістэме, каб яна з’явілася ў спісе.</p>
      </div>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="min-w-full border-collapse text-left text-sm">
        <thead>
          <tr className="border-b border-gray-200 bg-gray-50/90 text-xs font-semibold uppercase tracking-wide text-gray-500 backdrop-blur-sm">
            <th className="whitespace-nowrap px-6 py-3.5">
              <div className="inline-flex items-center gap-1">
                <span>Пастаўшчык</span>
                <button
                  type="button"
                  ref={supplierTriggerRef}
                  className="inline-flex items-center rounded p-0.5 text-gray-500 transition hover:bg-gray-100 hover:text-gray-700"
                  aria-label="Фільтр пастаўшчыкоў"
                  onClick={() => setSupplierMenuOpen((prev) => !prev)}
                >
                  <span aria-hidden>{supplierMenuOpen ? '▴' : '▾'}</span>
                </button>
              </div>
            </th>
            <th className="whitespace-nowrap px-4 py-3.5">
              <button
                type="button"
                onClick={onToggleDateSort}
                className="inline-flex items-center gap-1 rounded text-xs font-semibold uppercase tracking-wide text-gray-500 transition hover:text-gray-700"
                aria-label="Сартаваць па даце"
              >
                Дата
                <span aria-hidden>{sortDirection === 'asc' ? '↑' : '↓'}</span>
              </button>
            </th>
            <th className="whitespace-nowrap px-6 py-3.5 text-right tabular-nums">Тавары</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100 bg-white">
          {supplies.map((row) => (
            <tr
              key={row.id}
              className="cursor-pointer transition-colors hover:bg-primary/10"
              onClick={() => onOpenSupply(row)}
            >
              <td className="whitespace-nowrap px-6 py-3.5 font-medium text-gray-900">
                {row.supplierName}
              </td>
              <td className="whitespace-nowrap px-4 py-3.5 text-gray-600">{formatSupplyDate(row.date)}</td>
              <td className="px-6 py-3.5 text-right tabular-nums text-gray-700">{row.booksNumber}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {menuMounted &&
        supplierMenuOpen &&
        createPortal(
          <div
            ref={supplierMenuRef}
            className="fixed z-[70] w-64 rounded-lg border border-gray-200 bg-white p-3 shadow-lg"
            style={{ top: `${menuPosition.top}px`, left: `${menuPosition.left}px` }}
          >
            <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500">
              Фільтр пастаўшчыкоў
            </p>
            <div className="max-h-56 space-y-2 overflow-auto pr-1">
              {supplierFilters.length === 0 ? (
                <p className="text-xs text-gray-500">Няма пастаўшчыкоў</p>
              ) : (
                supplierFilters.map((name) => (
                  <label
                    key={name}
                    className="flex items-center gap-2 text-sm font-normal normal-case text-gray-700"
                  >
                    <input
                      type="checkbox"
                      className="size-4 rounded border-gray-300 accent-primary focus:ring-primary"
                      checked={selectedSuppliers.includes(name)}
                      onChange={() => onToggleSupplierFilter(name)}
                    />
                    <span className="truncate" title={name}>
                      {name}
                    </span>
                  </label>
                ))
              )}
            </div>
          </div>,
          document.body
        )}
    </div>
  );
}
