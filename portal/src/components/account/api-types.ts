import type { useErpFeedback } from '@/components/account/hooks/useErpFeedback'
import type { useGenerationProjects } from '@/components/account/hooks/useGenerationProjects'
import type { useProfile } from '@/components/account/hooks/useProfile'
import type { useTenants } from '@/components/account/hooks/useTenants'

/**
 * The presentation layer receives the state hooks' return values wholesale so
 * every callback stays reachable without re-plumbing individual props.
 */
export type ProjectsApi = ReturnType<typeof useGenerationProjects>
export type ProfileApi = ReturnType<typeof useProfile>
export type TenantsApi = ReturnType<typeof useTenants>
export type FeedbackApi = ReturnType<typeof useErpFeedback>
