import { useCallback, useEffect, useState } from 'react'

import { awaitingConnectionProjectIds, inviteDeliveryFeedback } from '@/components/account/helpers'
import type { AccountLabels } from '@/components/account/labels'
import { useProjectRealtime } from '@/components/account/hooks/useProjectRealtime'
import type {
  ConnectorResourceSummary,
  ConnectorScopeInput,
  ConnectorOption,
  GenerationProjectSummary,
  GenerationRunSummary,
  OneTimeActivationLink,
} from '@/components/account/types'
import {
  clearAuthSession,
  hasCustomerSessionHint,
  shouldClearAuthSessionForCustomerProfileStatus,
} from '@/components/booking/customer-session'
import type { CustomerSession } from '@/components/booking/customer-session'

type UseGenerationProjectsParams = {
  labels: AccountLabels
  openLogin: () => void
  customer: CustomerSession | null
}

/**
 * Outcome of submitting an answer / evidence for a pending work item. `resumed`
 * is true when the submission unblocked the run and generation picked back up on
 * its own — the caller uses it to tailor the confirmation the owner sees.
 */
export type PendingWorkResult = { ok: boolean; resumed: boolean; message?: string }

function withInputSourceReadiness(
  project: GenerationProjectSummary,
  inputSources: NonNullable<GenerationProjectSummary['inputSources']>,
): GenerationProjectSummary {
  const generationBlockedReason = inputSources
    .map((source) => source.generationBlockReason)
    .find((reason): reason is string => Boolean(reason))
  return {
    ...project,
    inputSources,
    inputSourceCount: inputSources.length,
    generationBlockedReason,
    canStartGeneration: !project.generationRuns?.length && !generationBlockedReason,
  }
}

export function useGenerationProjects({ labels, openLogin, customer }: UseGenerationProjectsParams) {
  const [projects, setProjects] = useState<GenerationProjectSummary[]>([])
  const [projectsLoading, setProjectsLoading] = useState(false)
  const [projectCreating, setProjectCreating] = useState(false)
  const [generationStarting, setGenerationStarting] = useState('')
  const [pendingAction, setPendingAction] = useState('')
  const [pendingEvidenceDrafts, setPendingEvidenceDrafts] = useState<Record<string, { title: string; text: string }>>({})
  const [pendingResolutionNotes, setPendingResolutionNotes] = useState<Record<string, string>>({})
  const [connectorSearches, setConnectorSearches] = useState<Record<string, string>>({})
  const [connectorCatalogsOpen, setConnectorCatalogsOpen] = useState<Record<string, boolean>>({})
  const [connectorErrors, setConnectorErrors] = useState<Record<string, string>>({})
  const [connectorResources, setConnectorResources] = useState<Record<string, ConnectorResourceSummary[]>>({})
  const [projectCreateOpen, setProjectCreateOpen] = useState(true)
  const [projectError, setProjectError] = useState('')
  const [projectFeedback, setProjectFeedback] = useState('')
  const [activationLinks, setActivationLinks] = useState<Record<string, OneTimeActivationLink>>({})
  const [projectIndustry, setProjectIndustry] = useState('')
  const [projectCompanyType, setProjectCompanyType] = useState('')
  const [projectGoal, setProjectGoal] = useState('')
  const [projectLeadership, setProjectLeadership] = useState('')
  const [inviteLinks, setInviteLinks] = useState<Record<string, string>>({})

  const expireCustomerSession = useCallback(() => {
    clearAuthSession()
    setProjects([])
    setProjectError('')
    setProjectFeedback('')
    openLogin()
  }, [openLogin])

  const refreshProjects = useCallback(async () => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      setProjects([])
      return
    }

    setProjectsLoading(true)
    setProjectError('')

    try {
      const response = await fetch('/api/customers/generation-projects', {
        method: 'GET',
        headers: {
        },
      })

      if (!response.ok) {
        if (shouldClearAuthSessionForCustomerProfileStatus(response.status)) {
          expireCustomerSession()
          return
        }

        const data = await response.json().catch(() => ({}))
        setProjectError(data?.message || 'Unable to load discovery projects.')
        return
      }

      const data = await response.json()
      const nextProjects = Array.isArray(data?.projects) ? data.projects : []
      setProjects(nextProjects)
      setProjectCreateOpen(nextProjects.length === 0)
    } catch {
      setProjectError('Unable to load discovery projects.')
    } finally {
      setProjectsLoading(false)
    }
  }, [expireCustomerSession])

  const refreshProject = useCallback(async (projectId: string, options: { quiet?: boolean } = {}) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      setProjects([])
      return false
    }

    if (!options.quiet) setProjectError('')

    try {
      const response = await fetch(`/api/customers/generation-projects/${encodeURIComponent(projectId)}`, {
        method: 'GET',
        headers: {
        },
      })

      if (!response.ok) {
        if (shouldClearAuthSessionForCustomerProfileStatus(response.status)) {
          expireCustomerSession()
          return false
        }

        if (!options.quiet && response.status < 500) {
          const data = await response.json().catch(() => ({}))
          setProjectError(data?.message || 'Unable to load discovery project.')
        }
        return false
      }

      const data = await response.json()
      if (data?.project) {
        setProjects((current) => current.map((project) => (project.id === projectId ? data.project : project)))
      }
      return true
    } catch {
      if (!options.quiet) setProjectError('Unable to load discovery project.')
      return false
    }
  }, [expireCustomerSession])

  const refreshRealtimeProject = useCallback(
    (projectId: string) => refreshProject(projectId, { quiet: true }),
    [refreshProject],
  )
  /**
   * Settles sources stuck mid-connect by asking Nango what actually happened.
   * `quiet` is used by the return-to-Portal reconciliation so a transient
   * Nango hiccup never throws an error banner at someone who did nothing wrong.
   */
  const reconcileInputSources = useCallback(async (projectId: string, options: { quiet?: boolean } = {}) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) return false

    try {
      const response = await fetch(
        `/api/customers/generation-projects/${encodeURIComponent(projectId)}/input-sources/reconcile`,
        { method: 'POST' },
      )
      if (!response.ok) {
        if (!options.quiet) {
          const data = await response.json().catch(() => ({}))
          setConnectorErrors((current) => ({
            ...current,
            [projectId]: data?.message || labels.connectorCheckFailed,
          }))
        }
        return false
      }

      const data = await response.json().catch(() => ({}))
      if (Array.isArray(data?.sources)) {
        setProjects((current) => current.map((project) => (
          project.id === projectId
            ? withInputSourceReadiness(project, data.sources)
            : project
        )))
      }
      return true
    } catch {
      if (!options.quiet) {
        setConnectorErrors((current) => ({ ...current, [projectId]: labels.connectorCheckFailed }))
      }
      return false
    }
  }, [labels.connectorCheckFailed])

  const refreshGuestProjects = useCallback(async () => {
    await Promise.all(awaitingConnectionProjectIds(projects)
      .map((projectId) => reconcileInputSources(projectId, { quiet: true })))
    await refreshProjects()
  }, [projects, reconcileInputSources, refreshProjects])

  useProjectRealtime({
    enabled: Boolean(customer),
    polling: customer?.isGuest === true,
    onProjectChanged: refreshRealtimeProject,
    onProjectRemoved: customer?.isGuest === true ? refreshGuestProjects : refreshProjects,
    onSessionExpired: expireCustomerSession,
  })

  // Shared development receives signed Nango webhooks through the Portal. For
  // fully local use, reconcile once when the customer returns from Nango.
  useEffect(() => {
    if (!customer) return
    const projectIds = awaitingConnectionProjectIds(projects)
    if (!projectIds.length) return

    let checking = false
    const reconcileAfterReturn = async () => {
      if (checking || document.visibilityState === 'hidden') return
      checking = true
      await Promise.all(projectIds.map((projectId) => reconcileInputSources(projectId, { quiet: true })))
      checking = false
    }

    const reconcileWhenVisible = () => {
      if (document.visibilityState === 'visible') void reconcileAfterReturn()
    }
    window.addEventListener('focus', reconcileAfterReturn)
    document.addEventListener('visibilitychange', reconcileWhenVisible)
    return () => {
      window.removeEventListener('focus', reconcileAfterReturn)
      document.removeEventListener('visibilitychange', reconcileWhenVisible)
    }
  }, [customer, projects, reconcileInputSources])

  const startGeneration = useCallback(async (projectId: string) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return
    }

    setGenerationStarting(projectId)
    setProjectError('')
    setProjectFeedback('')

    try {
      const response = await fetch(`/api/customers/generation-projects/${encodeURIComponent(projectId)}/start-generation`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          deployToCustomerStaging: true,
        }),
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        setProjectError(data?.message || 'Unable to start generation.')
        return
      }

      if (data?.project) {
        setProjects((current) => current.map((project) => (project.id === projectId ? data.project : project)))
      } else {
        await refreshProjects()
      }
      setProjectFeedback(data?.project?.status === 'waiting_for_evidence'
        ? 'Your request is saved. Generation will start automatically when company data is ready.'
        : 'Generation started.')
    } catch {
      setProjectError('Unable to start generation.')
    } finally {
      setGenerationStarting('')
    }
  }, [openLogin, refreshProjects])

  const generateWithAssumptions = useCallback(async (projectId: string) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return
    }

    const actionKey = `generate-with-assumptions:${projectId}`
    setPendingAction(actionKey)
    setProjectError('')
    setProjectFeedback('')
    try {
      const response = await fetch(
        `/api/customers/generation-projects/${encodeURIComponent(projectId)}/generate-with-assumptions`,
        { method: 'POST' },
      )
      const data = await response.json().catch(() => ({}))
      if (!response.ok) {
        setProjectError(data?.message || 'Unable to continue generation with assumptions.')
        return
      }
      if (data?.project) {
        setProjects((current) => current.map((project) => project.id === projectId ? data.project : project))
      } else {
        await refreshProjects()
      }
      setProjectFeedback('Generation continued with explicit assumptions.')
    } catch {
      setProjectError('Unable to continue generation with assumptions.')
    } finally {
      setPendingAction('')
    }
  }, [openLogin, refreshProjects])

  const sendInterviewInvite = useCallback(async (requestId: string) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return
    }

    const actionKey = `invite:${requestId}`
    setPendingAction(actionKey)
    setProjectError('')
    setProjectFeedback('')

    try {
      const response = await fetch(`/api/customers/interview-requests/${encodeURIComponent(requestId)}/send-invite`, {
        method: 'POST',
        headers: {
        },
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        setProjectError(data?.message || 'Unable to send interview invite.')
        return
      }

      if (data?.requestId && data?.inviteLink) {
        setInviteLinks((current) => ({ ...current, [data.requestId]: data.inviteLink }))
      }
      setProjectFeedback(inviteDeliveryFeedback(labels, data?.inviteDelivery))
      await refreshProjects()
    } catch {
      setProjectError('Unable to send interview invite.')
    } finally {
      setPendingAction('')
    }
  }, [labels, openLogin, refreshProjects])

  /** Resolves true when the link actually reached the clipboard, so the caller
   *  can confirm it to the customer instead of silently succeeding. */
  const copyInterviewInvite = useCallback(async (requestId: string, existingInviteLink?: string) => {
    if (existingInviteLink) {
      await navigator.clipboard?.writeText(existingInviteLink)
      return true
    }

    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return false
    }

    const actionKey = `copy-invite:${requestId}`
    setPendingAction(actionKey)
    setProjectError('')

    try {
      const response = await fetch(`/api/customers/interview-requests/${encodeURIComponent(requestId)}/copy-link`, {
        method: 'POST',
      })
      const data = await response.json().catch(() => ({}))
      if (!response.ok || !data?.inviteLink) {
        setProjectError(data?.message || 'Unable to copy interview invite.')
        return false
      }

      setInviteLinks((current) => ({ ...current, [requestId]: data.inviteLink }))
      await navigator.clipboard?.writeText(data.inviteLink)
      await refreshProjects()
      return true
    } catch {
      setProjectError('Unable to copy interview invite.')
      return false
    } finally {
      setPendingAction('')
    }
  }, [openLogin, refreshProjects])

  const addPendingEvidenceDocument = useCallback(async (projectId: string, item: GenerationRunSummary['pendingWorkItems'][number]): Promise<PendingWorkResult> => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return { ok: false, resumed: false }
    }

    const draft = pendingEvidenceDrafts[item.id] || { title: '', text: '' }
    const actionKey = `evidence:${item.id}`
    setPendingAction(actionKey)
    setProjectError('')
    setProjectFeedback('')

    try {
      const response = await fetch(`/api/customers/pending-work-items/${encodeURIComponent(item.id)}/add-evidence-document`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          title: draft.title || item.question || item.reason || 'Requested evidence',
          text: draft.text,
          sourceKind: 'manual',
          sourceName: 'Pending work evidence',
          contentType: 'markdown',
        }),
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        const message = data?.message || 'Unable to add evidence document.'
        setProjectError(message)
        return { ok: false, resumed: false, message }
      }

      setPendingEvidenceDrafts((current) => {
        const next = { ...current }
        delete next[item.id]
        return next
      })
      const resumed = Boolean(data?.autoResume?.resumed)
      setProjectFeedback(resumed ? 'Continuing generation...' : 'Evidence added.')
      await refreshProject(projectId)
      return { ok: true, resumed }
    } catch {
      const message = 'Unable to add evidence document.'
      setProjectError(message)
      return { ok: false, resumed: false, message }
    } finally {
      setPendingAction('')
    }
  }, [openLogin, pendingEvidenceDrafts, refreshProject])

  const resolvePendingWork = useCallback(async (projectId: string, item: GenerationRunSummary['pendingWorkItems'][number]): Promise<PendingWorkResult> => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return { ok: false, resumed: false }
    }

    const actionKey = `resolve:${item.id}`
    setPendingAction(actionKey)
    setProjectError('')
    setProjectFeedback('')

    try {
      const response = await fetch(`/api/customers/pending-work-items/${encodeURIComponent(item.id)}/resolve`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify({ resolutionNote: pendingResolutionNotes[item.id] || '' }),
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        const message = data?.message || 'Unable to resolve pending work.'
        setProjectError(message)
        return { ok: false, resumed: false, message }
      }

      const resumed = Boolean(data?.autoResume?.resumed)
      setProjectFeedback(resumed ? 'Continuing generation...' : 'Pending work resolved.')
      setPendingResolutionNotes((current) => {
        const next = { ...current }
        delete next[item.id]
        return next
      })
      await refreshProject(projectId)
      return { ok: true, resumed }
    } catch {
      const message = 'Unable to resolve pending work.'
      setProjectError(message)
      return { ok: false, resumed: false, message }
    } finally {
      setPendingAction('')
    }
  }, [openLogin, pendingResolutionNotes, refreshProject])

  const promoteProduction = useCallback(async (runId: string) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return
    }

    const actionKey = `promote-production:${runId}`
    setPendingAction(actionKey)
    setProjectError('')
    setProjectFeedback('')

    try {
      const response = await fetch(`/api/customers/generation-runs/${encodeURIComponent(runId)}/promote-production`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify({ confirm: true }),
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        setProjectError(data?.message || 'Unable to promote ERP to production.')
        return
      }

      if (data?.activation?.activationUrl) {
        setActivationLinks((current) => ({
          ...current,
          [`${runId}:production`]: data.activation,
        }))
        setProjectFeedback(labels.activationLinkReady)
      } else {
        setProjectFeedback(data?.run?.production?.status === 'ready'
          ? 'Production ERP is ready.'
          : 'Production promotion started.')
      }
      if (data?.run?.id) {
        setProjects((current) => current.map((project) => ({
          ...project,
          generationRuns: project.generationRuns?.map((run) =>
            run.id === data.run.id ? data.run : run),
        })))
      } else {
        await refreshProjects()
      }
    } catch {
      setProjectError('Unable to promote ERP to production.')
    } finally {
      setPendingAction('')
    }
  }, [labels.activationLinkReady, openLogin, refreshProjects])

  const requestRuntimeActivationLink = useCallback(async (
    runId: string,
    environment: 'staging' | 'production',
  ) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return
    }

    const actionKey = `activation:${environment}:${runId}`
    setPendingAction(actionKey)
    setProjectError('')
    setProjectFeedback('')

    try {
      const response = await fetch(`/api/customers/generation-runs/${encodeURIComponent(runId)}/environments/${environment}/activation-link`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify({}),
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        setProjectError(data?.message || 'Unable to generate activation link.')
        return
      }

      if (data?.activation?.activationUrl) {
        setActivationLinks((current) => ({
          ...current,
          [`${runId}:${environment}`]: data.activation,
        }))
      }
      setProjectFeedback(labels.activationLinkReady)
      await refreshProjects()
    } catch {
      setProjectError('Unable to generate activation link.')
    } finally {
      setPendingAction('')
    }
  }, [labels.activationLinkReady, openLogin, refreshProjects])

  const connectNangoInputSource = useCallback(async (project: GenerationProjectSummary, connector: ConnectorOption) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return
    }

    const actionKey = `connect-source:${project.id}:${connector.key}`
    setPendingAction(actionKey)
    setConnectorErrors((current) => ({ ...current, [project.id]: '' }))
    setProjectFeedback('')

    try {
      const response = await fetch(`/api/customers/generation-projects/${encodeURIComponent(project.id)}/nango/connect-session`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          connectorKey: connector.key,
          displayName: connector.title,
          authorizationAccepted: true,
        }),
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        setConnectorErrors((current) => ({
          ...current,
          [project.id]: data?.message || 'Unable to start the document connector.',
        }))
        return
      }

      if (data?.connectLink) {
        window.open(data.connectLink, '_blank', 'noopener,noreferrer')
      }
      if (data?.inputSource?.id) {
        setProjects((current) => current.map((item) => {
          if (item.id !== project.id) return item
          const sources = item.inputSources || []
          const nextSources = sources.some((source) => source.id === data.inputSource.id)
            ? sources.map((source) => source.id === data.inputSource.id ? data.inputSource : source)
            : [data.inputSource, ...sources]

          return withInputSourceReadiness(item, nextSources)
        }))
      } else {
        await refreshProjects()
      }
      setProjectFeedback(labels.connectOpened)
    } catch {
      setConnectorErrors((current) => ({
        ...current,
        [project.id]: 'Unable to start the document connector.',
      }))
    } finally {
      setPendingAction('')
    }
  }, [labels.connectOpened, openLogin, refreshProjects])

  const loadConnectorResources = useCallback(async (
    projectId: string,
    sourceId: string,
  ) => {
    const actionKey = `load-source-resources:${sourceId}`
    setPendingAction(actionKey)
    setConnectorErrors((current) => ({ ...current, [projectId]: '' }))
    try {
      const response = await fetch(
        `/api/customers/generation-projects/${encodeURIComponent(projectId)}/input-sources/${encodeURIComponent(sourceId)}/resources`,
        { method: 'GET' },
      )
      const data = await response.json().catch(() => ({}))
      if (!response.ok) {
        setConnectorErrors((current) => ({
          ...current,
          [projectId]: data?.message || labels.connectorResourcesFailed,
        }))
        await reconcileInputSources(projectId, { quiet: true })
        return [] as ConnectorResourceSummary[]
      }
      const resources = Array.isArray(data?.resources) ? data.resources : []
      setConnectorResources((current) => ({
        ...current,
        [sourceId]: resources,
      }))
      return resources as ConnectorResourceSummary[]
    } catch {
      setConnectorErrors((current) => ({
        ...current,
        [projectId]: labels.connectorResourcesFailed,
      }))
      return [] as ConnectorResourceSummary[]
    } finally {
      setPendingAction('')
    }
  }, [labels.connectorResourcesFailed, reconcileInputSources])

  const clearConnectorError = useCallback((projectId: string) => {
    setConnectorErrors((current) => ({ ...current, [projectId]: '' }))
  }, [])

  const updateInputSourceSelection = useCallback(async (
    projectId: string,
    sourceId: string,
    scope: ConnectorScopeInput,
  ) => {
    const actionKey = `save-source-selection:${sourceId}`
    setPendingAction(actionKey)
    setConnectorErrors((current) => ({ ...current, [projectId]: '' }))
    try {
      const response = await fetch(
        `/api/customers/generation-projects/${encodeURIComponent(projectId)}/input-sources/${encodeURIComponent(sourceId)}/selection`,
        {
          method: 'PUT',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify(scope),
        },
      )
      const source = await response.json().catch(() => ({}))
      if (!response.ok) {
        setConnectorErrors((current) => ({
          ...current,
          [projectId]: source?.message || labels.connectorSelectionFailed,
        }))
        await reconcileInputSources(projectId, { quiet: true })
        return false
      }
      setProjects((current) => current.map((project) => project.id !== projectId
        ? project
        : withInputSourceReadiness(
            project,
            (project.inputSources || []).map((item) => item.id === sourceId ? source : item),
          )))
      setConnectorResources((current) => ({
        ...current,
        [sourceId]: (current[sourceId] || []).map((resource) => ({
          ...resource,
          selected: scope.includedRootIds.includes(resource.id),
        })),
      }))
      return true
    } catch {
      setConnectorErrors((current) => ({
        ...current,
        [projectId]: labels.connectorSelectionFailed,
      }))
      return false
    } finally {
      setPendingAction('')
    }
  }, [labels.connectorSelectionFailed, reconcileInputSources])

  const refreshInputSource = useCallback(async (projectId: string, sourceId: string) => {
    const actionKey = `refresh-source:${sourceId}`
    setPendingAction(actionKey)
    setConnectorErrors((current) => ({ ...current, [projectId]: '' }))
    try {
      const response = await fetch(
        `/api/customers/generation-projects/${encodeURIComponent(projectId)}/input-sources/${encodeURIComponent(sourceId)}/refresh`,
        { method: 'POST' },
      )
      const source = await response.json().catch(() => ({}))
      if (!response.ok) {
        setConnectorErrors((current) => ({
          ...current,
          [projectId]: source?.message || labels.connectorCheckFailed,
        }))
        return false
      }
      setProjects((current) => current.map((project) => project.id !== projectId
        ? project
        : withInputSourceReadiness(
            project,
            (project.inputSources || []).map((item) => item.id === sourceId ? source : item),
          )))
      return true
    } catch {
      setConnectorErrors((current) => ({
        ...current,
        [projectId]: labels.connectorCheckFailed,
      }))
      return false
    } finally {
      setPendingAction('')
    }
  }, [labels.connectorCheckFailed])

  const disconnectInputSource = useCallback(async (projectId: string, sourceId: string) => {
    const actionKey = `disconnect-source:${sourceId}`
    setPendingAction(actionKey)
    setConnectorErrors((current) => ({ ...current, [projectId]: '' }))
    try {
      const response = await fetch(
        `/api/customers/generation-projects/${encodeURIComponent(projectId)}/input-sources/${encodeURIComponent(sourceId)}`,
        { method: 'DELETE' },
      )
      if (!response.ok) {
        const data = await response.json().catch(() => ({}))
        setConnectorErrors((current) => ({
          ...current,
          [projectId]: data?.message || labels.connectorDisconnectFailed,
        }))
        return false
      }
      setProjects((current) => current.map((project) => {
        if (project.id !== projectId) return project
        const inputSources = (project.inputSources || []).filter((source) => source.id !== sourceId)
        return withInputSourceReadiness(project, inputSources)
      }))
      setConnectorResources((current) => {
        const next = { ...current }
        delete next[sourceId]
        return next
      })
      return true
    } catch {
      setConnectorErrors((current) => ({
        ...current,
        [projectId]: labels.connectorDisconnectFailed,
      }))
      return false
    } finally {
      setPendingAction('')
    }
  }, [labels.connectorDisconnectFailed])

  const deleteProject = useCallback(async (projectId: string) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return false
    }

    const actionKey = `delete-project:${projectId}`
    setPendingAction(actionKey)
    setProjectError('')
    setProjectFeedback('')

    try {
      const response = await fetch(`/api/customers/generation-projects/${encodeURIComponent(projectId)}`, {
        method: 'DELETE',
        headers: {
        },
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        setProjectError(data?.message || 'Unable to delete this ERP.')
        return false
      }

      setProjects((current) => current.filter((project) => project.id !== projectId))
      setProjectFeedback(labels.erpDeleted)
      return true
    } catch {
      setProjectError('Unable to delete this ERP.')
      return false
    } finally {
      setPendingAction('')
    }
  }, [labels.erpDeleted, openLogin])

  return {
    projects,
    setProjects,
    projectsLoading,
    projectCreating,
    setProjectCreating,
    generationStarting,
    pendingAction,
    pendingEvidenceDrafts,
    setPendingEvidenceDrafts,
    pendingResolutionNotes,
    setPendingResolutionNotes,
    connectorSearches,
    setConnectorSearches,
    connectorCatalogsOpen,
    setConnectorCatalogsOpen,
    connectorErrors,
    connectorResources,
    projectCreateOpen,
    setProjectCreateOpen,
    projectError,
    setProjectError,
    projectFeedback,
    setProjectFeedback,
    activationLinks,
    setActivationLinks,
    projectIndustry,
    setProjectIndustry,
    projectCompanyType,
    setProjectCompanyType,
    projectGoal,
    setProjectGoal,
    projectLeadership,
    setProjectLeadership,
    inviteLinks,
    refreshProjects,
    refreshProject,
    startGeneration,
    generateWithAssumptions,
    sendInterviewInvite,
    copyInterviewInvite,
    addPendingEvidenceDocument,
    resolvePendingWork,
    promoteProduction,
    requestRuntimeActivationLink,
    connectNangoInputSource,
    reconcileInputSources,
    loadConnectorResources,
    clearConnectorError,
    updateInputSourceSelection,
    refreshInputSource,
    disconnectInputSource,
    deleteProject,
  }
}
