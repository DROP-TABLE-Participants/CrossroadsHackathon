import { Badge } from '@/components/ui/badge'

export function AdminStatusBadge({
  status,
  label,
}: {
  status: string
  label?: string
}) {
  const normalized = status.toLowerCase()
  const variant = normalized === 'failed' || normalized.includes('error')
    ? 'destructive'
    : normalized === 'active' || normalized === 'активен' || normalized === 'generated' || normalized === 'sent' || normalized === 'completed'
      ? 'default'
      : 'secondary'
  return <Badge variant={variant}>{label || status.replaceAll('_', ' ')}</Badge>
}
