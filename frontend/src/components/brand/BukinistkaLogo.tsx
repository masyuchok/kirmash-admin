import Image from 'next/image';
import Link from 'next/link';
import logoSrc from '../../../public/bukinistka-logo.png';

type Props = {
  className?: string;
  linkToHome?: boolean;
};

export default function BukinistkaLogo({
  className = '',
  linkToHome = true,
}: Props) {
  const inner = (
    <div
      className={`relative mx-auto flex h-10 w-full max-w-[200px] items-center justify-center ${className}`}
    >
      <Image
        src={logoSrc}
        alt="Bukinistka"
        width={200}
        height={26}
        className="h-8 w-auto max-w-full object-contain object-center"
        priority
      />
    </div>
  );

  if (linkToHome) {
    return (
      <Link
        href="/bukinistka"
        className="block rounded-lg outline-none transition hover:bg-gray-50 focus-visible:ring-2 focus-visible:ring-amber-300/50"
      >
        {inner}
      </Link>
    );
  }

  return inner;
}
