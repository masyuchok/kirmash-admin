import type { Supplier } from '@/types/supplier';
import { FiEdit } from 'react-icons/fi';
import { HiMiniArchiveBoxXMark } from 'react-icons/hi2';
import { ImBooks } from 'react-icons/im';
import { TiDocumentText } from 'react-icons/ti';

interface Props {
  suppliers: Supplier[];
  onEdit: (supplier: Supplier) => void;
  onInventory: (supplier: Supplier) => void;
  /** When true and the list is empty, copy assumes an active search filter. */
  hasActiveFilter?: boolean;
}

const ghostBtn =
  'inline-flex size-9 items-center justify-center rounded-lg text-gray-500 transition hover:bg-gray-100 hover:text-gray-900 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/30';

const SuppliersTable = ({
  suppliers,
  onEdit,
  onInventory,
  hasActiveFilter,
}: Props) => {
  if (suppliers.length === 0) {
    return (
      <div className="px-6 py-16 text-center">
        <p className="text-sm font-medium text-gray-900">
          {hasActiveFilter ? 'Нічога не знойдзена' : 'Пастаўшчыкоў пакуль няма'}
        </p>
        <p className="mt-1 text-sm text-gray-500">
          {hasActiveFilter
            ? 'Паспрабуйце змяніць запыт пошуку.'
            : 'Дадайце першага пастаўшчыка праз кнопку ўверсе.'}
        </p>
      </div>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="min-w-full border-collapse text-left text-sm">
        <thead>
          <tr className="border-b border-gray-200 bg-gray-50/90 text-xs font-semibold uppercase tracking-wide text-gray-500 backdrop-blur-sm">
            <th className="whitespace-nowrap px-6 py-3.5">Назва</th>
            <th className="whitespace-nowrap px-4 py-3.5">Telegram</th>
            <th className="whitespace-nowrap px-4 py-3.5">Сайт</th>
            <th className="whitespace-nowrap px-4 py-3.5">Краіна</th>
            <th className="whitespace-nowrap px-4 py-3.5">Горад</th>
            <th className="whitespace-nowrap px-4 py-3.5">VAT</th>
            <th className="whitespace-nowrap px-6 py-3.5 text-right">
              Дзеянні
            </th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100 bg-white">
          {suppliers.map((s) => (
            <tr key={s.id} className="transition hover:bg-gray-50/80">
              <td className="whitespace-nowrap px-6 py-3.5 font-medium text-gray-900">
                {s.name}
              </td>
              <td
                className="max-w-[10rem] truncate px-4 py-3.5 text-gray-600"
                title={s.telegram}
              >
                {s.telegram}
              </td>
              <td className="max-w-[14rem] px-4 py-3.5">
                <a
                  href={s.website}
                  className="block truncate text-primary hover:text-primary-hover hover:underline"
                  title={s.website}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  {s.website}
                </a>
              </td>
              <td className="whitespace-nowrap px-4 py-3.5 text-gray-600">
                {s.country}
              </td>
              <td className="whitespace-nowrap px-4 py-3.5 text-gray-600">
                {s.city}
              </td>
              <td className="px-4 py-3.5">
                {s.isVatPayer ? (
                  <span className="inline-flex rounded-full bg-emerald-50 px-2.5 py-0.5 text-xs font-medium text-emerald-800 ring-1 ring-inset ring-emerald-600/15">
                    Так
                  </span>
                ) : (
                  <span className="inline-flex rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600 ring-1 ring-inset ring-gray-500/10">
                    Не
                  </span>
                )}
              </td>
              <td className="px-6 py-3.5">
                <div className="flex flex-wrap items-center justify-end gap-0.5">
                  <button
                    type="button"
                    className={ghostBtn}
                    aria-label="Рэдагаваць"
                    onClick={() => onEdit(s)}
                  >
                    <FiEdit className="size-4" />
                  </button>
                  <button type="button" className={ghostBtn} aria-label="Архіў">
                    <HiMiniArchiveBoxXMark className="size-4" />
                  </button>
                  <button
                    type="button"
                    className={ghostBtn}
                    aria-label="Інвентарызацыя"
                    title="Інвентарызацыя"
                    onClick={() => onInventory(s)}
                  >
                    <ImBooks className="size-4" />
                  </button>
                  <button
                    type="button"
                    className={ghostBtn}
                    aria-label="Дакументы"
                  >
                    <TiDocumentText className="size-4" />
                  </button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
};

export default SuppliersTable;
