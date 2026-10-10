const BOOKING_TRIGGER_URLS = new Set([
  '#book-demo',
  'book-demo-modal',
  'demo-booking-modal',
  '/book-demo',
])

export function isBookingTriggerUrl(url?: string | null) {
  if (!url) return false
  return BOOKING_TRIGGER_URLS.has(url.trim().toLowerCase())
}
