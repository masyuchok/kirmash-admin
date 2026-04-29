'use client';

type Props = {
  label?: string;
  className?: string;
  sizeClassName?: string;
};

export default function LoadingSpinner({
  label = 'Загрузка...',
  className = 'mx-auto w-full max-w-6xl rounded-xl border border-gray-200 bg-white py-16 shadow-sm',
  sizeClassName = 'size-8 border-[3px]',
}: Props) {
  return (
    <div className={className}>
      <div className="flex flex-col items-center justify-center gap-3">
        <div
          className={`${sizeClassName} animate-spin rounded-full border-primary/25 border-t-primary`}
          aria-hidden
        />
        <p className="text-sm text-gray-500">{label}</p>
      </div>
    </div>
  );
}
