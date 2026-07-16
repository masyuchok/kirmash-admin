import OrganizationLogin from '@/components/auth/OrganizationLogin';
import { Suspense } from 'react';

export default function LoginPage() {
  return (
    <Suspense fallback={null}>
      <OrganizationLogin />
    </Suspense>
  );
}
