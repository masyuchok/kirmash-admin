import type { NextConfig } from 'next';

const apiProxyTarget = process.env.API_PROXY_TARGET?.trim();

const nextConfig: NextConfig = {
  reactStrictMode: true,
  distDir: '.next-runtime',
  experimental: {
    serverActions: {
      bodySizeLimit: '32mb',
    },
  },
  async rewrites() {
    if (!apiProxyTarget) {
      return [];
    }

    return [
      {
        source: '/api/:path*',
        destination: `${apiProxyTarget.replace(/\/$/, '')}/:path*`,
      },
    ];
  },
};

export default nextConfig;
