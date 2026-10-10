import { cn } from '@/lib/utils'
import type { AuthCopy } from './copy'

/**
 * Decorative right-hand panel for the auth split-screen: an animated tour of
 * what ERP Generator does. A cycling feature spotlight (16s loop, four 4s windows)
 * runs in sync with the generation-pipeline chips, above a floating
 * "generated ERP" preview card. Pure CSS animations (see theme.css,
 * `auth-*` keyframes) with a prefers-reduced-motion fallback.
 * aria-hidden — purely visual; the form column carries the real content.
 */
export function AuthShowcase({
  className,
  features,
}: {
  className?: string
  features: AuthCopy['showcase']['features']
}) {
  const stats = [
    { label: 'Open orders', value: '128' },
    { label: 'On-time', value: '96%' },
    { label: 'WIP value', value: '€48k' },
    { label: 'Suppliers', value: '34' },
  ]

  const rows = [
    { code: 'PO-1042', name: 'Steel sheets · 12t', status: 'Approved', tone: 'text-success' },
    { code: 'WO-3391', name: 'Frame assembly', status: 'In progress', tone: 'text-primary' },
    { code: 'INV-8820', name: 'Draft invoice', status: 'Pending', tone: 'text-muted-foreground' },
  ]

  const pipeline = ['Evidence', 'Design', 'Bundle', 'Live']

  return (
    <div
      aria-hidden
      className={cn(
        'relative overflow-hidden bg-gradient-to-br from-muted via-background to-muted',
        className,
      )}
    >
      {/* dot-grid texture */}
      <div
        className="absolute inset-0"
        style={{
          backgroundImage:
            'radial-gradient(circle, color-mix(in srgb, var(--foreground) 6%, transparent) 1px, transparent 1px)',
          backgroundSize: '22px 22px',
        }}
      />
      {/* soft brand glow */}
      <div className="absolute -right-24 -top-24 size-96 rounded-full bg-primary/10 blur-3xl" />
      <div className="absolute -bottom-32 left-0 size-96 rounded-full bg-primary/5 blur-3xl" />

      <div className="absolute inset-0 flex items-center justify-center p-10 xl:p-16">
        <div className="relative w-full max-w-md">
          {/* cycling feature spotlight */}
          <div className="auth-anim-rise relative mb-8 h-32" style={{ animationDelay: '0.1s' }}>
            {features.map((feature, index) => (
              <div
                key={feature.title}
                className="auth-feature-item absolute inset-0 flex flex-col gap-2"
                style={{ animationDelay: `${index * 4}s` }}
              >
                <p className="text-2xl font-semibold tracking-tight text-foreground">
                  {feature.title}
                </p>
                <p className="max-w-sm text-sm leading-relaxed text-muted-foreground">
                  {feature.caption}
                </p>
              </div>
            ))}
          </div>

          {/* main "generated ERP" card */}
          <div
            className="auth-anim-rise rotate-1 rounded-2xl border bg-card/90 p-6 shadow-xl backdrop-blur-sm"
            style={{ animationDelay: '0.25s' }}
          >
            <div className="flex items-center gap-3">
              <div className="flex size-9 items-center justify-center rounded-lg bg-primary text-primary-foreground">
                <svg viewBox="0 0 24 24" className="size-5" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M3 7l9-4 9 4-9 4-9-4z" />
                  <path d="M3 7v10l9 4 9-4V7" />
                  <path d="M12 11v10" />
                </svg>
              </div>
              <div className="min-w-0">
                <p className="truncate text-sm font-semibold text-foreground">Acme Manufacturing</p>
                <p className="truncate text-xs text-muted-foreground">Production ERP</p>
              </div>
              <span className="ml-auto inline-flex items-center gap-1.5 rounded-full bg-success/10 px-2.5 py-1 text-xs font-medium text-success">
                <span className="auth-dot size-1.5 rounded-full bg-success" />
                Live
              </span>
            </div>

            <div className="mt-5 grid grid-cols-2 gap-3">
              {stats.map((s, index) => (
                <div
                  key={s.label}
                  className="auth-anim-rise rounded-xl border bg-background/60 p-3"
                  style={{ animationDelay: `${0.4 + index * 0.08}s` }}
                >
                  <p className="text-[11px] uppercase tracking-wide text-muted-foreground">{s.label}</p>
                  <p className="mt-1 text-xl font-semibold tabular-nums text-foreground">{s.value}</p>
                </div>
              ))}
            </div>

            <div className="mt-5 space-y-2">
              {rows.map((r, index) => (
                <div
                  key={r.code}
                  className="auth-anim-rise flex items-center gap-3 rounded-lg border bg-background/60 px-3 py-2"
                  style={{ animationDelay: `${0.72 + index * 0.1}s` }}
                >
                  <span className="text-xs font-medium tabular-nums text-muted-foreground">{r.code}</span>
                  <span className="min-w-0 flex-1 truncate text-sm text-foreground">{r.name}</span>
                  <span className={cn('text-xs font-medium', r.tone)}>{r.status}</span>
                </div>
              ))}
            </div>
          </div>

          {/* pipeline chip, overlapping the main card — chips light up in sync
              with the feature spotlight above */}
          <div
            className="auth-anim-rise absolute -bottom-6 -left-6 -rotate-2 rounded-xl border bg-card p-4 shadow-lg"
            style={{ animationDelay: '1s' }}
          >
            <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Generation</p>
            <div className="mt-2.5 flex items-center gap-2">
              {pipeline.map((step, index) => (
                <div key={step} className="flex items-center gap-2">
                  <span
                    className="auth-chip inline-flex items-center rounded-md bg-muted px-2 py-1 text-xs font-medium text-muted-foreground"
                    style={{ animationDelay: `${index * 4}s` }}
                  >
                    {step}
                  </span>
                  {index < pipeline.length - 1 ? <span className="h-px w-4 bg-border" /> : null}
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
