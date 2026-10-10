const DEFAULT_SITE_URL = 'https://erpgenerator.example'

const removeTrailingSlash = (value: string): string => value.replace(/\/$/, '')

export const getSiteUrl = (): string => {
  const rawUrl = process.env.NEXT_PUBLIC_SITE_URL || process.env.FRONTEND_URL || DEFAULT_SITE_URL

  try {
    return removeTrailingSlash(new URL(rawUrl).toString())
  } catch {
    return DEFAULT_SITE_URL
  }
}

/**
 * Returns the externally reachable Portal origin used in account and interview links.
 */
export const getCanonicalPublicSiteUrl = (): string => {
  const configured = process.env.NEXT_PUBLIC_SITE_URL?.trim()
  if (!configured) {
    if (process.env.NODE_ENV === 'production') {
      throw new Error('NEXT_PUBLIC_SITE_URL is required for public links in production.')
    }

    return getSiteUrl()
  }

  let parsed: URL
  try {
    parsed = new URL(configured)
  } catch {
    throw new Error('NEXT_PUBLIC_SITE_URL must be a valid absolute URL.')
  }

  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password) {
    throw new Error('NEXT_PUBLIC_SITE_URL must be an HTTP(S) URL without embedded credentials.')
  }
  if (process.env.NODE_ENV === 'production' && parsed.protocol !== 'https:') {
    throw new Error('NEXT_PUBLIC_SITE_URL must use HTTPS in production.')
  }
  if (parsed.pathname !== '/' || parsed.search || parsed.hash) {
    throw new Error('NEXT_PUBLIC_SITE_URL must be an origin without a path, query string, or fragment.')
  }

  return parsed.origin
}

export const toAbsoluteUrl = (path: string): string => {
  const normalizedPath = path.startsWith('/') ? path : `/${path}`
  return new URL(normalizedPath, `${getSiteUrl()}/`).toString()
}
