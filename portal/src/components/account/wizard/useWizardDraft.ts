'use client'

import { useCallback, useEffect, useRef, useState } from 'react'

export type WizardPerson = {
  name: string
  email: string
  roleTitle: string
}

/**
 * Structured answers. These are composed into the API's `industry` /
 * `companyType` / `goal` strings at submit time — see `compose.ts`. Keys come
 * from the catalogs in `copy.ts` and are locale-stable, so switching language
 * mid-wizard keeps every pick.
 */
export type WizardDraft = {
  companyName: string
  industryKey: string
  industryOther: string
  sizeKey: string
  ownershipKey: string
  sitesKey: string
  painKeys: string[]
  goalNotes: string
  people: WizardPerson[]
}

export const EMPTY_DRAFT: WizardDraft = {
  companyName: '',
  industryKey: '',
  industryOther: '',
  sizeKey: '',
  ownershipKey: '',
  sitesKey: '',
  painKeys: [],
  goalNotes: '',
  people: [],
}

// v2 = structured picks. The bump makes pre-restructure drafts expire cleanly
// instead of hydrating into a half-populated state.
function draftKey(customerId: string) {
  return `erp-wizard-draft:v2:${customerId}`
}

function readString(value: unknown) {
  return typeof value === 'string' ? value : ''
}

function readDraft(customerId: string): WizardDraft | null {
  if (typeof window === 'undefined') return null
  try {
    const raw = window.sessionStorage.getItem(draftKey(customerId))
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<WizardDraft>
    return {
      companyName: readString(parsed.companyName),
      industryKey: readString(parsed.industryKey),
      industryOther: readString(parsed.industryOther),
      sizeKey: readString(parsed.sizeKey),
      ownershipKey: readString(parsed.ownershipKey),
      sitesKey: readString(parsed.sitesKey),
      painKeys: Array.isArray(parsed.painKeys)
        ? parsed.painKeys.filter((key): key is string => typeof key === 'string')
        : [],
      goalNotes: readString(parsed.goalNotes),
      people: Array.isArray(parsed.people)
        ? parsed.people
            .filter((person): person is WizardPerson => Boolean(person && typeof person === 'object'))
            .map((person) => ({
              name: readString(person.name),
              email: readString(person.email),
              roleTitle: readString(person.roleTitle),
            }))
        : [],
    }
  } catch {
    return null
  }
}

/**
 * Keeps the wizard's answers in sessionStorage so an accidental refresh or
 * navigation never loses the owner's work mid-flow.
 */
export function useWizardDraft(customerId: string | null, initial: WizardDraft) {
  const [draft, setDraft] = useState<WizardDraft>(initial)
  const hydratedFor = useRef<string | null>(null)

  useEffect(() => {
    if (!customerId || hydratedFor.current === customerId) return
    hydratedFor.current = customerId
    const stored = readDraft(customerId)
    if (stored) {
      setDraft((current) => ({
        ...stored,
        // Never let a stale stored draft blank out fresh profile prefills.
        companyName: stored.companyName || current.companyName,
      }))
    }
  }, [customerId])

  useEffect(() => {
    if (!customerId || typeof window === 'undefined') return
    try {
      window.sessionStorage.setItem(draftKey(customerId), JSON.stringify(draft))
    } catch {
      // Storage full/blocked — the wizard still works, just without persistence.
    }
  }, [customerId, draft])

  const clearDraft = useCallback(() => {
    if (!customerId || typeof window === 'undefined') return
    try {
      window.sessionStorage.removeItem(draftKey(customerId))
    } catch {
      // Ignore storage errors on cleanup.
    }
  }, [customerId])

  return { draft, setDraft, clearDraft }
}
