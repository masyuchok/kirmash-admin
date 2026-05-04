import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  /* config options here */
  reactStrictMode: true,
  // Use custom build directory to avoid intermittent lock/corruption
  // on default `.next` folder in this Windows workspace.
  distDir: ".next-runtime",
};

export default nextConfig;
