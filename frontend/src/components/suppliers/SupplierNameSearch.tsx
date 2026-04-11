'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { FiSearch } from 'react-icons/fi';
import type { Supplier } from '@/types/supplier';

const MAX_SUGGESTIONS = 15;

type Props = {
  suppliers: Supplier[];
  value: string;
  onChange: (value: string) => void;
};

export default function SupplierNameSearch({ suppliers, value, onChange }: Props) {
  const rootRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);

  const suggestions = useMemo(() => {
    const q = value.trim().toLowerCase();
    if (!q) return [];
    return suppliers
      .filter((s) => s.name.toLowerCase().startsWith(q))
      .slice(0, MAX_SUGGESTIONS);
  }, [suppliers, value]);

  useEffect(() => {
    if (!open) return;
    const onDocMouseDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', onDocMouseDown);
    return () => document.removeEventListener('mousedown', onDocMouseDown);
  }, [open]);

  const showList = open && value.trim() !== '' && suggestions.length > 0;

  return (
    <div ref={rootRef} className="w-full">
      <label
        htmlFor="supplier-name-search"
        className="mb-2 block text-xs font-semibold uppercase tracking-wide text-gray-500"
      >
        Пошук па назве
      </label>
      <div className="relative w-full">
        <FiSearch
          className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-gray-400"
          aria-hidden
        />
        <input
          id="supplier-name-search"
          type="search"
          autoComplete="off"
          className="w-full rounded-lg border border-gray-200 bg-white py-2.5 pl-10 pr-3 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500/25"
          placeholder="Пачніце ўводзіць назву..."
          value={value}
          onChange={(e) => {
            onChange(e.target.value);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
        />
        {showList && (
          <ul
            className="absolute z-20 mt-1.5 max-h-60 w-full overflow-auto rounded-lg border border-gray-200 bg-white py-1 shadow-lg ring-1 ring-black/5"
            role="listbox"
          >
            {suggestions.map((s) => (
              <li key={s.id} role="option">
                <button
                  type="button"
                  className="w-full px-3 py-2.5 text-left text-sm text-gray-800 transition hover:bg-gray-50"
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => {
                    onChange(s.name);
                    setOpen(false);
                  }}
                >
                  {s.name}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
