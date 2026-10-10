'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { useEffect, useState } from 'react'
import { locales, type Locale } from '@/i18n/config'

export default function NotFoundPage() {
  const pathname = usePathname() || '/'
  const localeSegment = pathname.split('/').filter(Boolean)[0]
  const localeFromPath = locales.includes(localeSegment as Locale)
    ? (localeSegment as Locale)
    : null

  const [locale, setLocale] = useState<Locale>(localeFromPath ?? 'en')

  useEffect(() => {
    if (localeFromPath) {
      setLocale(localeFromPath)
      return
    }

    try {
      const savedLocale = window.localStorage.getItem('currentLocale')
      if (savedLocale === 'bg' || savedLocale === 'en') {
        setLocale(savedLocale)
      }
    } catch {
      // ignore storage unavailability
    }
  }, [localeFromPath])

  const copy =
    locale === 'bg'
      ? {
          title: 'Страницата не е намерена',
          description: 'Страницата, която търсите, не съществува или е преместена.',
          cta: 'Към началната страница',
        }
      : {
          title: 'Page not found',
          description: 'The page you’re looking for doesn’t exist or may have been moved.',
          cta: 'Go to homepage',
        }

  return (
    <section
      style={{
        position: 'relative',
        minHeight: '100svh',
        background: 'var(--background)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        overflow: 'hidden',
        padding: 16,
      }}
    >
      <div
        style={{
          position: 'relative',
          width: '100%',
          maxWidth: 620,
          textAlign: 'center',
          background: 'var(--card)',
          border: '1px solid var(--border)',
          borderRadius: 12,
          padding: '34px 24px',
        }}
      >
        <div
          style={{
            margin: '0 auto 14px',
            width: 60,
            height: 60,
            borderRadius: 12,
            background: 'color-mix(in srgb, var(--primary) 10%, transparent)',
            color: 'var(--primary)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            fontSize: 22,
            fontWeight: 700,
            lineHeight: 1,
          }}
        >
          404
        </div>

        <h1
          style={{
            margin: 0,
            color: 'var(--foreground)',
            fontSize: 'clamp(28px, 4.2vw, 42px)',
            lineHeight: 1.08,
            fontWeight: 700,
          }}
        >
          {copy.title}
        </h1>

        <p
          style={{
            margin: '14px auto 0',
            maxWidth: 500,
            color: 'var(--muted-foreground)',
            fontSize: 'clamp(15px, 1.8vw, 19px)',
            lineHeight: 1.35,
          }}
        >
          {copy.description}
        </p>

        <div style={{ marginTop: 24 }}>
          <Link
            href={`/${locale}`}
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              justifyContent: 'center',
              minWidth: 164,
              minHeight: 38,
              padding: '10px 14px',
              borderRadius: 8,
              background: 'var(--primary)',
              border: '1px solid var(--primary)',
              color: 'var(--primary-foreground)',
              textDecoration: 'none',
              fontSize: 14,
              lineHeight: 1,
              fontWeight: 700,
            }}
          >
            {copy.cta}
          </Link>
        </div>
      </div>
    </section>
  )
}
