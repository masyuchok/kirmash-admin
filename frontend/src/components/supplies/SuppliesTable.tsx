import type { SupplyListItem } from '@/types/supply';

type Props = {
  supplies: SupplyListItem[];
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

export default function SuppliesTable({ supplies }: Props) {
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
            <th className="whitespace-nowrap px-6 py-3.5">Пастаўшчык</th>
            <th className="whitespace-nowrap px-4 py-3.5">Дата</th>
            <th className="whitespace-nowrap px-6 py-3.5 text-right tabular-nums">Кнігі</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100 bg-white">
          {supplies.map((row) => (
            <tr key={row.id} className="transition hover:bg-gray-50/80">
              <td className="whitespace-nowrap px-6 py-3.5 font-medium text-gray-900">
                {row.supplierName}
              </td>
              <td className="whitespace-nowrap px-4 py-3.5 text-gray-600">{formatSupplyDate(row.date)}</td>
              <td className="px-6 py-3.5 text-right tabular-nums text-gray-700">{row.booksNumber}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
