'use client';
import { useTopbar } from './TopbarContext';

const btnBase =
  'inline-flex h-10 shrink-0 items-center justify-center gap-2 rounded-lg px-4 text-sm font-medium transition focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-4';

const btnPrimary = `${btnBase} bg-primary text-white shadow-sm hover:bg-primary-hover focus-visible:outline-primary`;

const btnSecondary = `${btnBase} border border-gray-200 bg-white text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-gray-300`;
const btnDanger = `${btnBase} border border-red-200 bg-red-50 text-red-700 shadow-sm hover:bg-red-100 focus-visible:outline-red-300`;
const btnIconOnly = 'w-10 px-0';
const backArrowOnlyClass =
  'inline-flex h-8 w-8 shrink-0 items-center justify-center rounded text-gray-500 transition hover:text-gray-900 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-300 disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-6';

const Topbar = () => {
  const { topbarButtons, topbarPage } = useTopbar();
  const leftButtons = topbarButtons.filter((btn) => btn.position === 'left');
  const rightButtons = topbarButtons.filter((btn) => btn.position !== 'left');

  const resolveButtonClass = (
    variant: 'primary' | 'secondary' | 'danger',
    iconOnly?: boolean
  ) =>
    `${variant === 'secondary' ? btnSecondary : variant === 'danger' ? btnDanger : btnPrimary} ${
      iconOnly ? btnIconOnly : ''
    }`;

  if (!topbarPage?.title && topbarButtons.length === 0) {
    return null;
  }

  return (
    <header className="shrink-0 border-b border-gray-200 bg-white">
      <div className="mx-auto flex w-full max-w-6xl items-center justify-between gap-4 px-6 py-4">
        <div className="min-w-0 flex flex-1 items-center gap-2">
          {leftButtons.map((btn, idx) => {
            const variant = btn.variant ?? 'secondary';
            const buttonClass = btn.iconOnly
              ? backArrowOnlyClass
              : resolveButtonClass(variant, btn.iconOnly);
            return (
              <button
                key={`left-${idx}`}
                type="button"
                onClick={btn.onClick}
                disabled={btn.disabled ?? !btn.onClick}
                className={buttonClass}
                aria-label={btn.label}
              >
                {btn.icon}
                {!btn.iconOnly && btn.label}
              </button>
            );
          })}
          <div className="min-w-0">
            {topbarPage?.title && (
              <h1 className="text-lg font-semibold tracking-tight text-gray-900 md:text-xl">
                {topbarPage.title}
              </h1>
            )}
            {topbarPage?.subtitle && (
              <p className="mt-0.5 text-sm text-gray-500">
                {topbarPage.subtitle}
              </p>
            )}
          </div>
        </div>
        {rightButtons.length > 0 && (
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-2">
            {rightButtons.map((btn, idx) => {
              const variant = btn.variant ?? 'primary';
              const buttonClass = resolveButtonClass(variant, btn.iconOnly);
              return (
                <button
                  key={idx}
                  type="button"
                  onClick={btn.onClick}
                  disabled={btn.disabled ?? !btn.onClick}
                  className={buttonClass}
                  aria-label={btn.label}
                  title={btn.label}
                >
                  {btn.icon}
                  {!btn.iconOnly && btn.label}
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
