'use client'

import type { EventInput } from '@fullcalendar/core'
import dayGridPlugin from '@fullcalendar/daygrid'
import interactionPlugin from '@fullcalendar/interaction'
import type { DateClickArg } from '@fullcalendar/interaction'
import FullCalendar from '@fullcalendar/react'
import type { Ref } from 'react'

/**
 * The booking modal's month calendar, split out of DemoBookingProvider so the
 * heavy @fullcalendar stack is loaded lazily (next/dynamic) only when the
 * booking modal actually opens — the provider itself wraps every marketing
 * and account page, and a static import would drag FullCalendar into every
 * route's compile.
 */
export default function BookingCalendar({
  calendarRef,
  todayIsoDate,
  today,
  events,
  availabilityLoading,
  isDayUnavailable,
  onDateClick,
}: {
  calendarRef: Ref<FullCalendar>
  todayIsoDate: string
  today: Date
  events: EventInput[]
  availabilityLoading: boolean
  isDayUnavailable: (date: Date) => boolean
  onDateClick: (arg: DateClickArg) => void
}) {
  return (
    <FullCalendar
      ref={calendarRef}
      plugins={[dayGridPlugin, interactionPlugin]}
      initialView="dayGridMonth"
      height="auto"
      fixedWeekCount
      showNonCurrentDates
      validRange={{ start: todayIsoDate }}
      events={events}
      dateClick={onDateClick}
      dayCellClassNames={(arg) => {
        const classes: string[] = []

        if (availabilityLoading || arg.date < today) {
          classes.push('past-day')
        }

        if (isDayUnavailable(arg.date)) {
          classes.push('taken-day')
        }

        return classes
      }}
      headerToolbar={{
        left: 'prev,next',
        center: 'title',
        right: '',
      }}
    />
  )
}
