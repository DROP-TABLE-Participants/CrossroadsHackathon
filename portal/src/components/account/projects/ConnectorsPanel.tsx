'use client'

import { useEffect, useState } from 'react'
import { WarningCircle as AlertCircle, CaretDown, ArrowSquareOut as ExternalLink, Lock, Plug, ArrowsClockwise as RefreshCw, Trash } from '@phosphor-icons/react'

import { Alert, AlertDescription } from '@/components/ui/alert'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from '@/components/ui/collapsible'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { cn } from '@/lib/utils'
import { ConnectorLogo } from '@/components/account/ConnectorLogo'
import type { ProjectsApi } from '@/components/account/api-types'
import { ConnectorConsentDialog } from '@/components/account/projects/ConnectorConsentDialog'
import { ConnectorScopeDialog } from '@/components/account/projects/ConnectorScopeDialog'
import {
  connectorOptions,
  connectorSources,
  connectorStatusLabel,
  connectorWithSourceState,
  formatDate,
  sourceHealthLabel,
  sourceProviderLabel,
} from '@/components/account/helpers'
import type { AccountLabels } from '@/components/account/labels'
import type {
  ConnectorOption,
  GenerationProjectSummary,
} from '@/components/account/types'
import type { Locale } from '@/i18n/config'

type ConnectorsPanelProps = {
  labels: AccountLabels
  locale: Locale
  project: GenerationProjectSummary
  projectsApi: ProjectsApi
  /** Generation is holding this project's evidence — adding sources is paused. */
  locked?: boolean
}

const minimumCheckingFeedbackMs = 450

function connectorBadgeVariant(status: ConnectorOption['status']) {
  if (status === 'connected') return 'default' as const
  if (status === 'failed') return 'destructive' as const
  return 'secondary' as const
}

/** Maps a source's engine status onto the same vocabulary the catalog uses. */
function sourceConnectorStatus(
  source: NonNullable<GenerationProjectSummary['inputSources']>[number],
): ConnectorOption['status'] {
  const status = source.status || 'connected'
  if (['requires_reconnect', 'connection_failed', 'error'].includes(status)) return 'failed'
  if (status === 'connecting' || status === 'draft') return 'connecting'
  return 'connected'
}

/**
 * Company data connectors: connected sources at a glance, a health summary,
 * and an inline collapsible catalog for adding or managing connectors. The
 * catalog expands in place inside the card — a floating popover would cover the
 * project's primary action when it auto-opens for a project with no sources.
 */
export function ConnectorsPanel({ labels, locale, project, projectsApi, locked }: ConnectorsPanelProps) {
  const [checkingSourceId, setCheckingSourceId] = useState('')
  const [connectorToAuthorize, setConnectorToAuthorize] = useState<ConnectorOption | null>(null)
  const [scopeSource, setScopeSource] = useState<NonNullable<GenerationProjectSummary['inputSources']>[number] | null>(null)
  const {
    pendingAction,
    connectorSearches,
    setConnectorSearches,
    connectorCatalogsOpen,
    setConnectorCatalogsOpen,
    connectorErrors,
    connectorResources,
    connectNangoInputSource,
    loadConnectorResources,
    clearConnectorError,
    updateInputSourceSelection,
    reconcileInputSources,
    refreshInputSource,
    disconnectInputSource,
  } = projectsApi

  const allowedIntegrations = project.availableConnectors || []
  const connectorSearch = connectorSearches[project.id] || ''
  const allConnectors = connectorOptions(labels, allowedIntegrations, '')
    .map((connector) => connectorWithSourceState(project, connector))
  const connectors = connectorOptions(labels, allowedIntegrations, connectorSearch)
    .map((connector) => connectorWithSourceState(project, connector))
  const catalogOpen = connectorCatalogsOpen[project.id] ?? !project.inputSources?.length
  const scopeSourceStatus = project.inputSources?.find((source) => source.id === scopeSource?.id)?.status

  useEffect(() => {
    if (scopeSourceStatus && ['requires_reconnect', 'connection_failed', 'error'].includes(scopeSourceStatus))
      setScopeSource(null)
  }, [scopeSourceStatus])

  const checkSource = async (sourceId: string) => {
    if (checkingSourceId) return
    const startedAt = Date.now()
    setCheckingSourceId(sourceId)
    try {
      const source = project.inputSources?.find((item) => item.id === sourceId)
      if (source && ['connecting', 'draft'].includes(source.status || ''))
        await reconcileInputSources(project.id)
      else
        await refreshInputSource(project.id, sourceId)
    } finally {
      const remaining = minimumCheckingFeedbackMs - (Date.now() - startedAt)
      if (remaining > 0) {
        await new Promise((resolve) => window.setTimeout(resolve, remaining))
      }
      setCheckingSourceId('')
    }
  }

  const openScope = (source: NonNullable<GenerationProjectSummary['inputSources']>[number]) => {
    clearConnectorError(project.id)
    setScopeSource(source)
  }

  return (
    <div data-slot="connectors-panel" className="flex flex-col gap-3">
      <div className="flex flex-col gap-1">
        <h3 className="m-0 text-base font-semibold">{labels.connectCompanyData}</h3>
        <p className="m-0 text-xs text-muted-foreground">{labels.connectorHelper}</p>
      </div>
      {connectorErrors[project.id] ? (
        <Alert variant="destructive">
          <AlertCircle />
          <AlertDescription>{connectorErrors[project.id]}</AlertDescription>
        </Alert>
      ) : null}
      {project.inputSources?.length ? (
        <div className="grid gap-2">
          {project.inputSources.slice(0, 4).map((source) => {
            const providerName = sourceProviderLabel(labels, source.provider)
            const displayName = source.displayName || providerName
            const health = sourceHealthLabel(labels, source)
            const showProviderName = displayName.localeCompare(providerName, undefined, { sensitivity: 'accent' }) !== 0
            const showHealth = !['connected', 'ready'].includes(health.trim().toLowerCase())
            const checking = checkingSourceId === source.id || pendingAction === `refresh-source:${source.id}`
            const authorizationPending = ['connecting', 'draft'].includes(source.status || '')
            const needsReconnect = ['requires_reconnect', 'connection_failed', 'error']
              .includes(source.status || '')
            const needsSelection = !needsReconnect && !authorizationPending &&
              (source.requiresSelection || source.status === 'awaiting_scope')
            const syncFailed = source.status === 'sync_failed'
            const sourceConnector = allConnectors.find((connector) =>
              connectorSources(project, connector)?.some((candidate) => candidate.id === source.id))
            const badgeLabel = needsSelection
              ? labels.connectorScopeRequired
              : source.status === 'syncing'
                ? labels.syncingSource
                : syncFailed
                  ? labels.connectorSyncFailed
                  : needsReconnect
                    ? labels.connectorNeedsReconnect
                    : connectorStatusLabel(labels, sourceConnectorStatus(source))

            return (
              <article
                data-slot="connected-source"
                className="flex flex-col gap-3 rounded-lg border bg-muted/20 p-3 sm:flex-row sm:items-center"
                key={source.id}
              >
                <div className="flex min-w-0 flex-1 items-center gap-3">
                  <span className="flex size-10 shrink-0 items-center justify-center rounded-lg border bg-background">
                    <ConnectorLogo name={source.provider || providerName} className="size-6" />
                  </span>
                  <div className="flex min-w-0 flex-col gap-0.5">
                    <strong className="truncate text-sm font-semibold">{displayName}</strong>
                    {showProviderName ? (
                      <span className="truncate text-xs text-muted-foreground">{providerName}</span>
                    ) : null}
                    <ul className="m-0 flex list-none flex-wrap gap-x-3 gap-y-0.5 p-0 text-xs text-muted-foreground">
                      {typeof source.lastRecordCount === 'number' ? (
                        <li>{labels.sourceRecords}: {source.lastRecordCount}</li>
                      ) : null}
                      {typeof source.lastTextRecordCount === 'number' ? (
                        <li>{labels.textRecords}: {source.lastTextRecordCount}</li>
                      ) : null}
                      {source.lastCheckedAt ? (
                        <li>{labels.lastChecked}: {formatDate(locale, source.lastCheckedAt)}</li>
                      ) : null}
                      {showHealth ? <li>{health}</li> : null}
                      {source.status === 'syncing' && source.syncProcessedCount ? (
                        <li>
                          {source.syncProcessedCount} synchronized
                          {source.syncFailedCount ? ` · ${source.syncFailedCount} unavailable` : ''}
                        </li>
                      ) : null}
                    </ul>
                  </div>
                </div>
                <Badge
                  variant={needsReconnect || syncFailed
                    ? 'destructive'
                    : connectorBadgeVariant(sourceConnectorStatus(source))}
                  className="w-fit shrink-0"
                >
                  {badgeLabel}
                </Badge>
                <div className="flex shrink-0 flex-wrap gap-2">
                  {source.provider === 'notion' && !needsReconnect && !authorizationPending ? (
                    <Button
                      type="button"
                      size="sm"
                      variant={needsSelection ? 'default' : 'outline'}
                      onClick={() => openScope(source)}
                      disabled={pendingAction === `load-source-resources:${source.id}`}
                    >
                      {pendingAction === `load-source-resources:${source.id}`
                        ? <Spinner data-icon="inline-start" aria-hidden="true" />
                        : <Plug data-icon="inline-start" />}
                      {needsSelection ? labels.selectConnectorContent : labels.manageConnectorContent}
                    </Button>
                  ) : null}
                  {(needsReconnect || authorizationPending) && sourceConnector ? (
                    <Button
                      type="button"
                      size="sm"
                      onClick={() => setConnectorToAuthorize(sourceConnector)}
                      disabled={pendingAction === `connect-source:${project.id}:${sourceConnector.key}`}
                    >
                      <ExternalLink data-icon="inline-start" />
                      {labels.reconnectSource}
                    </Button>
                  ) : null}
                  {!needsSelection && !needsReconnect ? (
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => checkSource(source.id)}
                      disabled={Boolean(checkingSourceId) || source.status === 'syncing'}
                      aria-busy={checking}
                    >
                      {checking ? (
                        <Spinner data-icon="inline-start" aria-hidden="true" />
                      ) : (
                        <RefreshCw data-icon="inline-start" />
                      )}
                      {checking ? labels.checkingSource : labels.checkSource}
                    </Button>
                  ) : null}
                  <AlertDialog>
                    <AlertDialogTrigger asChild>
                      <Button type="button" size="sm" variant="ghost" aria-label={labels.disconnectSource}>
                        <Trash aria-hidden="true" />
                      </Button>
                    </AlertDialogTrigger>
                    <AlertDialogContent>
                      <AlertDialogHeader>
                        <AlertDialogTitle>{labels.disconnectSourceConfirmTitle}</AlertDialogTitle>
                        <AlertDialogDescription>{labels.disconnectSourceConfirmDescription}</AlertDialogDescription>
                      </AlertDialogHeader>
                      <AlertDialogFooter>
                        <AlertDialogCancel>{labels.cancel}</AlertDialogCancel>
                        <AlertDialogAction
                          onClick={() => disconnectInputSource(project.id, source.id)}
                          disabled={pendingAction === `disconnect-source:${source.id}`}
                        >
                          {labels.disconnectSource}
                        </AlertDialogAction>
                      </AlertDialogFooter>
                    </AlertDialogContent>
                  </AlertDialog>
                </div>
              </article>
            )
          })}
        </div>
      ) : (
        <p className="m-0 text-sm text-muted-foreground">{labels.noConnectedSources}</p>
      )}
      {/* While a run holds this project, the catalog is withheld rather than
          shown-but-dead: changing the evidence mid-analysis would either be
          silently ignored or invalidate the run in progress. */}
      {locked ? (
        <p className="flex items-start gap-2 rounded-md border bg-muted/30 p-3 text-xs text-muted-foreground">
          <Lock aria-hidden="true" className="mt-0.5 size-3.5 shrink-0" />
          {labels.connectorsLockedDuringGeneration}
        </p>
      ) : (
      <Collapsible
        open={catalogOpen}
        onOpenChange={(open) => {
          setConnectorCatalogsOpen((current) => current[project.id] === open
            ? current
            : { ...current, [project.id]: open })
        }}
        className="flex flex-col gap-3"
      >
        {/* Says what the next click does, and shows which way it will go.
            "Add or manage connectors" never changed when the list opened, so
            the only way to know its state was to look below it. */}
        <CollapsibleTrigger asChild>
          <Button type="button" variant="outline" size="sm" className="w-fit">
            <Plug data-icon="inline-start" />
            {catalogOpen ? labels.hideDataSources : labels.addDataSource}
            <CaretDown
              data-icon="inline-end"
              aria-hidden="true"
              className={cn('transition-transform', catalogOpen && 'rotate-180')}
            />
          </Button>
        </CollapsibleTrigger>
        <CollapsibleContent className="flex flex-col gap-3">
          {allConnectors.length > 1 && <Input
            type="search"
            placeholder={labels.searchConnectors}
            aria-label={labels.searchConnectors}
            value={connectorSearch}
            onChange={(event) => setConnectorSearches((current) => ({
              ...current,
              [project.id]: event.target.value,
            }))}
          />}
          <div className="grid gap-2">
            {connectors.map((connector) => {
              const connecting = pendingAction === `connect-source:${project.id}:${connector.key}`
              const disabled = !['available', 'failed'].includes(connector.status) || connecting
              const buttonText = connecting || connector.status === 'connecting'
                ? labels.connectingDataSource
                : connector.status === 'connected'
                  ? labels.connectorConnected
                  : connector.status === 'failed'
                    ? labels.reconnectSource
                  : labels.connectDataSource

              return (
                <article
                  data-slot="connector-card"
                  className={cn(
                    'flex flex-col gap-2 rounded-md border p-3',
                    disabled && connector.status !== 'connected' && 'opacity-80',
                  )}
                  key={connector.key}
                >
                  <div className="flex items-start justify-between gap-2">
                    <div className="flex min-w-0 items-center gap-2">
                      <ConnectorLogo name={connector.title} />
                      <div className="flex min-w-0 flex-col">
                        <strong className="text-sm font-medium">{connector.title}</strong>
                        <span className="text-xs text-muted-foreground">{connector.description}</span>
                      </div>
                    </div>
                    <Badge variant={connectorBadgeVariant(connector.status)}>
                      {connectorStatusLabel(labels, connector.status)}
                    </Badge>
                  </div>
                  <div>
                    <Button
                      type="button"
                      size="sm"
                      variant={connector.status === 'connected' ? 'outline' : 'default'}
                      onClick={() => setConnectorToAuthorize(connector)}
                      disabled={disabled}
                    >
                      {connecting ? (
                        <Spinner data-icon="inline-start" />
                      ) : (
                        <ExternalLink data-icon="inline-start" />
                      )}
                      {buttonText}
                    </Button>
                  </div>
                </article>
              )
            })}
          </div>
        </CollapsibleContent>
      </Collapsible>
      )}
      <ConnectorConsentDialog
        labels={labels}
        connector={connectorToAuthorize}
        busy={Boolean(connectorToAuthorize &&
          pendingAction === `connect-source:${project.id}:${connectorToAuthorize.key}`)}
        onOpenChange={(open) => {
          if (!open) setConnectorToAuthorize(null)
        }}
        onConfirm={async (connector) => {
          await connectNangoInputSource(project, connector)
          setConnectorToAuthorize(null)
        }}
      />
      <ConnectorScopeDialog
        key={scopeSource?.id || 'closed'}
        labels={labels}
        source={scopeSource}
        resources={scopeSource ? connectorResources[scopeSource.id] || [] : []}
        loading={Boolean(scopeSource &&
          pendingAction === `load-source-resources:${scopeSource.id}`)}
        saving={Boolean(scopeSource &&
          pendingAction === `save-source-selection:${scopeSource.id}`)}
        onOpenChange={(open) => {
          if (!open) setScopeSource(null)
        }}
        onLoadResources={() => scopeSource
          ? loadConnectorResources(project.id, scopeSource.id).then(() => undefined)
          : Promise.resolve()}
        onSave={(scope) => scopeSource
          ? updateInputSourceSelection(project.id, scopeSource.id, scope)
          : Promise.resolve(false)}
      />
    </div>
  )
}
