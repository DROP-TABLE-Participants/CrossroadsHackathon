import { useCallback, useState } from 'react'
import type { Dispatch, SetStateAction } from 'react'

import type { AccountLabels } from '@/components/account/labels'
import type {
  ErpChangeJobSummary,
  ErpChangeProposalSummary,
  ErpFeedbackSummary,
  ErpUsageObservationSummary,
} from '@/components/account/types'
import { hasCustomerSessionHint } from '@/components/booking/customer-session'

type UseErpFeedbackParams = {
  labels: AccountLabels
  openLogin: () => void
  setTenantError: Dispatch<SetStateAction<string>>
  setTenantFeedback: Dispatch<SetStateAction<string>>
  refreshTenants: () => Promise<void>
  refreshProjects: () => Promise<void>
}

export function useErpFeedback({
  labels,
  openLogin,
  setTenantError,
  setTenantFeedback,
  refreshTenants,
  refreshProjects,
}: UseErpFeedbackParams) {
  const [erpFeedbackItems, setErpFeedbackItems] = useState<ErpFeedbackSummary[]>([])
  const [erpUsageObservations, setErpUsageObservations] = useState<ErpUsageObservationSummary[]>([])
  const [erpChangeProposals, setErpChangeProposals] = useState<ErpChangeProposalSummary[]>([])
  const [erpChangeJobs, setErpChangeJobs] = useState<ErpChangeJobSummary[]>([])
  const [erpFeedbackLoading, setErpFeedbackLoading] = useState(false)
  const [erpFeedbackAction, setErpFeedbackAction] = useState('')
  const [erpFeedbackNotice, setErpFeedbackNotice] = useState('')

  const refreshErpFeedback = useCallback(async () => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      setErpFeedbackItems([])
      setErpUsageObservations([])
      setErpChangeProposals([])
      setErpChangeJobs([])
      return
    }

    setErpFeedbackLoading(true)

    try {
      const [feedbackResponse, observationsResponse, proposalsResponse, jobsResponse] = await Promise.all([
        fetch('/api/customers/erp-feedback', {
          method: 'GET',
        }),
        fetch('/api/customers/erp-usage-observations', {
          method: 'GET',
        }),
        fetch('/api/customers/erp-change-proposals', {
          method: 'GET',
        }),
        fetch('/api/customers/erp-change-jobs', {
          method: 'GET',
        }),
      ])

      if (feedbackResponse.ok) {
        const data = await feedbackResponse.json().catch(() => ({}))
        setErpFeedbackItems(Array.isArray(data?.feedback) ? data.feedback : [])
      }

      if (observationsResponse.ok) {
        const data = await observationsResponse.json().catch(() => ({}))
        setErpUsageObservations(Array.isArray(data?.observations) ? data.observations : [])
      }

      if (proposalsResponse.ok) {
        const data = await proposalsResponse.json().catch(() => ({}))
        setErpChangeProposals(Array.isArray(data?.proposals) ? data.proposals : [])
      }

      if (jobsResponse.ok) {
        const data = await jobsResponse.json().catch(() => ({}))
        setErpChangeJobs(Array.isArray(data?.jobs) ? data.jobs : [])
      }
    } catch {
      setTenantError('Unable to load ERP feedback.')
    } finally {
      setErpFeedbackLoading(false)
    }
  }, [setTenantError])

  const dismissUsageObservation = useCallback(async (observationId: string) => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return
    }

    const actionKey = `observation-dismiss:${observationId}`
    setErpFeedbackAction(actionKey)
    setTenantError('')
    setTenantFeedback('')
    setErpFeedbackNotice('')

    try {
      const response = await fetch(`/api/customers/erp-usage-observations/${encodeURIComponent(observationId)}/dismiss`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        setTenantError(data?.message || 'Unable to dismiss observation.')
        return
      }

      if (data?.observation) {
        setErpUsageObservations((current) => current.map((observation) =>
          observation.id === observationId ? data.observation : observation))
      }
      setTenantFeedback('Usage observation dismissed.')
    } catch {
      setTenantError('Unable to dismiss observation.')
    } finally {
      setErpFeedbackAction('')
    }
  }, [openLogin, setTenantError, setTenantFeedback])

  const reviewFeedbackProposal = useCallback(async (proposalId: string, decision: 'accept' | 'reject') => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      openLogin()
      return
    }

    const actionKey = `proposal-${decision}:${proposalId}`
    setErpFeedbackAction(actionKey)
    setTenantError('')
    setTenantFeedback('')
    setErpFeedbackNotice('')

    try {
      const response = await fetch(`/api/customers/erp-change-proposals/${encodeURIComponent(proposalId)}/${decision}`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
      })
      const data = await response.json().catch(() => ({}))

      if (!response.ok) {
        setTenantError(data?.message || 'Unable to review change proposal.')
        return
      }

      if (data?.proposal) {
        setErpChangeProposals((current) => current.map((proposal) =>
          proposal.id === proposalId ? data.proposal : proposal))
      } else {
        await refreshErpFeedback()
      }
      setTenantFeedback(decision === 'accept' ? labels.acceptedNotApplied : 'Change proposal rejected.')
    } catch {
      setTenantError('Unable to review change proposal.')
    } finally {
      setErpFeedbackAction('')
    }
  }, [labels.acceptedNotApplied, openLogin, refreshErpFeedback, setTenantError, setTenantFeedback])

  return {
    erpFeedbackItems,
    setErpFeedbackItems,
    erpUsageObservations,
    setErpUsageObservations,
    erpChangeProposals,
    setErpChangeProposals,
    erpChangeJobs,
    setErpChangeJobs,
    erpFeedbackLoading,
    erpFeedbackAction,
    erpFeedbackNotice,
    refreshErpFeedback,
    dismissUsageObservation,
    reviewFeedbackProposal,
  }
}
