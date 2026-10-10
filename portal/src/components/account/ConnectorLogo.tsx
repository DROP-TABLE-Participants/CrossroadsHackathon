import { cn } from '@/lib/utils'

/**
 * Connector brand marks, served from `public/connectors/` as each vendor's own
 * full-colour logo. The files carry a viewBox and no intrinsic width/height, so
 * size is CSS-driven; `object-contain` keeps the non-square marks (SharePoint)
 * from stretching.
 */
const LOGO_FILES: Record<string, string> = {
  'google-drive': '/connectors/google-drive.svg',
  sharepoint: '/connectors/sharepoint.svg',
  'microsoft-teams': '/connectors/microsoft-teams.svg',
  notion: '/connectors/notion.svg',
  slack: '/connectors/slack.svg',
}

/** Accepts either a catalog key (`google-drive`) or a display title (`Google Drive`). */
function logoKey(name: string) {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-')
}

export function ConnectorLogo({ name, className }: { name: string; className?: string }) {
  const src = LOGO_FILES[logoKey(name)]
  if (!src) return null

  return (
    // eslint-disable-next-line @next/next/no-img-element -- static brand SVG; the image optimizer adds nothing.
    <img
      src={src}
      alt=""
      aria-hidden="true"
      className={cn('size-5 shrink-0 object-contain', className)}
    />
  )
}
