'use client';
import { useTopbar } from './TopbarContext';

const btnBase =
  'inline-flex h-10 shrink-0 items-center justify-center gap-2 rounded-lg px-4 text-sm font-medium transition focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-4';

const btnPrimary =
  `${btnBase} bg-primary text-white shadow-sm hover:bg-primary-hover focus-visible:outline-primary`;

const btnSecondary =
  `${btnBase} border border-gray-200 bg-white text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-gray-300`;

const Topbar = () => {
  const { topbarButtons, topbarPage } = useTopbar();

  if (!topbarPage?.title && topbarButtons.length === 0) {
    return null;
  }

  return (
    <header className="shrink-0 border-b border-gray-200 bg-white">
      <div className="mx-auto flex w-full max-w-6xl items-center justify-between gap-4 px-6 py-4">
        <div className="min-w-0 flex-1">
          {topbarPage?.title && (
            <h1 className="text-lg font-semibold tracking-tight text-gray-900 md:text-xl">
              {topbarPage.title}
            </h1>
          )}
          {topbarPage?.subtitle && (
            <p className="mt-0.5 text-sm text-gray-500">{topbarPage.subtitle}</p>
          )}
        </div>
        {topbarButtons.length > 0 && (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-2">
            {topbarButtons.map((btn, idx) => {
              const variant = btn.variant ?? 'primary';
              return (
                <button
                  key={idx}
                  type="button"
                  onClick={btn.onClick}
                  disabled={btn.disabled ?? !btn.onClick}
                  className={variant === 'secondary' ? btnSecondary : btnPrimary}
                >
                  {btn.icon}
                  {btn.label}
                </button>
              );
            })}
          </div>
        )}
      </div>
    </header>
  );
};

export default Topbar;
