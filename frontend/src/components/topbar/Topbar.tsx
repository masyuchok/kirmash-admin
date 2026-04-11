'use client';
import { useTopbar } from './TopbarContext';

const Topbar = () => {
  const { topbarButtons } = useTopbar();

  return (
    <div className="flex justify-end items-center gap-3 px-6 py-4 border-b border-gray-200/80 bg-white">
      {topbarButtons.map((btn, idx) => (
        <button
          key={idx}
          onClick={btn.onClick}
          className="flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2.5 text-sm font-medium text-white shadow-sm transition hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50"
          disabled={!btn.onClick}
        >
          {btn.icon}
          {btn.label}
        </button>
      ))}
    </div>
  );
};

export default Topbar;
