'use client'

import {
  CalendarDots,
  CaretUpDown,
  FolderSimple,
  FlowArrow,
  Gauge,
  Scroll,
  ShieldCheck,
  SignOut,
  UsersThree,
} from '@phosphor-icons/react'
import Link from 'next/link'
import { usePathname, useRouter } from 'next/navigation'
import type { ReactNode } from 'react'

import { useAdminSession } from '@/components/admin/AdminSessionProvider'
import type { AdminLabels } from '@/components/admin/labels'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Separator } from '@/components/ui/separator'
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarProvider,
  SidebarTrigger,
} from '@/components/ui/sidebar'
import { Toaster } from '@/components/ui/sonner'
import type { Locale } from '@/i18n/config'

function initials(name: string) {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((part) => part.charAt(0))
    .join('')
    .toUpperCase() || 'PA'
}

export function AdminSidebarShell({
  locale,
  labels,
  children,
}: {
  locale: Locale
  labels: AdminLabels
  children: ReactNode
}) {
  const { admin, logout } = useAdminSession()
  const pathname = usePathname()
  const router = useRouter()
  const base = `/${locale}/admin`
  const items = [
    { href: base, label: labels.overview, icon: Gauge, exact: true },
    { href: `${base}/customers`, label: labels.customers, icon: UsersThree },
    { href: `${base}/projects`, label: labels.projects, icon: FolderSimple },
    { href: `${base}/orchestration`, label: labels.orchestration, icon: FlowArrow },
    { href: `${base}/administrators`, label: labels.administrators, icon: ShieldCheck },
    { href: `${base}/demo-bookings`, label: labels.demoBookings, icon: CalendarDots },
    { href: `${base}/audit`, label: labels.auditLog, icon: Scroll },
  ]
  const activeItem = [...items]
    .filter((item) => item.exact ? pathname === item.href : pathname.startsWith(item.href))
    .sort((a, b) => b.href.length - a.href.length)[0] ?? items[0]

  return (
    <div data-surface="account" className="font-sans antialiased text-foreground">
      <SidebarProvider>
        <Sidebar variant="inset" collapsible="icon">
          <SidebarHeader>
            <SidebarMenu>
              <SidebarMenuItem>
                <SidebarMenuButton size="lg" asChild>
                  <Link href={base} aria-label={labels.administration}>
                    <div className="flex aspect-square size-8 items-center justify-center rounded-lg bg-primary text-primary-foreground">
                      <ShieldCheck className="size-4" aria-hidden="true" />
                    </div>
                    <div className="grid flex-1 text-left text-sm leading-tight">
                      <span className="truncate font-semibold">ERP Generator</span>
                      <span className="truncate text-xs text-muted-foreground">{labels.workspace}</span>
                    </div>
                  </Link>
                </SidebarMenuButton>
              </SidebarMenuItem>
            </SidebarMenu>
          </SidebarHeader>
          <SidebarContent>
            <SidebarGroup>
              <SidebarGroupLabel>{labels.administration}</SidebarGroupLabel>
              <SidebarGroupContent>
                <SidebarMenu>
                  {items.map((item) => (
                    <SidebarMenuItem key={item.href}>
                      <SidebarMenuButton
                        asChild
                        isActive={activeItem.href === item.href}
                        tooltip={item.label}
                      >
                        <Link href={item.href}>
                          <item.icon aria-hidden="true" />
                          <span>{item.label}</span>
                        </Link>
                      </SidebarMenuButton>
                    </SidebarMenuItem>
                  ))}
                </SidebarMenu>
              </SidebarGroupContent>
            </SidebarGroup>
          </SidebarContent>
          <SidebarFooter>
            <SidebarMenu>
              <SidebarMenuItem>
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <SidebarMenuButton size="lg">
                      <Avatar className="size-8">
                        <AvatarFallback className="bg-primary text-xs text-primary-foreground">
                          {initials(admin?.name || admin?.email || '')}
                        </AvatarFallback>
                      </Avatar>
                      <div className="grid flex-1 text-left text-sm leading-tight">
                        <span className="truncate font-medium">{admin?.name || admin?.email}</span>
                        <span className="truncate text-xs text-muted-foreground">{admin?.email}</span>
                      </div>
                      <CaretUpDown className="ml-auto size-4" aria-hidden="true" />
                    </SidebarMenuButton>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent side="top" align="start" className="min-w-56">
                    <DropdownMenuLabel>
                      <span className="block truncate">{admin?.name}</span>
                      <span className="block truncate text-xs font-normal text-muted-foreground">
                        {admin?.role}
                      </span>
                    </DropdownMenuLabel>
                    <DropdownMenuSeparator />
                    <DropdownMenuItem
                      onSelect={async () => {
                        await logout()
                        router.replace(`/${locale}/admin/login`)
                      }}
                      className="focus:bg-destructive/10"
                    >
                      <SignOut />
                      <span className="text-destructive">{labels.signOut}</span>
                    </DropdownMenuItem>
                  </DropdownMenuContent>
                </DropdownMenu>
              </SidebarMenuItem>
            </SidebarMenu>
          </SidebarFooter>
        </Sidebar>
        <SidebarInset className="bg-card md:peer-data-[variant=inset]:border">
          <header className="flex h-14 shrink-0 items-center gap-2 border-b px-4">
            <SidebarTrigger className="-ml-1" />
            <Separator orientation="vertical" className="mr-1 data-[orientation=vertical]:h-4" />
            <span className="text-sm font-medium">{activeItem.label}</span>
          </header>
          <div className="flex flex-1 flex-col gap-6 p-4 md:p-6">{children}</div>
        </SidebarInset>
      </SidebarProvider>
      <Toaster />
    </div>
  )
}
