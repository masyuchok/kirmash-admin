'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { FiChevronDown, FiX } from 'react-icons/fi';

export type ProductSearchOption = {
  shopifyProductId: string;
  productName: string;
};

type Props = {
  products: ProductSearchOption[];
  value: string;
  onChange: (product: ProductSearchOption | null) => void;
  placeholder?: string;
  emptyMessage?: string;
};

export default function ProductSearchSelect({
  products,
  value,
  onChange,
  placeholder = 'Пошук або выбар тавару...',
  emptyMessage = 'Тавары не знойдзены',
}: Props) {
  const rootRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');

  const selected = useMemo(
    () => products.find((p) => p.shopifyProductId === value) ?? null,
    [products, value]
  );

  const sortedProducts = useMemo(
    () =>
      [...products].sort((a, b) =>
        a.productName.localeCompare(b.productName, 'be')
      ),
    [products]
  );

  const filtered = useMemo(() => {
    const search = query.trim().toLowerCase();
    if (!search) return sortedProducts;
    return sortedProducts.filter((p) =>
      p.productName.toLowerCase().includes(search)
    );
  }, [sortedProducts, query]);

  const displayValue = open ? query : (selected?.productName ?? query);

  useEffect(() => {
    if (!open) return;
    const onDocMouseDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
        setOpen(false);
        setQuery(selected?.productName ?? '');
      }
    };
    document.addEventListener('mousedown', onDocMouseDown);
    return () => document.removeEventListener('mousedown', onDocMouseDown);
  }, [open, selected?.productName]);

  useEffect(() => {
    if (!value) {
      setQuery('');
      return;
    }
    if (!open && selected) {
      setQuery(selected.productName);
    }
  }, [value, selected, open]);

  const clearSelection = () => {
    onChange(null);
    setQuery('');
    setOpen(true);
    inputRef.current?.focus();
  };

  const pickProduct = (product: ProductSearchOption) => {
    onChange(product);
    setQuery(product.productName);
    setOpen(false);
  };

  const showList = open;

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
          if (value) onChange(null);
        }}
        onFocus={() => {
          setOpen(true);
          if (selected) setQuery(selected.productName);
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
      {showList && (
        <ul
          className="absolute z-20 mt-1.5 max-h-60 w-full overflow-auto rounded-lg border border-gray-200 bg-white py-1 shadow-lg ring-1 ring-black/5"
          role="listbox"
        >
          {filtered.length === 0 ? (
            <li className="px-3 py-2.5 text-sm text-gray-500">
              {emptyMessage}
            </li>
          ) : (
            filtered.map((product) => (
              <li
                key={product.shopifyProductId}
                role="option"
                aria-selected={product.shopifyProductId === value}
              >
                <button
                  type="button"
                  className={`w-full px-3 py-2.5 text-left text-sm transition hover:bg-gray-50 ${
                    product.shopifyProductId === value
                      ? 'bg-primary/5 font-medium text-primary'
                      : 'text-gray-800'
                  }`}
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => pickProduct(product)}
                >
                  {product.productName}
                </button>
              </li>
            ))
          )}
        </ul>
      )}
    </div>
  );
}
