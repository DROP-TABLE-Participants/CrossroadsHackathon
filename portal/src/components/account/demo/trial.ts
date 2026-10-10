import { addDays, differenceInCalendarDays } from 'date-fns'

/** The free trial runs for two weeks from the day the first ERP was created. */
export const TRIAL_LENGTH_DAYS = 14

export type TrialStatus = {
  /** Local calendar day the trial opened — day 1, not day 0. */
  start: Date
  /** Last day the trial is still live, inclusive. */
  end: Date
  daysUsed: number
  daysLeft: number
  expired: boolean
}

export function startOfToday(): Date {
  const date = new Date()
  date.setHours(0, 0, 0, 0)
  return date
}

/** The local calendar day an ISO timestamp falls on. */
function toLocalDay(value: string): Date | null {
  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) return null
  parsed.setHours(0, 0, 0, 0)
  return parsed
}

/**
 * When the trial started: the day the customer created their *first* ERP.
 *
 * The projects list arrives newest-first, so this deliberately scans for the
 * earliest rather than trusting order — creating a second ERP must not restart
 * anyone's trial, and must not shorten it either.
 */
export function trialStart(projects: Array<{ createdAt?: string }>): Date | null {
  let earliest: Date | null = null

  for (const project of projects) {
    if (!project.createdAt) continue
    const day = toLocalDay(project.createdAt)
    if (!day) continue
    if (!earliest || day < earliest) earliest = day
  }

  return earliest
}

/**
 * Days remaining, counted in calendar days rather than elapsed hours: someone
 * who signs up at 23:00 has not burned a day by midnight, and a customer
 * checking on the morning of day 14 should still read "1 day left".
 */
export function trialStatus(start: Date, today: Date): TrialStatus {
  const daysUsed = Math.max(0, differenceInCalendarDays(today, start))
  const daysLeft = Math.max(0, TRIAL_LENGTH_DAYS - daysUsed)

  return {
    start,
    end: addDays(start, TRIAL_LENGTH_DAYS - 1),
    daysUsed,
    daysLeft,
    expired: daysLeft === 0,
  }
}

function dayFormatter(locale: 'en' | 'bg') {
  return new Intl.DateTimeFormat(locale === 'bg' ? 'bg-BG' : 'en-US', { dateStyle: 'medium' })
}

export function formatDay(locale: 'en' | 'bg', date: Date): string {
  return dayFormatter(locale).format(date)
}
