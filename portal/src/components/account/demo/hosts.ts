/**
 * The ERP Generator people a customer can book a call with.
 *
 * Scheduling lives in Cal.com: it owns each host's real availability, the
 * 30-minute weekday slots, timezone conversion, and the reschedule/cancel links
 * that go out with the confirmation email. Nothing about a booked call is
 * stored in Portal, so there is no second copy to drift out of date.
 *
 * `calLink` is the `username/event-slug` half of a cal.com URL — for
 * https://cal.com/esemenov/30min that is `esemenov/30min`.
 *
 * NEXT_PUBLIC_CALCOM_* overrides let each handle/event-slug be changed without a
 * deploy; the defaults below are the current cal.com handles.
 *
 * Job titles are deliberately absent: these are real colleagues, and the code
 * has no source of truth for their roles.
 */
export type DemoHost = {
  id: string
  name: string
  calLink: string
  /**
   * Which Cal instance hosts this person. Omit for cal.com (the embed's
   * default). Stoyan is on cal.eu — Cal's EU-residency instance — which is a
   * genuinely different origin, not a vanity domain: pointing the default
   * app.cal.com at his handle just 404s.
   */
  calOrigin?: string
  /**
   * The host's public Cal profile photo (`/api/avatar/<uuid>.png`, keyed to
   * the Cal instance above). Radix's Avatar falls back to the initials on its
   * own if this is absent or fails to load, so a re-uploaded photo (new uuid)
   * degrades to initials rather than breaking the card — refresh it here or via
   * the NEXT_PUBLIC_CALCOM_*_AVATAR override.
   */
  photoUrl?: string
}

// Each env var is referenced literally — Next only inlines NEXT_PUBLIC_* vars
// into client bundles when it can see the full name at build time.
export const DEMO_HOSTS: DemoHost[] = [
  {
    id: 'mario',
    name: 'Mario',
    calLink: process.env.NEXT_PUBLIC_CALCOM_MARIO || 'mberberov/30min',
    photoUrl: process.env.NEXT_PUBLIC_CALCOM_MARIO_AVATAR
      || 'https://cal.com/api/avatar/aeb2bd45-d0cb-4968-b360-a076e6fb9bf8.png',
  },
  {
    id: 'egor',
    name: 'Egor',
    calLink: process.env.NEXT_PUBLIC_CALCOM_EGOR || 'esemenov/30min',
    photoUrl: process.env.NEXT_PUBLIC_CALCOM_EGOR_AVATAR
      || 'https://cal.com/api/avatar/9ab2cb02-4ed2-40c5-a213-f2df99dacdf3.png',
  },
  {
    id: 'stoyan',
    name: 'Stoyan',
    calLink: process.env.NEXT_PUBLIC_CALCOM_STOYAN || 'sivanov/30min',
    calOrigin: process.env.NEXT_PUBLIC_CALCOM_STOYAN_ORIGIN || 'https://cal.eu',
    photoUrl: process.env.NEXT_PUBLIC_CALCOM_STOYAN_AVATAR
      || 'https://cal.eu/api/avatar/526c41a5-725e-441e-8f11-d95953fe506c.png',
  },
]

export function initialsOf(name: string): string {
  return name.trim().charAt(0).toUpperCase() || '?'
}
