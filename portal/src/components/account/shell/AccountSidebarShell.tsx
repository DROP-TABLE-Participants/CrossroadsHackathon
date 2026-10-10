'use client'

import Link from 'next/link'
import { usePathname, useSearchParams } from 'next/navigation'
import { Buildings, SquaresFour, Plus, Question } from '@phosphor-icons/react'
import type { CSSProperties, ReactNode } from 'react'
import { Separator } from '@/components/ui/separator'
import { Sidebar, SidebarContent, SidebarHeader, SidebarInset, SidebarMenu, SidebarMenuButton, SidebarMenuItem, SidebarProvider, SidebarTrigger } from '@/components/ui/sidebar'
import { Toaster } from '@/components/ui/sonner'
import type { AccountLabels } from '@/components/account/labels'
import type { Locale } from '@/i18n/config'

export function AccountSidebarShell({ locale, children }: { locale: Locale; labels: AccountLabels; children: ReactNode }) {
  const pathname = usePathname()
  const search = useSearchParams().toString()
  const query = search ? `?${search}` : ''
  const base = `/${locale}/account`
  const items = [
    { href: base, label: locale === 'bg' ? 'Моите ERP системи' : 'My ERPs', icon: SquaresFour },
    { href: `${base}/create`, label: locale === 'bg' ? 'Създай ERP' : 'Create ERP', icon: Plus },
    { href: `${base}/help`, label: locale === 'bg' ? 'Помощ' : 'Help', icon: Question },
  ]
  return <div data-surface="account" className="font-sans antialiased text-foreground"><SidebarProvider style={{ '--sidebar-width': '14rem' } as CSSProperties}>
    <Sidebar collapsible="icon"><SidebarHeader className="border-b"><SidebarMenu><SidebarMenuItem><SidebarMenuButton size="lg" asChild><Link href={base}><Buildings className="size-5 text-primary" /><span className="font-semibold">ERP Generator</span></Link></SidebarMenuButton></SidebarMenuItem></SidebarMenu></SidebarHeader>
      <SidebarContent className="p-2"><SidebarMenu>{items.map(item => <SidebarMenuItem key={item.href}><SidebarMenuButton asChild isActive={pathname === item.href} tooltip={item.label}><Link href={item.href}><item.icon /><span>{item.label}</span></Link></SidebarMenuButton></SidebarMenuItem>)}</SidebarMenu></SidebarContent>
    </Sidebar>
    <SidebarInset className="min-w-0 bg-background"><header className="flex h-12 shrink-0 items-center gap-2 border-b px-4"><SidebarTrigger /><Separator orientation="vertical" className="h-4" /><span className="text-sm text-muted-foreground">{items.find(v => pathname === v.href)?.label ?? (pathname.endsWith('/feedback') ? (locale === 'bg' ? 'Обратна връзка' : 'Feedback') : 'ERP Generator')}</span><div className="ml-auto flex gap-1 text-xs">{(['en', 'bg'] as const).map(language => <Link key={language} href={pathname.replace(/^\/(en|bg)/, `/${language}`) + query} lang={language} aria-current={locale === language ? 'true' : undefined} className={`rounded-md px-2 py-1.5 ${locale === language ? 'bg-accent font-medium text-accent-foreground' : 'text-muted-foreground hover:bg-muted'}`}>{language.toUpperCase()}</Link>)}</div></header><div className="flex min-w-0 flex-1 flex-col gap-5 p-4 lg:p-6">{children}</div></SidebarInset>
  </SidebarProvider><Toaster /></div>
}
