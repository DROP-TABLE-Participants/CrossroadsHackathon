import type { Metadata } from 'next'
import localFont from 'next/font/local'
import { Inter } from 'next/font/google'
import React from 'react'

import { getSiteUrl } from '@/lib/site-url'
import './globals.css'
import './theme.css'

export const metadata: Metadata = {
  metadataBase: new URL(`${getSiteUrl()}/`),
  robots: { index: false, follow: false },
}

const gilroyMedium = localFont({
  display: 'swap',
  src: [{ path: '../../../assets/Fonts/Gilroy/Gilroy-Medium.ttf', weight: '500', style: 'normal' }],
})

const inter = Inter({
  display: 'swap',
  subsets: ['latin', 'cyrillic'],
  variable: '--font-inter',
})

export default function PortalLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body className={`${gilroyMedium.className} ${inter.variable}`}>
        <div className="eg-page">{children}</div>
      </body>
    </html>
  )
}
