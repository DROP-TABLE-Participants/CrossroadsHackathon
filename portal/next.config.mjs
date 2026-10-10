/** @type {import('next').NextConfig} */
const nextConfig = {
  deploymentId: process.env.NEXT_DEPLOYMENT_ID,
  ...(process.env.NEXT_DEPLOYMENT_ID
    ? { generateBuildId: async () => process.env.NEXT_DEPLOYMENT_ID }
    : {}),
  experimental: {
    optimizePackageImports: ['@phosphor-icons/react'],
  },
  output: 'standalone',
}

export default nextConfig
