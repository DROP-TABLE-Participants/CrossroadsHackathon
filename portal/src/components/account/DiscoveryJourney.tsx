import { Warning as AlertTriangle, BracketsCurly as Braces, Plugs as Cable, Check, Question as CircleHelp, Rocket, ShieldCheck } from '@phosphor-icons/react'

import { cn } from '@/lib/utils'
import type { Locale } from '@/i18n/config'

type DiscoveryJourneyProps = {
  activeStep: number
  locale: Locale
}

type ConnectorHealthSummaryProps = {
  available: number
  connected: number
  locale: Locale
}

const journeyCopy = {
  en: {
    label: 'How your ERP is created',
    steps: [
      {
        title: 'Describe your business',
        description: 'Description, answers, and Notion',
        icon: Cable,
      },
      {
        title: 'Answer what is needed',
        description: 'Only targeted questions',
        icon: CircleHelp,
      },
      {
        title: 'Generate and test ERP',
        description: 'Business pages, records, and checks',
        icon: Braces,
      },
      {
        title: 'Open your ERP',
        description: 'Your tested presentation sandbox',
        icon: ShieldCheck,
      },
    ],
  },
  bg: {
    label: 'Как се създава вашият ERP',
    steps: [
      {
        title: 'Опишете бизнеса',
        description: 'Описание, отговори и Notion',
        icon: Cable,
      },
      {
        title: 'Отговорете при нужда',
        description: 'Само целеви въпроси',
        icon: CircleHelp,
      },
      {
        title: 'Генериране и тестове',
        description: 'Бизнес страници, записи и проверки',
        icon: Braces,
      },
      {
        title: 'Отворете вашето ERP',
        description: 'Проверена среда за презентация',
        icon: ShieldCheck,
      },
    ],
  },
} as const

export function DiscoveryJourney({ activeStep, locale }: DiscoveryJourneyProps) {
  const copy = journeyCopy[locale]

  return (
    <ol
      aria-label={copy.label}
      className="grid gap-3 rounded-lg border bg-muted/30 p-3 sm:grid-cols-4 sm:gap-2"
    >
      {copy.steps.map((step, index) => {
        const isComplete = index < activeStep
        const isActive = index === activeStep

        return (
          <li
            className="flex items-start gap-2.5"
            key={step.title}
            aria-current={isActive ? 'step' : undefined}
          >
            <span
              aria-hidden="true"
              className={cn(
                'flex size-7 shrink-0 items-center justify-center rounded-full border text-xs font-semibold',
                isComplete && 'border-primary bg-primary text-primary-foreground',
                isActive && 'border-primary text-primary',
                !isComplete && !isActive && 'border-border bg-background text-muted-foreground',
              )}
            >
              {isComplete ? <Check className="size-3.5" /> : index + 1}
            </span>
            <span className="flex min-w-0 flex-col gap-0.5">
              <strong
                className={cn(
                  'text-xs font-semibold leading-tight',
                  isActive ? 'text-foreground' : isComplete ? 'text-foreground/80' : 'text-muted-foreground',
                )}
              >
                {step.title}
              </strong>
              <small className="text-[11px] leading-tight text-muted-foreground">{step.description}</small>
            </span>
          </li>
        )
      })}
    </ol>
  )
}

export function ConnectorHealthSummary({
  available,
  connected,
  locale,
}: ConnectorHealthSummaryProps) {
  const copy = locale === 'bg'
    ? {
        label: 'Състояние на конекторите',
        connected: 'Свързани',
        available: 'Налични сега',
      }
    : {
        label: 'Connector health',
        connected: 'Connected',
        available: 'Available now',
      }

  return (
    <div aria-label={copy.label} className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground">
      <span className="inline-flex items-center gap-1.5">
        <i aria-hidden="true" className="size-2 rounded-full bg-primary" />
        {copy.connected} <strong className="text-foreground">{connected}</strong>
      </span>
      <span className="inline-flex items-center gap-1.5">
        <i aria-hidden="true" className="size-2 rounded-full bg-muted-foreground/50" />
        {copy.available} <strong className="text-foreground">{available}</strong>
      </span>
    </div>
  )
}

export function DiscoveryPrimaryActionCopy({ locale }: { locale: Locale }) {
  const copy = locale === 'bg'
    ? {
        eyebrow: 'Следваща стъпка',
        title: 'Нека ERP Generator изгради вашия ERP',
        description: 'GO стартира discovery, задава само нужните въпроси и генерира тестван ERP в стейджинг.',
      }
    : {
        eyebrow: 'Next step',
        title: 'Let ERP Generator build your ERP',
        description: 'GO starts discovery, asks only what the evidence cannot answer, and generates a tested ERP.',
      }

  return (
    <div className="flex flex-col gap-1">
      <span className="text-xs font-semibold tracking-wide text-primary uppercase">{copy.eyebrow}</span>
      <strong className="text-sm font-semibold">{copy.title}</strong>
      <p className="text-sm text-muted-foreground">{copy.description}</p>
    </div>
  )
}

export function DiscoveryWorkingCopy({ locale }: { locale: Locale }) {
  const copy = locale === 'bg'
    ? 'ERP Generator продължава по discovery, генериране и тестове.'
    : 'ERP Generator is progressing through discovery, generation, and tests.'

  return (
    <span className="inline-flex items-center gap-2 text-sm text-muted-foreground">
      <Rocket className="size-4 shrink-0 text-primary" aria-hidden="true" />
      {copy}
    </span>
  )
}

export function DiscoveryTerminalIncidentCopy({ locale }: { locale: Locale }) {
  const copy = locale === 'bg'
    ? 'Генерирането спря преди проверката на вашето ERP. Информацията ви е запазена и подготвеното демо остава достъпно.'
    : 'Generation stopped before your ERP could be verified. Your evidence is saved and your prepared demo remains available.'

  return (
    <span className="inline-flex items-center gap-2 text-sm text-muted-foreground" role="status">
      <AlertTriangle className="size-4 shrink-0 text-destructive" aria-hidden="true" />
      {copy}
    </span>
  )
}
