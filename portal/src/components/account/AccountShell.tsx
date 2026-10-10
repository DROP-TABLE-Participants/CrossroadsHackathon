'use client'

import Image from 'next/image'
import Link from 'next/link'
import { SignOut as LogOut, User as UserRound } from '@phosphor-icons/react'
import type { ReactNode } from 'react'

import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Toaster } from '@/components/ui/sonner'
import type { AccountLabels } from '@/components/account/labels'
import type { Locale } from '@/i18n/config'

type AccountShellProps = {
  locale: Locale
  labels: AccountLabels
  customerName: string
  customerEmail?: string
  onLogout: () => void
  onOpenProfile: () => void
  children: ReactNode
}

function initialsOf(name: string) {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  if (!parts.length) return '?'
  const first = parts[0]?.charAt(0) || ''
  const last = parts.length > 1 ? parts[parts.length - 1]?.charAt(0) || '' : ''
  return `${first}${last}`.toUpperCase() || '?'
}

export function AccountShell({
  locale,
  labels,
  customerName,
  customerEmail,
  onLogout,
  onOpenProfile,
  children,
}: AccountShellProps) {
  return (
    <div className="font-sans antialiased flex min-h-svh flex-col bg-background text-foreground">
      <header className="border-b bg-background">
        <div className="mx-auto flex w-full max-w-5xl items-center justify-between gap-4 px-4 py-3">
          <Link href={`/${locale}`} className="flex items-center" aria-label="ERP Generator home">
            <Image src="/erpgenerator-logo.svg" alt="ERP Generator" width={140} height={32} priority className="h-8 w-auto" />
          </Link>
          <div className="flex items-center gap-1.5">
            <Button variant="ghost" size="sm" className="text-muted-foreground" asChild>
              <Link href={`/${locale}`}>{labels.home}</Link>
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <button
                  type="button"
                  aria-label={customerName}
                  className="rounded-full outline-none ring-offset-2 ring-offset-background transition focus-visible:ring-2 focus-visible:ring-ring data-[state=open]:ring-2 data-[state=open]:ring-ring"
                >
                  <Avatar className="size-9">
                    <AvatarFallback className="bg-primary text-sm font-medium text-primary-foreground">
                      {initialsOf(customerName)}
                    </AvatarFallback>
                  </Avatar>
                </button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="min-w-52">
                <DropdownMenuLabel className="flex flex-col gap-0.5">
                  <span className="truncate text-sm font-medium">{customerName}</span>
                  {customerEmail ? (
                    <span className="truncate text-xs font-normal text-muted-foreground">{customerEmail}</span>
                  ) : null}
                </DropdownMenuLabel>
                <DropdownMenuSeparator />
                <DropdownMenuGroup>
                  <DropdownMenuItem onSelect={onOpenProfile}>
                    <UserRound />
                    {labels.profileTitle}
                  </DropdownMenuItem>
                  <DropdownMenuItem variant="destructive" onSelect={onLogout}>
                    <LogOut />
                    {labels.logout}
                  </DropdownMenuItem>
                </DropdownMenuGroup>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        </div>
      </header>
      <main className="mx-auto flex w-full max-w-5xl flex-1 flex-col gap-6 px-4 py-8">
        {children}
      </main>
      <Toaster />
    </div>
  )
}
