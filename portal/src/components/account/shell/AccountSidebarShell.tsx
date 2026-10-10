'use client'

import Link from 'next/link'
import { usePathname, useSearchParams } from 'next/navigation'
import { Buildings, SquaresFour, Plus, Question } from '@phosphor-icons/react'
import type { ReactNode } from 'react'
import { Separator } from '@/components/ui/separator'
import { Sidebar, SidebarContent, SidebarFooter, SidebarHeader, SidebarInset, SidebarMenu, SidebarMenuButton, SidebarMenuItem, SidebarProvider, SidebarTrigger } from '@/components/ui/sidebar'
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
  return <div data-surface="account" className="font-sans antialiased text-foreground"><SidebarProvider>
    <Sidebar variant="inset" collapsible="icon"><SidebarHeader><SidebarMenu><SidebarMenuItem><SidebarMenuButton size="lg" asChild><Link href={base}><Buildings className="size-6" /><span className="font-semibold">ERP Generator</span></Link></SidebarMenuButton></SidebarMenuItem></SidebarMenu></SidebarHeader>
      <SidebarContent><SidebarMenu>{items.map(item => <SidebarMenuItem key={item.href}><SidebarMenuButton asChild isActive={pathname === item.href} tooltip={item.label}><Link href={item.href}><item.icon /><span>{item.label}</span></Link></SidebarMenuButton></SidebarMenuItem>)}</SidebarMenu></SidebarContent>
      <SidebarFooter><p className="p-2 text-xs text-muted-foreground group-data-[collapsible=icon]:hidden">{locale === 'bg' ? 'Лично пространство за гости' : 'Your guest workspace'}</p></SidebarFooter>
    </Sidebar>
    <SidebarInset className="bg-card md:peer-data-[variant=inset]:border"><header className="flex h-14 items-center gap-2 border-b px-4"><SidebarTrigger /><Separator orientation="vertical" className="h-4" /><span className="text-sm font-medium">{items.find(v => pathname === v.href)?.label ?? 'ERP Generator'}</span><div className="ml-auto flex gap-3 text-xs"><Link href={pathname.replace(/^\/(en|bg)/, '/en') + query}>EN</Link><Link href={pathname.replace(/^\/(en|bg)/, '/bg') + query}>BG</Link></div></header><div className="flex flex-1 flex-col gap-6 p-4 md:p-6">{children}</div></SidebarInset>
  </SidebarProvider><Toaster /></div>
}
