'use client'

import { CalendarPlus, Clock, Sparkle, VideoCamera } from '@phosphor-icons/react'
import Link from 'next/link'
import { useCallback, useEffect, useMemo, useState } from 'react'

import { Avatar, AvatarFallback, AvatarImage } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Card,
  CardAction,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui/card'
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from '@/components/ui/empty'
import { Progress } from '@/components/ui/progress'
import { Skeleton } from '@/components/ui/skeleton'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { hasCustomerSessionHint } from '@/components/booking/customer-session'
import { BookDemoDialog } from '@/components/account/demo/BookDemoDialog'
import { DEMO_HOSTS, initialsOf, type DemoHost } from '@/components/account/demo/hosts'
import {
  TRIAL_LENGTH_DAYS,
  formatDay,
  startOfToday,
  trialStart,
  trialStatus,
} from '@/components/account/demo/trial'
import { getLabels } from '@/components/account/labels'
import type { AccountLabels } from '@/components/account/labels'
import type { Locale } from '@/i18n/config'

/** The headline number: what the customer came to this page to check. */
function trialHeadline(labels: AccountLabels, daysLeft: number) {
  if (daysLeft === 1) return labels.trialLastDay
  return labels.trialDaysLeft(daysLeft)
}

/** /account/demo — free-trial standing, and booking time with the team. */
export function DemoContent({ locale }: { locale: Locale }) {
  const { customer } = useDemoBooking()
  const labels = useMemo(() => getLabels(locale), [locale])
  const today = useMemo(() => startOfToday(), [])

  const [createdDates, setCreatedDates] = useState<string[] | null>(null)
  const [host, setHost] = useState<DemoHost | null>(null)

  // Only the earliest ERP's creation date matters here, and it never changes,
  // so this page reads the list once.
  const loadProjects = useCallback(async () => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      setCreatedDates([])
      return
    }

    try {
      const response = await fetch('/api/customers/generation-projects', {
        cache: 'no-store',
      })
      if (!response.ok) {
        setCreatedDates([])
        return
      }
      const data = await response.json()
      const projects = Array.isArray(data?.projects) ? data.projects : []
      setCreatedDates(projects.map((project: { createdAt?: string }) => project.createdAt || '').filter(Boolean))
    } catch {
      setCreatedDates([])
    }
  }, [])

  useEffect(() => {
    loadProjects()
  }, [loadProjects])

  const start = useMemo(
    () => (createdDates ? trialStart(createdDates.map((createdAt) => ({ createdAt }))) : null),
    [createdDates],
  )
  const trial = useMemo(() => (start ? trialStatus(start, today) : null), [start, today])

  const bookingSection = (
    // Tightened from the stock card rhythm so both cards clear a 720px laptop
    // without scrolling. Padding gives way before type does — this panel is
    // deliberately large-print for its audience.
    <Card className="gap-4 py-4">
      <CardHeader>
        <CardTitle>{labels.demoBookTitle}</CardTitle>
        <CardDescription>{labels.demoBookHelper}</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <ul className="grid gap-3 sm:grid-cols-3">
          {DEMO_HOSTS.map((option) => (
            <li key={option.id}>
              <article className="flex h-full flex-col items-center gap-2.5 rounded-lg border p-3 text-center">
                <Avatar className="size-10">
                  {option.photoUrl ? (
                    <AvatarImage src={option.photoUrl} alt={option.name} />
                  ) : null}
                  <AvatarFallback className="bg-primary text-base font-medium text-primary-foreground">
                    {initialsOf(option.name)}
                  </AvatarFallback>
                </Avatar>
                <div className="flex flex-col gap-0.5">
                  <strong className="text-base font-semibold">{option.name}</strong>
                  <span className="flex items-center justify-center gap-1 text-xs text-muted-foreground">
                    <Clock aria-hidden="true" className="size-3.5" />
                    {labels.demoSlotLength}
                  </span>
                </div>
                <Button
                  type="button"
                  variant="outline"
                  className="mt-auto w-full"
                  onClick={() => setHost(option)}
                  aria-label={labels.demoBookWith(option.name)}
                >
                  <VideoCamera data-icon="inline-start" />
                  {labels.demoBookCta}
                </Button>
              </article>
            </li>
          ))}
        </ul>

        {/* Cal.com emails the manage links; saying so up front stops people
            hunting this page for a cancel button that will never be here. */}
        <p className="text-xs text-muted-foreground">{labels.demoManageNote}</p>
      </CardContent>
    </Card>
  )

  return (
    // No page heading: the shell's top bar already names this page, and the two
    // cards say what they are. Dropping it buys the height the page needs to
    // fit without scrolling. `aria-label` keeps the region named for
    // screen readers.
    <section aria-label={labels.demoTitle} className="flex flex-col gap-4">
      {createdDates === null ? (
        <Skeleton className="h-44 w-full rounded-xl" />
      ) : !trial ? (
        // No ERP yet: there is no clock to show, so point at the thing that
        // starts it rather than displaying a hollow "14 of 14".
        <Card>
          <CardContent>
            <Empty>
              <EmptyHeader>
                <EmptyMedia variant="icon">
                  <Sparkle />
                </EmptyMedia>
                <EmptyTitle>{labels.trialNotStarted}</EmptyTitle>
                <EmptyDescription>{labels.trialNotStartedHelper}</EmptyDescription>
              </EmptyHeader>
              <EmptyContent>
                <Button asChild>
                  <Link href={`/${locale}/account/create`}>
                    <CalendarPlus data-icon="inline-start" />
                    {labels.trialCreateErp}
                  </Link>
                </Button>
              </EmptyContent>
            </Empty>
          </CardContent>
        </Card>
      ) : (
        <Card className="gap-4 py-4">
          <CardHeader>
            <CardTitle>{labels.trialTitle}</CardTitle>
            <CardDescription>{labels.trialDayOf(trial.daysUsed + 1, TRIAL_LENGTH_DAYS)}</CardDescription>
            {/* Only an ended trial gets a badge. While it is running the days
                left are already the headline below, and a badge repeating them
                is the same sentence twice. */}
            {trial.expired ? (
              <CardAction>
                <Badge variant="outline">{labels.trialEnded}</Badge>
              </CardAction>
            ) : null}
          </CardHeader>

          <CardContent className="flex flex-col gap-4">
            {trial.expired ? (
              <p className="text-sm text-muted-foreground">{labels.trialEndedHelper}</p>
            ) : (
              <div className="flex flex-col gap-2">
                <span className="text-3xl font-semibold tracking-tight tabular-nums">
                  {trialHeadline(labels, trial.daysLeft)}
                </span>
                {/* Days *remaining*, so the bar drains as the trial runs out —
                    a bar that fills up would read as progress being made. */}
                <Progress
                  value={(trial.daysLeft / TRIAL_LENGTH_DAYS) * 100}
                  aria-label={labels.trialDaysLeft(trial.daysLeft)}
                />
              </div>
            )}

            <dl className="grid gap-4 sm:grid-cols-2">
              <div className="flex flex-col gap-0.5">
                <dt className="text-xs font-medium text-muted-foreground">{labels.trialStartedOn}</dt>
                <dd className="text-sm font-medium">{formatDay(locale, trial.start)}</dd>
              </div>
              <div className="flex flex-col gap-0.5">
                <dt className="text-xs font-medium text-muted-foreground">{labels.trialEndsOn}</dt>
                <dd className="text-sm font-medium">{formatDay(locale, trial.end)}</dd>
              </div>
            </dl>
          </CardContent>
        </Card>
      )}

      {bookingSection}

      <BookDemoDialog
        labels={labels}
        host={host}
        onOpenChange={(open) => {
          if (!open) setHost(null)
        }}
        name={customer?.name || customer?.representativeName}
        email={customer?.email}
      />
    </section>
  )
}
