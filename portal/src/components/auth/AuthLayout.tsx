import Image from 'next/image'
import Link from 'next/link'
import React from 'react'

import type { Locale } from '@/i18n/config'
import type { AuthCopy } from './copy'
import { AuthShowcase } from './AuthShowcase'

/**
 * Split-screen auth shell: form column on the left (logo top, form centered,
 * legal notice pinned to the bottom), decorative ERP Generator showcase on the
 * right (hidden below lg, where the form goes full-width).
 *
 * `font-sans` on the root is required — theme.css scopes the shadcn element
 * resets to `.font-sans`, so without it buttons/inputs fall back to native
 * chrome.
 */
export function AuthLayout({
  locale,
  title,
  description,
  legal,
  showcase,
  children,
}: {
  locale: Locale
  title: string
  description: string
  legal: AuthCopy['legal']
  showcase: AuthCopy['showcase']
  children: React.ReactNode
}) {
  return (
    <div className="font-sans antialiased grid min-h-svh bg-background text-foreground lg:grid-cols-2">
      <div className="relative flex min-h-svh flex-col px-6 py-8 sm:px-10 lg:px-14">
        <Link href={`/${locale}`} className="inline-flex items-center self-start" aria-label="ERP Generator">
          <Image src="/erpgenerator-logo.svg" alt="ERP Generator" width={140} height={32} priority className="h-8 w-auto" />
        </Link>

        <main className="flex flex-1 items-center justify-center py-10">
          <div className="w-full max-w-[400px]">
            <header className="mb-8 text-center">
              <h1 className="text-3xl font-semibold tracking-tight text-foreground">{title}</h1>
              <p className="mt-2 text-muted-foreground">{description}</p>
            </header>
            {children}
          </div>
        </main>

        <footer className="mx-auto w-full max-w-[400px]">
          <p className="text-center text-xs leading-relaxed text-muted-foreground">
            {legal.prefix}{' '}
            <Link
              href={`/${locale}/terms-of-service`}
              className="text-foreground underline underline-offset-2 hover:text-primary"
            >
              {legal.terms}
            </Link>{' '}
            {legal.and}{' '}
            <Link
              href={`/${locale}/privacy-policy`}
              className="text-foreground underline underline-offset-2 hover:text-primary"
            >
              {legal.privacy}
            </Link>
            .
          </p>
        </footer>
      </div>

      <AuthShowcase className="hidden lg:block" features={showcase.features} />
    </div>
  )
}
