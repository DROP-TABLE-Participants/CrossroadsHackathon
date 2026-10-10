import { INDUSTRY_OTHER_KEY, type WizardCopy, type WizardOption } from './copy'
import type { WizardDraft } from './useWizardDraft'
import type { Locale } from '@/i18n/config'

/**
 * The wizard collects structured picks, but the control-plane API still takes
 * three free-text strings (`industry`, `companyType`, `goal`) — which the engine
 * feeds verbatim into its Bootstrap prompt and, for `industry`, into the ERP
 * module name. These helpers do that composition in one place so the wire format
 * stays reviewable, and so the Review step shows exactly what will be sent.
 *
 * Composed values are localized: the owner's picks stay in the owner's language,
 * the same contract the free-text fields had.
 */

/** Mirrors the server's caps in `POST /api/customers/generation-projects`. */
export const INDUSTRY_MAX = 120
export const COMPANY_TYPE_MAX = 120
export const GOAL_MAX = 2000
/** Leaves room for the composed pain list to fit under GOAL_MAX. */
export const GOAL_NOTES_MAX = 1500

function labelFor(options: WizardOption[], key: string) {
  return options.find((option) => option.key === key)?.label ?? ''
}

export function composeIndustry(draft: WizardDraft, copy: WizardCopy): string {
  if (draft.industryKey === INDUSTRY_OTHER_KEY) return draft.industryOther.trim()
  return labelFor(copy.steps.business.industryOptions, draft.industryKey)
}

export function composeCompanyType(draft: WizardDraft, copy: WizardCopy, locale: Locale): string {
  const { ownershipOptions, sizeOptions, sitesOptions } = copy.steps.business
  const parts = [
    labelFor(ownershipOptions, draft.ownershipKey),
    labelFor(sizeOptions, draft.sizeKey),
    labelFor(sitesOptions, draft.sitesKey),
  ].filter(Boolean)

  // Reads as one fragment ("Family-owned, 11–50 people, single site") instead of
  // three stitched-together headings. Safe to lowercase the trailing parts —
  // these are controlled vocabularies with no proper nouns.
  return parts
    .map((part, index) => (index === 0 ? part : part.toLocaleLowerCase(locale)))
    .join(', ')
}

export function composeGoal(draft: WizardDraft, copy: WizardCopy): string {
  const { painOptions, composedPainsHeading, composedNotesHeading } = copy.steps.goal
  // Catalog order, not click order, so the same answers always read the same.
  const pains = painOptions
    .filter((option) => draft.painKeys.includes(option.key))
    .map((option) => option.label)
  const notes = draft.goalNotes.trim()

  const blocks: string[] = []
  if (pains.length) {
    blocks.push([composedPainsHeading, ...pains.map((pain) => `- ${pain}`)].join('\n'))
  }
  if (notes) {
    blocks.push(`${composedNotesHeading}\n${notes}`)
  }

  return blocks.join('\n\n')
}

export function composeProjectFields(draft: WizardDraft, copy: WizardCopy, locale: Locale) {
  return {
    industry: composeIndustry(draft, copy),
    companyType: composeCompanyType(draft, copy, locale),
    goal: composeGoal(draft, copy),
  }
}
