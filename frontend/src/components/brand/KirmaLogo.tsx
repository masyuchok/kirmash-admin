import Image from 'next/image';
import Link from 'next/link';

/** Raster logo served from `public/kirma-logo.png`. */
const LOGO_SRC = '/kirma-logo.png';

type Props = {
  className?: string;
  /** Wrap in a link to home (default true for sidebar). */
  linkToHome?: boolean;
};

export default function KirmaLogo({
  className = '',
  linkToHome = true,
}: Props) {
  const inner = (
    <div className={`relative mx-auto h-16 w-full max-w-[160px] ${className}`}>
      <Image
        src={LOGO_SRC}
        alt="Kirma.sh"
        fill
        className="object-contain object-center"
        sizes="160px"
        priority
      />
    </div>
  );

  if (linkToHome) {
    return (
      <Link
        href="/"
        className="block rounded-lg outline-none ring-primary/0 transition hover:bg-gray-50 focus-visible:ring-2 focus-visible:ring-primary/30"
      >
        {inner}
      </Link>
    );
  }

  return inner;
}
