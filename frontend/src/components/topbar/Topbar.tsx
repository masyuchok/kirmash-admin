'use client';
import { useTopbar } from './TopbarContext';

const Topbar = () => {
  const { topbarButtons } = useTopbar();

  return (
    <div className="flex justify-end items-center gap-4 p-4 border-b bg-white">
      {topbarButtons.map((btn, idx) => (
        <button
          key={idx}
          onClick={btn.onClick}
          className="flex items-center gap-2 px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 transition disabled:opacity-50"
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
