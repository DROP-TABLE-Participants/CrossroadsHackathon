import { useState } from 'react'
import type { FormEvent } from 'react'

import type { AccountLabels } from '@/components/account/labels'
import { hasCustomerSessionHint } from '@/components/booking/customer-session'

type UseProfileParams = {
  labels: AccountLabels
  openLogin: () => void
  refreshCustomer: () => Promise<void>
}

export function useProfile({ labels, openLogin, refreshCustomer }: UseProfileParams) {
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [phoneNumber, setPhoneNumber] = useState('')
  const [companyName, setCompanyName] = useState('')
  const [position, setPosition] = useState('')
  const [feedback, setFeedback] = useState('')
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)

  const handleProfileSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setFeedback('')
    setError('')
    setSaving(true)

    const hasSession = hasCustomerSessionHint()
    if (!hasSession) {
      setSaving(false)
      setError('Session expired. Please login again.')
      openLogin()
      return
    }

    try {
      const response = await fetch('/api/customers/me', {
        method: 'PATCH',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          name,
          email,
          phoneNumber,
          companyName,
          position,
        }),
      })

      if (!response.ok) {
        const data = await response.json().catch(() => ({}))
        setError(data?.message || 'Unable to save profile details.')
        return
      }

      await refreshCustomer()
      setFeedback(labels.saved)
    } catch {
      setError('Unable to save profile details.')
    } finally {
      setSaving(false)
    }
  }

  return {
    name,
    setName,
    email,
    setEmail,
    phoneNumber,
    setPhoneNumber,
    companyName,
    setCompanyName,
    position,
    setPosition,
    feedback,
    error,
    saving,
    handleProfileSubmit,
  }
}
