const defaultLandingUrl = 'http://localhost:3000'

export function getLandingUrl() {
  const configured = process.env.ERP_GENERATOR_LANDING_PUBLIC_URL?.trim() || defaultLandingUrl
  const parsed = new URL(configured)
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password) {
    throw new Error('ERP_GENERATOR_LANDING_PUBLIC_URL must be an HTTP(S) origin without credentials.')
  }
  if (parsed.pathname !== '/' || parsed.search || parsed.hash) {
    throw new Error('ERP_GENERATOR_LANDING_PUBLIC_URL must be an origin.')
  }
  if (process.env.NODE_ENV === 'production' && parsed.protocol !== 'https:') {
    throw new Error('ERP_GENERATOR_LANDING_PUBLIC_URL must use HTTPS in production.')
  }
  return parsed.origin
}
