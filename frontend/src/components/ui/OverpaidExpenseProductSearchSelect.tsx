'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { FiChevronDown, FiX } from 'react-icons/fi';
import type { VatReportOverpaidExpenseProductOption } from '@/types/report-details';

type Props = {
  options: VatReportOverpaidExpenseProductOption[];
  value: string;
  onChange: (expenseProductId: string) => void;
  formatDate: (value: string) => string;
  placeholder?: string;
  emptyMessage?: string;
};

function buildProductTitle(option: VatReportOverpaidExpenseProductOption): string {
  const variantName = option.shopifyVariantTitle?.trim() ?? '';
  return variantName ? `${option.productTitle} — ${variantName}` : option.productTitle;
}

function buildOptionLabel(
  option: VatReportOverpaidExpenseProductOption,
  formatDate: (value: string) => string
): string {
  return [
    formatDate(option.expenseDateUtc),
    option.invoiceNumber || option.comment || `Фактура #${option.expenseId}`,
    buildProductTitle(option),
    `×${option.quantity}`,
    `(пераплата ${option.overpaidQuantity})`,
  ].join(' · ');
}

export default function OverpaidExpenseProductSearchSelect({
  options,
  value,
  onChange,
  formatDate,
  placeholder = 'Пошук па назве тавару...',
  emptyMessage = 'Тавары не знойдзены',
}: Props) {
  const rootRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');

  const selected = useMemo(
    () => options.find((option) => String(option.expenseProductId) === value) ?? null,
    [options, value]
  );

  const sortedOptions = useMemo(
    () =>
      [...options].sort((a, b) =>
        buildProductTitle(a).localeCompare(buildProductTitle(b), 'be')
      ),
    [options]
  );

  const filtered = useMemo(() => {
    const search = query.trim().toLowerCase();
    if (!search) return sortedOptions;
    return sortedOptions.filter((option) => buildProductTitle(option).toLowerCase().includes(search));
  }, [sortedOptions, query]);

  const selectedLabel = selected ? buildOptionLabel(selected, formatDate) : '';
  const displayValue = open ? query : selectedLabel;

  useEffect(() => {
    if (!open) return;
    const onDocMouseDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
        setOpen(false);
        setQuery(selectedLabel);
      }
    };
    document.addEventListener('mousedown', onDocMouseDown);
    return () => document.removeEventListener('mousedown', onDocMouseDown);
  }, [open, selectedLabel]);

  useEffect(() => {
    if (!value) {
      setQuery('');
      return;
    }
    if (!open && selected) {
      setQuery(buildOptionLabel(selected, formatDate));
    }
  }, [value, selected, open, formatDate]);

  const clearSelection = () => {
    onChange('');
    setQuery('');
    setOpen(true);
    inputRef.current?.focus();
  };

  const pickOption = (option: VatReportOverpaidExpenseProductOption) => {
    onChange(String(option.expenseProductId));
    setQuery(buildOptionLabel(option, formatDate));
    setOpen(false);
  };

  return (
    <div ref={rootRef} className="relative w-full">
      <input
        ref={inputRef}
        type="search"
        autoComplete="off"
        role="combobox"
        aria-expanded={open}
        aria-autocomplete="list"
        placeholder={placeholder}
        value={displayValue}
        onChange={(e) => {
          const next = e.target.value;
          setQuery(next);
          setOpen(true);
          if (value) onChange('');
        }}
        onFocus={() => {
          setOpen(true);
          if (selected) setQuery(buildProductTitle(selected));
        }}
        className="w-full rounded-lg border border-gray-200 bg-white py-2 pr-16 pl-3 text-sm text-gray-800 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/20"
      />
      <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center gap-0.5 pr-2">
        {value && !open && (
          <button
            type="button"
            tabIndex={-1}
            aria-label="Ачысціць выбар"
            className="pointer-events-auto rounded p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-600"
            onMouseDown={(e) => e.preventDefault()}
            onClick={clearSelection}
          >
            <FiX className="size-4" aria-hidden />
          </button>
        )}
        <FiChevronDown
          className={`size-4 text-gray-400 transition ${open ? 'rotate-180' : ''}`}
          aria-hidden
        />
      </div>
      {open && (
        <ul
          className="absolute z-20 mt-1.5 max-h-60 w-full overflow-auto rounded-lg border border-gray-200 bg-white py-1 shadow-lg ring-1 ring-black/5"
          role="listbox"
        >
          {filtered.length === 0 ? (
            <li className="px-3 py-2.5 text-sm text-gray-500">{emptyMessage}</li>
          ) : (
            filtered.map((option) => {
              const optionId = String(option.expenseProductId);
              const isSelected = optionId === value;
              return (
                <li key={option.expenseProductId} role="option" aria-selected={isSelected}>
                  <button
                    type="button"
                    className={`w-full px-3 py-2.5 text-left text-sm transition hover:bg-gray-50 ${
                      isSelected ? 'bg-primary/5 font-medium text-primary' : 'text-gray-800'
                    }`}
                    onMouseDown={(e) => e.preventDefault()}
                    onClick={() => pickOption(option)}
                  >
                    {buildOptionLabel(option, formatDate)}
                  </button>
                </li>
              );
            })
          )}
        </ul>
      )}
    </div>
  );
}
