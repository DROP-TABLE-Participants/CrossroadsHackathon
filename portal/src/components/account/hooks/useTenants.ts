import { useCallback, useState } from 'react'
import type { Dispatch, FormEvent, SetStateAction } from 'react'

import { resolveGeneratedBundleSelection } from '@/components/account/helpers'
import type { AccountLabels } from '@/components/account/labels'
import type { GeneratedBundleOption, OneTimeActivationLink, TenantSummary } from '@/components/account/types'
import { hasCustomerSessionHint } from '@/components/booking/customer-session'

type UseTenantsParams = {
  labels: AccountLabels
  openLogin: () => void
  companyName: string
  generatedBundleOptions: GeneratedBundleOption[]
  setActivationLinks: Dispatch<SetStateAction<Record<string, OneTimeActivationLink>>>
  refreshProjects: () => Promise<void>
}

export function useTenants({
  labels,
  openLogin,
  companyName,
  generatedBundleOptions,
  setActivationLinks,
  refreshProjects,
}: UseTenantsParams) {
  const [tenants, setTenants] = useState<TenantSummary[]>([])
  const [tenantsLoading, setTenantsLoading] = useState(false)
  const [tenantCreating, setTenantCreating] = useState(false)
  const [tenantError, setTenantError] = useState('')
  const [tenantFeedback, setTenantFeedback] = useState('')
  const [tenantSlug, setTenantSlug] = useState('')
  const [tenantBundleSelection, setTenantBundleSelection] = useState('')

  const refreshTenants = useCallback(async () => {
    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      setTenants([])
      return
    }

    setTenantsLoading(true)
    setTenantError('')

    try {
      const response = await fetch('/api/customers/tenants', {
        method: 'GET',
        headers: {
        },
      })

      if (!response.ok) {
        const data = await response.json().catch(() => ({}))
        setTenantError(data?.message || 'Unable to load ERP environments.')
        return
      }

      const data = await response.json()
      setTenants(Array.isArray(data?.tenants) ? data.tenants : [])
    } catch {
      setTenantError('Unable to load ERP environments.')
    } finally {
      setTenantsLoading(false)
    }
  }, [])

  const handleCreateTenantSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setTenantError('')
    setTenantFeedback('')
    setTenantCreating(true)

    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      setTenantCreating(false)
      openLogin()
      return
    }

    const selectedBundle = resolveGeneratedBundleSelection(tenantBundleSelection, generatedBundleOptions)
    const generatedBundle = generatedBundleOptions.find((bundle) => bundle.id === selectedBundle)

    if (!generatedBundle) {
      setTenantError('Select a generated ERP bundle before creating an environment.')
      setTenantCreating(false)
      return
    }

    try {
      const response = await fetch('/api/customers/tenants', {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          environmentName: companyName,
          slug: tenantSlug,
          bundleSourceType: 'generated',
          bundleRunId: generatedBundle?.runId,
        }),
      })

      const data = await response.json().catch(() => ({}))
      if (!response.ok) {
        setTenantError(data?.message || 'Unable to create ERP environment.')
        return
      }

      if (data?.deployment?.activation?.activationUrl && generatedBundle?.runId) {
        setActivationLinks((current) => ({
          ...current,
          [`${generatedBundle.runId}:staging`]: data.deployment.activation,
        }))
        setTenantFeedback(labels.activationLinkReady)
      } else {
        setTenantFeedback(data?.deployment?.message || 'ERP environment created.')
      }
      await refreshTenants()
      await refreshProjects()
    } catch {
      setTenantError('Unable to create ERP environment.')
    } finally {
      setTenantCreating(false)
    }
  }

  return {
    tenants,
    setTenants,
    tenantsLoading,
    tenantCreating,
    tenantError,
    setTenantError,
    tenantFeedback,
    setTenantFeedback,
    tenantSlug,
    setTenantSlug,
    tenantBundleSelection,
    setTenantBundleSelection,
    refreshTenants,
    handleCreateTenantSubmit,
  }
}
