import { GetServerSideProps } from 'next';

export const getServerSideProps: GetServerSideProps = async ({ req }) => {
  const cookies = req.headers.cookie || '';
  const jwt = cookies.split(';').find((c) => c.trim().startsWith('jwt_token='));

  if (!jwt) {
    return {
      redirect: {
        destination: `${process.env.NEXT_PUBLIC_API_URL!}/auth/login?shop=${process.env.NEXT_PUBLIC_SHOP_DOMAIN!}`,
        permanent: false,
      },
    };
  }

  return { props: {} };
};

export default function Home() {
  return (
    <div className="h-screen flex items-center justify-center text-xl font-semibold">
      Добро пожаловать в админку! 🎉
    </div>
  );
}
