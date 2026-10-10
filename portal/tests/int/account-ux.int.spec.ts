import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import React from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  CustomerAccountPage,
  discoveryJourneyStep,
  prioritizeCustomerPendingWorkItems,
  resolveGeneratedBundleSelection,
  shouldShowCustomerArtifact,
  shouldShowRuntimeEnvironmentCreateForm,
} from '@/components/account/CustomerAccountPage'
import { PublicInterviewPage } from '@/components/account/PublicInterviewPage'
import { getLabels } from '@/components/account/labels'
import {
  generationInFlight,
  generationProgress,
} from '@/components/account/projects/generation-progress'
import type {
  GenerationProjectSummary,
  GenerationRunSummary,
} from '@/components/account/types'
import { saveAuthSession, shouldClearAuthSessionForCustomerProfileStatus } from '@/components/booking/customer-session'
import { installTestLocalStorage } from './helpers/local-storage'

const realtimeState = vi.hoisted(() => ({
  handlers: new Map<string, Set<(message: unknown) => void>>(),
}))

vi.mock('@microsoft/signalr', () => ({
  HttpTransportType: { WebSockets: 1 },
  LogLevel: { Warning: 3 },
  HubConnectionBuilder: class {
    withUrl() { return this }
    configureLogging() { return this }
    build() {
      return {
        on(eventName: string, handler: (message: unknown) => void) {
          const handlers = realtimeState.handlers.get(eventName) ?? new Set()
          handlers.add(handler)
          realtimeState.handlers.set(eventName, handlers)
        },
        off(eventName: string, handler: (message: unknown) => void) {
          realtimeState.handlers.get(eventName)?.delete(handler)
        },
        onclose() {},
        start: vi.fn(async () => {}),
        stop: vi.fn(async () => {}),
      }
    }
  },
}))

const bookingState = vi.hoisted(() => ({
  current: {} as {
    customer: {
      id: string
      email: string
      name: string
      companyName: string
      phoneNumber?: string
      position?: string
      isGuest?: boolean
    } | null
    activeBooking: null
    openLogin: () => void
    openBooking: () => void
    refreshCustomer: () => Promise<void>
    logout: () => Promise<void>
  },
}))

vi.mock('@/components/booking/DemoBookingProvider', () => ({
  useDemoBooking: () => bookingState.current,
}))

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

function setSignedInCustomer() {
  installTestLocalStorage()
  saveAuthSession({ id: 'customer-1', email: 'owner@example.test' })
  bookingState.current = {
    customer: {
      id: 'customer-1',
      email: 'owner@example.test',
      name: 'Owner',
      companyName: 'Acme Foods',
      phoneNumber: '+1555010101',
      position: 'Owner',
    },
    activeBooking: null,
    openLogin: vi.fn(),
    openBooking: vi.fn(),
    refreshCustomer: vi.fn(async () => {}),
    logout: vi.fn(async () => {}),
  }
}

function baseRun(overrides: Record<string, unknown> = {}) {
  return {
    id: 'run-1',
    status: 'completed',
    stages: [
      {
        id: 'stage-1',
        stage: 'compile_check',
        status: 'completed',
        summary: 'Bundle compiled.',
      },
    ],
    artifacts: [
      {
        id: 'artifact-1',
        kind: 'compile_result',
        label: 'Compile result',
        status: 'completed',
        summary: 'Compile check passed.',
      },
    ],
    pendingWorkItems: [],
    generatedErp: {
      entities: Array.from({ length: 23 }, (_, index) => `Entity ${index + 1}`),
      workflows: Array.from({ length: 4 }, (_, index) => `Workflow ${index + 1}`),
      actions: ['create_invoice_draft'],
      reports: Array.from({ length: 9 }, (_, index) => `Report ${index + 1}`),
      dashboards: ['main'],
      permissions: ['owner'],
      openIssues: [],
    },
    ...overrides,
  }
}

function baseProject(overrides: Record<string, unknown> = {}) {
  const generationRuns = Array.isArray(overrides.generationRuns)
    ? overrides.generationRuns
    : [baseRun()]
  return {
    id: 'project-1',
    companyName: 'Acme Foods',
    industry: 'food_and_beverage',
    companyType: 'manufacturer',
    status: 'active',
    discoveryMode: 'connector_first',
    inputSourceCount: 1,
    inputSources: [],
    availableConnectors: ['notion'],
    participants: [],
    requests: [],
    generationRuns,
    canStartGeneration: generationRuns.length === 0,
    generationBlockedReason: null,
    ...overrides,
  }
}

describe('canonical generation cancellation status', () => {
  const project = {
    id: 'project-cancellation',
    companyName: 'Acme Foods',
  } satisfies GenerationProjectSummary
  const run = (status: string): GenerationRunSummary => ({
    id: 'run-cancellation',
    status,
    stages: [
      {
        id: 'stage-company',
        stage: 'company_understanding',
        status: 'completed',
      },
    ],
    artifacts: [],
    pendingWorkItems: [],
  })

  it('shows cancellation in progress as active durable work', () => {
    const progress = generationProgress(
      getLabels('en'),
      project,
      run('cancellation_requested'),
    )

    expect(progress).toMatchObject({
      headline: 'Generation is stopping safely.',
      tone: 'waiting',
    })
    expect(generationInFlight(run('cancellation_requested'))).toBe(true)
  })

  it('shows a terminal cancellation without pretending work continues', () => {
    const progress = generationProgress(
      getLabels('en'),
      project,
      run('cancelled'),
    )

    expect(progress).toMatchObject({
      headline: 'Generation was cancelled. Completed work was preserved.',
      tone: 'cancelled',
    })
    expect(generationInFlight(run('cancelled'))).toBe(false)
  })
})

describe('canonical generation resume status', () => {
  it('shows resolved input as active resume work rather than missing evidence', () => {
    const labels = getLabels('en')
    const project = {
      id: 'project-resume',
      companyName: 'Acme Foods',
      status: 'generating',
    } satisfies GenerationProjectSummary
    const run = {
      id: 'run-resume',
      status: 'resuming',
      stages: [
        {
          id: 'stage-company',
          stage: 'company_understanding',
          status: 'waiting_for_input',
        },
      ],
      artifacts: [],
      pendingWorkItems: [
        {
          id: 'pending-resolved',
          kind: 'interview_request',
          status: 'resolved',
          blocking: true,
        },
      ],
    } satisfies GenerationRunSummary

    expect(generationProgress(labels, project, run)).toMatchObject({
      phase: 'questions',
      headline: 'Thanks — continuing where we left off.',
      tone: 'running',
    })
    expect(generationInFlight(run)).toBe(true)
  })

  it('shows repeated resume failure as operator attention without asking for more evidence', () => {
    const labels = getLabels('en')
    const project = {
      id: 'project-resume-failed',
      companyName: 'Acme Foods',
      status: 'needs_information',
    } satisfies GenerationProjectSummary
    const run = {
      id: 'run-resume-failed',
      status: 'resume_failed',
      stages: [],
      artifacts: [],
      pendingWorkItems: [],
    } satisfies GenerationRunSummary

    expect(generationProgress(labels, project, run)).toMatchObject({
      headline: 'We received everything required, but automatic resume needs attention from our team.',
      tone: 'failed',
    })
    expect(generationInFlight(run)).toBe(true)
  })
})

function baseTenant(overrides: Record<string, unknown> = {}) {
  return {
    id: 'tenant-1',
    stableId: 'tenant-stable-1',
    slug: 'acme',
    companyName: 'Acme Foods',
    plan: 'starter',
    status: 'active',
    setupStatus: 'questions_complete',
    host: 'acme.erpgenerator.test',
    runtimeUrl: 'http://acme.erpgenerator.test',
    deploymentStatus: 'staging',
    stagingDeploymentStatus: 'ready',
    productionDeploymentStatus: 'pending',
    latestStagedBundleVersion: '0.1.0',
    deploymentHistory: [],
    ...overrides,
  }
}

function mockAccountFetch(input: {
  projects?: unknown[]
  projectsAfterPendingInterview?: unknown[]
  tenants?: unknown[]
  feedback?: unknown[]
  observations?: unknown[]
  proposals?: unknown[]
  jobs?: unknown[]
  promotedRun?: unknown
  promoteResponse?: () => Promise<Response>
  applyChangeJobResponse?: () => Promise<Response>
  connectSourceResponse?: () => Promise<Response>
  reconcileSourcesResponse?: () => Promise<Response>
  refreshSourceResponse?: () => Promise<Response>
  connectorResourcesResponse?: () => Promise<Response>
  connectorSelectionResponse?: () => Promise<Response>
  disconnectSourceResponse?: () => Promise<Response>
  generationProjectsResponse?: () => Promise<Response>
  pendingInterviewResponse?: unknown
  sendInviteResponse?: unknown
  copyInviteResponse?: unknown
} = {}) {
  let pendingInterviewCreated = false
  let generatedTenantCreated = false
  let generatedTenantProject: unknown | null = null
  let appliedChangeJob: unknown | null = null
  let observations = (input.observations || []) as Array<Record<string, unknown>>

  vi.stubGlobal('fetch', vi.fn(async (request: RequestInfo | URL, init?: RequestInit) => {
    const url = String(request)



    if (url.includes('/api/customers/pending-work-items/pending-interview/create-interview-request')) {
      pendingInterviewCreated = true
      return jsonResponse(input.pendingInterviewResponse || {
        requestId: 'request-1',
        inviteLink: 'http://localhost/interviews/invite-token',
        inviteDelivery: {
          status: 'sent',
          recipientEmail: 'finance@example.test',
          provider: 'fake',
          attemptCount: 1,
        },
      }, 201)
    }

    if (url.includes('/api/customers/interview-requests/request-1/send-invite')) {
      return jsonResponse(input.sendInviteResponse || {
        requestId: 'request-1',
        inviteLink: 'http://localhost/interviews/regenerated-token',
        inviteDelivery: {
          status: 'sent',
          recipientEmail: 'finance@example.test',
          provider: 'fake',
          attemptCount: 2,
        },
      })
    }

    if (url.includes('/api/customers/interview-requests/request-1/copy-link')) {
      return jsonResponse(input.copyInviteResponse || {
        inviteLink: 'http://localhost/interviews/persisted-token',
      })
    }

    if (url.includes('/api/customers/generation-projects/project-1/nango/connect-session')) {
      if (input.connectSourceResponse) return input.connectSourceResponse()

      return jsonResponse({
        sessionToken: 'session-token',
        connectLink: 'https://connect.nango.test/?session_token=session-token',
        expiresAt: '2026-07-05T00:30:00.000Z',
        providerConfigKey: 'notion-files',
        inputSource: {
          id: 'source-1',
          sourceType: 'nango',
          provider: 'notion',
          sourceFamily: 'files_documents',
          displayName: 'Notion',
          status: 'connecting',
          lastConnectionStatus: 'connect_session_created',
          generationBlockReason: 'connector_setup_pending',
        },
      })
    }

    if (url.includes('/api/customers/generation-projects/project-1/input-sources/reconcile')) {
      if (input.reconcileSourcesResponse) return input.reconcileSourcesResponse()

      const project = input.projects?.[0] as { inputSources?: unknown[] } | undefined
      return jsonResponse({ sources: project?.inputSources || [] })
    }

    if (url.includes('/api/customers/generation-projects/project-1/input-sources/') &&
        url.endsWith('/refresh')) {
      if (input.refreshSourceResponse) return input.refreshSourceResponse()
      const source = (input.projects?.[0] as { inputSources?: unknown[] } | undefined)?.inputSources?.[0] || {}
      return jsonResponse(source)
    }

    if (url.includes('/api/customers/generation-projects/project-1/input-sources/') &&
        url.endsWith('/resources')) {
      if (input.connectorResourcesResponse) return input.connectorResourcesResponse()
      return jsonResponse({ resources: [] })
    }

    if (url.includes('/api/customers/generation-projects/project-1/input-sources/') &&
        url.endsWith('/selection')) {
      if (input.connectorSelectionResponse) return input.connectorSelectionResponse()
      return jsonResponse({
        id: 'source-notion',
        provider: 'notion',
        displayName: 'Notion',
        status: 'syncing',
        selectedResourceCount: 1,
        generationBlockReason: 'connector_syncing',
      })
    }

    if (url.includes('/api/customers/generation-projects/project-1/input-sources/') &&
        init?.method === 'DELETE') {
      if (input.disconnectSourceResponse) return input.disconnectSourceResponse()
      return new Response(null, { status: 204 })
    }

    if (url.includes('/api/customers/generation-projects/project-1/start-generation')) {
      return jsonResponse({
        ok: true,
        agent: { runId: 'run-go', status: 'queued' },
        project: baseProject({
          tenantId: 'tenant-hidden',
          status: 'generating',
          generationRuns: [baseRun({ id: 'run-go', status: 'queued', generatedErp: undefined })],
        }),
      })
    }

    if (url.includes('/api/customers/erp-usage-observations/observation-1/dismiss')) {
      observations = observations.map((observation) =>
        observation.id === 'observation-1' ? { ...observation, status: 'dismissed' } : observation)
      return jsonResponse({ observation: observations[0] })
    }

    if (url.includes('/api/customers/erp-change-jobs/job-1/apply-production')) {
      if (input.applyChangeJobResponse) return input.applyChangeJobResponse()

      appliedChangeJob = {
        id: 'job-1',
        proposalId: 'proposal-1',
        tenantId: 'tenant-1',
        environmentId: 'tenant-1-staging',
        status: 'staged_for_review',
        stageStatus: 'staged_for_review',
        patchedBundleVersion: '0.1.0-cp-proposal_1',
        patchedBundleHash: 'hash-patched',
        stagingDeploymentId: 'deployment-staging-2',
        stagingUrl: 'http://acme-staging.erpgenerator.test',
        productionApplyStatus: 'applied',
        productionDeploymentId: 'runtime-production-42',
        productionBundleVersion: '0.1.0-cp-proposal_1',
        productionBundleHash: 'hash-patched',
        productionUrl: 'http://acme.erpgenerator.test',
        patchSummary: 'Added optional Billing entity field to invoices.',
      }
      return jsonResponse({ job: appliedChangeJob })
    }

    if (url.includes('/api/customers/erp-feedback')) {
      return jsonResponse({ feedback: input.feedback || [] })
    }

    if (url.includes('/api/customers/erp-usage-observations')) {
      return jsonResponse({ observations })
    }

    if (url.includes('/api/customers/erp-change-jobs')) {
      return jsonResponse({ jobs: appliedChangeJob ? [appliedChangeJob] : input.jobs || [] })
    }

    if (url.includes('/api/customers/erp-change-proposals')) {
      return jsonResponse({ proposals: input.proposals || [] })
    }

    if (url === '/api/customers/tenants' && init?.method === 'POST') {
      generatedTenantCreated = true
      generatedTenantProject = baseProject({
        tenantId: 'tenant-1',
        generationRuns: [baseRun({
          status: 'erp_ready',
          erp: {
            status: 'ready', testsStatus: 'passed',
            url: 'http://acme-staging.erpgenerator.test',
            bundleVersion: '0.1.0',
            activation: {
              status: 'pending',
              activationUrlAvailable: true,
              activationEnvironment: 'staging',
              activationEmail: 'owner@example.test',
            },
          },
        })],
      })
      return jsonResponse({
        tenant: baseTenant(),
        deployment: {
          status: 'requested',
          message: 'ERP staging environment creation started.',
          activation: {
            activationUrl: 'http://acme.erpgenerator.test/activate',
            activationUrlAvailable: true,
            activationEnvironment: 'staging',
          },
        },
      }, 201)
    }

    if (url.includes('/api/customers/tenants')) {
      return jsonResponse({ tenants: generatedTenantCreated ? [baseTenant()] : input.tenants || [] })
    }

    if (/\/api\/customers\/generation-projects\/project-1(?:$|\?)/.test(url)) {
      return jsonResponse({
        project: generatedTenantProject
          ? generatedTenantProject
          : pendingInterviewCreated && input.projectsAfterPendingInterview
          ? input.projectsAfterPendingInterview[0]
          : input.projects?.[0] || baseProject(),
      })
    }

    if (url.endsWith('/api/customers/generation-projects') || url === '/api/customers/generation-projects') {
      if (input.generationProjectsResponse) return input.generationProjectsResponse()

      return jsonResponse({
        projects: generatedTenantProject
          ? [generatedTenantProject]
          : pendingInterviewCreated && input.projectsAfterPendingInterview
          ? input.projectsAfterPendingInterview
          : input.projects || [],
      })
    }

    return jsonResponse({ message: 'Not found' }, 404)
  }))
}

async function renderAccount(input: {
  projects?: unknown[]
  projectsAfterPendingInterview?: unknown[]
  tenants?: unknown[]
  feedback?: unknown[]
  observations?: unknown[]
  proposals?: unknown[]
  jobs?: unknown[]
  promotedRun?: unknown
  promoteResponse?: () => Promise<Response>
  applyChangeJobResponse?: () => Promise<Response>
  connectSourceResponse?: () => Promise<Response>
  reconcileSourcesResponse?: () => Promise<Response>
  refreshSourceResponse?: () => Promise<Response>
  connectorResourcesResponse?: () => Promise<Response>
  connectorSelectionResponse?: () => Promise<Response>
  disconnectSourceResponse?: () => Promise<Response>
  generationProjectsResponse?: () => Promise<Response>
  pendingInterviewResponse?: unknown
  sendInviteResponse?: unknown
  copyInviteResponse?: unknown
} = {}) {
  setSignedInCustomer()
  mockAccountFetch(input)
  render(React.createElement(CustomerAccountPage, { locale: 'en' }))
  await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled())
}

function sectionByHeading(name: string) {
  const section = screen.getByRole('heading', { name }).closest('section')
  expect(section).toBeTruthy()
  return section as HTMLElement
}

// Scoped to the catalog card specifically: the panel now also renders an
// <article> per connected source above the catalog, and that summary card
// carries the same connector name and status badge. Matching the nearest
// <article> would land on the summary, where a "Connect source" button was
// never going to be — passing the assertion without proving anything.
function connectorCardByStatus(title: string, status: string) {
  const card = screen.getAllByText(title)
    .map((item) => item.closest<HTMLElement>('[data-slot="connector-card"]'))
    .find((candidate): candidate is HTMLElement =>
      Boolean(candidate && within(candidate).queryAllByText(status).length),
    )
  expect(card).toBeTruthy()
  return card
}

// The connector catalog collapses once a project already has input sources, and
// its trigger names the next click rather than the current state: "Add a data
// source" means collapsed, "Hide data sources" means expanded. Asserting on that
// label is the point — it is the only thing that tells an owner which way the
// catalog will go. Open it before asserting on picker cards.
function openConnectorPicker() {
  if (screen.queryByRole('button', { name: 'Hide data sources' })) return

  fireEvent.click(screen.getByRole('button', { name: 'Add a data source' }))
  expect(screen.getByRole('button', { name: 'Hide data sources' })).toBeTruthy()
}

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  window.localStorage?.clear()
  vi.unstubAllGlobals()
  vi.clearAllMocks()
})

describe('customer account UX rules', () => {
  it('opens the tested ERP through the ownership-checked handoff form', async () => {
    await renderAccount({ projects: [baseProject({ generationRuns: [baseRun({ status: 'erp_ready', erp: { status: 'ready', url: 'http://acme.localhost', testsStatus: 'passed' },  })] })] })
    const button = await screen.findByRole('button', { name: 'Open ERP' })
    expect(button.closest('form')?.getAttribute('action')).toBe('/api/customers/generation-runs/run-1/open')
    expect(button.closest('form')?.getAttribute('method')).toBe('post')
    expect(screen.queryByText(/Owner activation:/)).toBeNull()
    expect(screen.queryByRole('button', { name: 'Promote to production' })).toBeNull()
  })
  it('keeps a durable assistant turn retryable without background polling', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const token = 'public-interview-retry-token'
    const serverTurnKey = `interview-turn-${'a'.repeat(64)}`
    const initialMessage = {
      id: 'message-question',
      role: 'assistant',
      content: 'Who owns the release decision?',
      messageKind: 'question',
      questionKey: 'release_owner',
      createdAt: '2026-07-13T12:00:00.000Z',
    }
    const interview = {
      project: { companyName: 'Acme Foods', industry: 'manufacturing' },
      participant: { name: 'Quality Manager', roleTitle: 'Quality Manager', department: 'Quality' },
      request: { status: 'in_progress', questionSetKey: 'pending_work_release_owner' },
      pendingWork: { question: 'Who owns the release decision?', targetRole: 'Quality Manager' },
      completion: { completed: false },
      messages: [initialMessage],
    }
    let assistantAttempts = 0
    const fetchMock = vi.fn(async (request: RequestInfo | URL, init?: RequestInit) => {
      const url = String(request)
      if (url === `/api/interviews/${token}`) return jsonResponse({ interview })
      if (url.endsWith('/start')) return jsonResponse({ interview, sessionId: 'session-1' })
      if (url.endsWith('/messages')) {
        const submitted = JSON.parse(String(init?.body || '{}'))
        expect(submitted.turnKey).toMatch(/^interview-submission-/)
        return jsonResponse({
          message: {
            id: 'message-answer',
            role: 'user',
            content: 'The Quality Manager records the release before Warehouse can ship.',
            messageKind: 'answer',
            questionKey: 'release_owner',
            createdAt: '2026-07-13T12:01:00.000Z',
          },
          turn: { turnKey: serverTurnKey, status: 'queued' },
        }, 201)
      }
      if (url.endsWith('/assistant-response')) {
        expect(JSON.parse(String(init?.body || '{}'))).toEqual({ turnKey: serverTurnKey })
        assistantAttempts += 1
        if (assistantAttempts === 1) {
          return jsonResponse({
            decision: 'processing',
            retryable: true,
            completed: false,
            pollAfterMs: 30_000,
            turn: { turnKey: serverTurnKey, status: 'retry_wait' },
          }, 202)
        }
        return jsonResponse({
          decision: 'complete',
          completed: true,
          message: {
            id: 'message-completion',
            role: 'assistant',
            content: 'Thank you. We have enough information. This interview has been completed.',
            messageKind: 'completion',
            questionKey: 'release_owner',
            createdAt: '2026-07-13T12:02:00.000Z',
          },
          supervision: { supervised: true, workerAttempts: 1, supervisorAttempts: 1 },
          turn: { turnKey: serverTurnKey, status: 'completed' },
        })
      }
      throw new Error(`Unexpected public interview request: ${url} ${init?.method || 'GET'}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    render(React.createElement(PublicInterviewPage, { token }))
    const composer = await screen.findByPlaceholderText('…or type your answer here')
    fireEvent.change(composer, {
      target: { value: 'The Quality Manager records the release before Warehouse can ship.' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    await waitFor(() => expect(assistantAttempts).toBe(1))
    expect(fetchMock.mock.calls.filter(([request]) => String(request).endsWith('/messages'))).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy()
    await vi.advanceTimersByTimeAsync(30_000)
    expect(assistantAttempts).toBe(1)
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))

    // The stage swaps the composer for a closing banner once the turn completes,
    // so this is the participant-visible proof that the interview finished.
    await screen.findByText('Thank you — we have what we need.')
    expect(fetchMock.mock.calls.filter(([request]) => String(request).endsWith('/messages'))).toHaveLength(1)
    expect(fetchMock.mock.calls.filter(([request]) => String(request).endsWith('/assistant-response'))).toHaveLength(2)
  })

  it('offers an explicit retry for a saved user turn after the browser reopens', async () => {
    const token = 'public-interview-reopen-token'
    const serverTurnKey = `interview-turn-${'b'.repeat(64)}`
    const interview = {
      project: { companyName: 'Acme Foods', industry: 'manufacturing' },
      participant: { name: 'Quality Manager', roleTitle: 'Quality Manager', department: 'Quality' },
      request: { status: 'in_progress', questionSetKey: 'pending_work_release_owner' },
      pendingWork: { question: 'Who owns the release decision?', targetRole: 'Quality Manager' },
      completion: { completed: false },
      turn: { turnKey: serverTurnKey, status: 'retry_wait' },
      messages: [
        {
          id: 'message-question',
          role: 'assistant',
          content: 'Who owns the release decision?',
          messageKind: 'question',
          questionKey: 'release_owner',
          createdAt: '2026-07-13T12:00:00.000Z',
        },
        {
          id: 'message-answer',
          role: 'user',
          content: 'The Quality Manager records the release before Warehouse can ship.',
          messageKind: 'answer',
          questionKey: 'release_owner',
          createdAt: '2026-07-13T12:01:00.000Z',
        },
      ],
    }
    const fetchMock = vi.fn(async (request: RequestInfo | URL, init?: RequestInit) => {
      const url = String(request)
      if (url === `/api/interviews/${token}` || url.endsWith('/start')) return jsonResponse({ interview })
      if (url.endsWith('/assistant-response')) {
        expect(JSON.parse(String(init?.body || '{}'))).toEqual({ turnKey: serverTurnKey })
        return jsonResponse({
          decision: 'complete',
          completed: true,
          message: {
            id: 'message-completion',
            role: 'assistant',
            content: 'Thank you. We have enough information. This interview has been completed.',
            messageKind: 'completion',
            questionKey: 'release_owner',
            createdAt: '2026-07-13T12:02:00.000Z',
          },
          supervision: { supervised: true, workerAttempts: 1, supervisorAttempts: 1 },
          turn: { turnKey: serverTurnKey, status: 'completed' },
        })
      }
      throw new Error(`Unexpected public interview request: ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    render(React.createElement(PublicInterviewPage, { token }))

    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }))
    await screen.findByText('Thank you — we have what we need.')
    expect(fetchMock.mock.calls.filter(([request]) => String(request).endsWith('/messages'))).toHaveLength(0)
    expect(fetchMock.mock.calls.filter(([request]) => String(request).endsWith('/assistant-response'))).toHaveLength(1)
  })

  it('only clears the customer session for confirmed auth failures', () => {
    expect(shouldClearAuthSessionForCustomerProfileStatus(401)).toBe(true)
    expect(shouldClearAuthSessionForCustomerProfileStatus(403)).toBe(false)
    expect(shouldClearAuthSessionForCustomerProfileStatus(404)).toBe(false)
    expect(shouldClearAuthSessionForCustomerProfileStatus(500)).toBe(false)
    expect(shouldClearAuthSessionForCustomerProfileStatus(503)).toBe(false)
  })

  it('clears stale customer projects after the customer token expires', async () => {
    await renderAccount({
      projects: [baseProject({ companyName: 'Stale recovery project' })],
      generationProjectsResponse: async () => jsonResponse({ message: 'Unauthorized' }, 401),
    })

    await waitFor(() => expect(bookingState.current.openLogin).toHaveBeenCalled())
    expect(window.localStorage.getItem('customerSessionActive')).toBeNull()
    expect(screen.queryByText('Stale recovery project')).toBeNull()
  })

  it('keeps generic ERP environment creation hidden', () => {
    expect(shouldShowRuntimeEnvironmentCreateForm(0)).toBe(false)
    expect(shouldShowRuntimeEnvironmentCreateForm(1)).toBe(false)
  })

  it('selects only generated bundles for customer environment creation', () => {
    const bundles = [{ id: 'generated:run-1' }, { id: 'generated:run-2' }]

    expect(resolveGeneratedBundleSelection('', bundles)).toBe('generated:run-1')
    expect(resolveGeneratedBundleSelection('demo:sample_operations_erp', bundles)).toBe('generated:run-1')
    expect(resolveGeneratedBundleSelection('generated:run-2', bundles)).toBe('generated:run-2')
    expect(resolveGeneratedBundleSelection('demo:sample_operations_erp', [])).toBe('')
  })

  it('hides customer-unsafe agent artifacts from the account UI', () => {
    expect(shouldShowCustomerArtifact({ kind: 'compile_result', label: 'Compile result' })).toBe(true)
    expect(shouldShowCustomerArtifact({ kind: 'company_map', label: 'Company Map' })).toBe(false)
    expect(shouldShowCustomerArtifact({ kind: 'raw_artifact_json', label: 'Raw artifact JSON' })).toBe(false)
  })

  it('prioritizes newest unresolved blocking action items before the four-item display cap', () => {
    const items = [
      { id: 'resolved-1', kind: 'interview_request', status: 'resolved', blocking: true, createdAt: '2026-07-01T00:00:00Z' },
      { id: 'blocking-older', kind: 'interview_request', status: 'open', blocking: true, createdAt: '2026-07-02T00:00:00Z' },
      { id: 'nonblocking-1', kind: 'interview_request', status: 'open', blocking: false, createdAt: '2026-07-05T00:00:00Z' },
      { id: 'resolved-2', kind: 'interview_request', status: 'resolved', blocking: true, createdAt: '2026-07-03T00:00:00Z' },
      { id: 'blocking-newest', kind: 'evidence_collection_request', status: 'open', blocking: true, createdAt: '2026-07-06T00:00:00Z' },
    ]

    expect(prioritizeCustomerPendingWorkItems(items).slice(0, 4).map((item) => item.id)).toEqual([
      'blocking-newest',
      'blocking-older',
      'nonblocking-1',
      'resolved-2',
    ])
  })

  it('renders ERP feedback and change proposal review controls safely', async () => {
    await renderAccount({
      tenants: [baseTenant()],
      feedback: [{
        id: 'feedback-1',
        tenantId: 'tenant-1',
        sourceEnvironment: 'staging',
        category: 'missing_field',
        message: 'Finance keeps adding a missing billing entity field to invoices.',
        status: 'new',
        entityKey: 'invoices',
        recordId: '42',
        pagePath: '/entities/invoices/42',
      }],
      observations: [{
        id: 'observation-1',
        tenantId: 'tenant-1',
        sourceEnvironment: 'staging',
        status: 'new',
        kind: 'missing_field_pattern',
        severity: 'medium',
        title: 'Repeated missing field feedback',
        summary: 'Users submitted repeated feedback about missing fields.',
        recommendation: {
          proposalType: 'add_optional_field',
          summary: 'Create an optional billing entity field.',
          supportedByCurrentRuntime: true,
        },
        evidenceCount: 2,
        relatedEntityKey: 'sales_order',
      }],
      proposals: [{
        id: 'proposal-1',
        tenantId: 'tenant-1',
        feedbackId: 'feedback-1',
        status: 'proposed',
        proposalType: 'add_optional_field',
        title: 'Optional field request',
        summary: 'Add an optional billing entity field.',
        risk: 'low',
        supportedByCurrentRuntime: true,
        proposedChanges: [{ kind: 'company_map', nodes: [{ id: 'node-1' }] }],
      }],
    })

    expect(await screen.findByText('Feedback & improvements')).toBeTruthy()
    expect(screen.getByText('Finance keeps adding a missing billing entity field to invoices.')).toBeTruthy()
    expect(screen.getByText('What ERP Generator noticed')).toBeTruthy()
    expect(screen.getByText('Repeated missing field feedback')).toBeTruthy()
    expect(screen.getByText('Create an optional billing entity field.')).toBeTruthy()
    expect(screen.getByText('Optional field request')).toBeTruthy()
    expect(screen.getByText('Anything you accept is tested on a copy first. Your live ERP only changes when you confirm.')).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Analyze usage' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Create proposal' })).toBeNull()
    expect(screen.queryByText('Create proposal')).toBeNull()
    expect(screen.getByText('Accept')).toBeTruthy()
    expect(screen.getByText('Reject')).toBeTruthy()
    expect(screen.queryByText('company_map')).toBeNull()
    expect(screen.queryByText('node-1')).toBeNull()
  })

  it('never exposes operational staging after acceptance, including after automation terminalizes', async () => {
    await renderAccount({
      tenants: [baseTenant()],
      feedback: [{
        id: 'feedback-1',
        tenantId: 'tenant-1',
        sourceEnvironment: 'production',
        category: 'missing_field',
        message: 'Finance keeps adding a missing billing entity field to invoices.',
        status: 'proposed',
        entityKey: 'invoices',
      }],
      proposals: [{
        id: 'proposal-1',
        tenantId: 'tenant-1',
        feedbackId: 'feedback-1',
        status: 'accepted',
        proposalType: 'add_optional_field',
        title: 'Optional field request',
        summary: 'Add an optional billing entity field.',
        risk: 'low',
        supportedByCurrentRuntime: true,
        stagingAutomation: {
          status: 'terminal',
          attemptCount: 3,
          failureCode: 'change_staging_attempts_exhausted',
        },
        proposedChanges: [{ kind: 'add_optional_field', entityKey: 'invoices', fieldKey: 'billing_entity' }],
      }],
      jobs: [],
    })

    const feedbackSection = sectionByHeading('Feedback & improvements')
    expect(within(feedbackSection).queryByRole('button', { name: /Stage change for review/ })).toBeNull()
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([request]) =>
      String(request).includes('/api/customers/erp-change-proposals/proposal-1/stage'))).toBe(false)
  })

  it('does not require a second staging click while accepted change automation is queued', async () => {
    await renderAccount({
      tenants: [baseTenant()],
      proposals: [{
        id: 'proposal-1',
        tenantId: 'tenant-1',
        status: 'accepted',
        proposalType: 'add_optional_field',
        title: 'Optional field request',
        summary: 'Add an optional billing entity field.',
        risk: 'low',
        supportedByCurrentRuntime: true,
        stagingAutomation: { status: 'queued', attemptCount: 0 },
      }],
      jobs: [],
    })

    const feedbackSection = sectionByHeading('Feedback & improvements')
    expect(within(feedbackSection).queryByText('Preparing and testing your ERP...')).toBeNull()
    expect(within(feedbackSection).queryByRole('button', { name: /Stage change for review/ })).toBeNull()
  })

  it('renders existing staged change jobs without raw patch JSON', async () => {
    await renderAccount({
      tenants: [baseTenant()],
      proposals: [{
        id: 'proposal-1',
        tenantId: 'tenant-1',
        feedbackId: 'feedback-1',
        status: 'implemented_in_staging',
        proposalType: 'add_optional_field',
        title: 'Optional field request',
        summary: 'Add an optional billing entity field.',
        risk: 'low',
        supportedByCurrentRuntime: true,
      }],
      jobs: [{
        id: 'job-1',
        proposalId: 'proposal-1',
        tenantId: 'tenant-1',
        status: 'staged_for_review',
        stageStatus: 'staged_for_review',
        patchedBundleVersion: '0.1.0-cp-proposal_1',
        stagingUrl: 'http://acme-staging.erpgenerator.test',
        patchSummary: 'Added optional Billing entity field to invoices.',
        patchPlanJson: {
          operations: [{ type: 'add_optional_field', entityKey: 'invoices', fieldKey: 'billing_entity' }],
          company_map: { nodes: [{ id: 'node-1' }] },
        },
      }],
    })

    const feedbackSection = sectionByHeading('Feedback & improvements')

    expect(await within(feedbackSection).findByText('Optional field request')).toBeTruthy()
    expect(within(feedbackSection).queryByRole('link', { name: /Open for review/ })).toBeNull()
    expect(within(feedbackSection).queryByText('patchPlanJson')).toBeNull()
    expect(within(feedbackSection).queryByText('company_map')).toBeNull()
    expect(within(feedbackSection).queryByText('node-1')).toBeNull()
  })

  it('does not expose stage or production application controls for change proposals', async () => {
    await renderAccount({ tenants: [baseTenant()], proposals: [{
      id: 'proposal-1', tenantId: 'tenant-1', feedbackId: 'feedback-1',
      status: 'accepted', proposalType: 'add_optional_field', title: 'Optional field request',
      summary: 'Add an optional billing entity field.', risk: 'low', supportedByCurrentRuntime: true,
    }] })
    const feedbackSection = sectionByHeading('Feedback & improvements')
    expect(await within(feedbackSection).findByText('Optional field request')).toBeTruthy()
    expect(within(feedbackSection).queryByRole('button', { name: /Apply to production/ })).toBeNull()
    expect(within(feedbackSection).queryByRole('link', { name: /Open for review|Open production ERP/ })).toBeNull()
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([request]) => String(request).includes('/apply-production'))).toBe(false)
  })

  it('does not show ERP environment creation before a generated bundle exists', async () => {
    await renderAccount({ projects: [], tenants: [] })

    expect(screen.queryByRole('heading', { name: 'ERP lifecycle / environments' })).toBeNull()
    expect(screen.queryByText('Create ERP environment')).toBeNull()
  })

  it('routes the empty state to the guided ERP creation wizard instead of an inline form', async () => {
    await renderAccount({ projects: [] })

    const createLink = await screen.findByRole('link', { name: 'Create your ERP' })
    expect(createLink.getAttribute('href')).toBe('/en/account/create')

    // The company descriptors are now collected step-by-step in the wizard, so
    // the account page must not expose the old inline discovery-project form.
    expect(screen.queryByLabelText('Industry')).toBeNull()
    expect(screen.queryByLabelText('Company type')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Create discovery project' })).toBeNull()
  })

  it('does not invent manufacturing for a project without an industry', async () => {
    await renderAccount({
      projects: [baseProject({ industry: undefined, companyType: undefined, generationRuns: [] })],
    })

    expect(await screen.findByText('Acme Foods')).toBeTruthy()
    expect(screen.queryByText(/manufacturing/i)).toBeNull()
  })

  it('keeps a generated bundle in preparation until its ERP is ready', async () => {
    await renderAccount({ projects: [baseProject({ status: 'generated' })] })

    expect(await screen.findByText('Acme Foods')).toBeTruthy()

    const progress = screen.getByRole('region', { name: 'Where things stand' })
    expect(within(progress).getByText('Step 3 of 4')).toBeTruthy()
    expect(within(progress).getByText('Building your ERP')).toBeTruthy()
    expect(within(progress).getByText('99%')).toBeTruthy()
    expect(within(progress).getByText('Preparing and testing your ERP...')).toBeTruthy()
    expect(within(progress).queryByText('Your ERP is ready.')).toBeNull()

    expect(screen.queryByText(/compile_check/)).toBeNull()
    expect(screen.queryByText(/run-1/)).toBeNull()
    expect(screen.queryByText('Technical details')).toBeNull()
    expect(screen.queryByText('Developer source details')).toBeNull()
  })

  it('GO requests ERP deployment and acceptance tests', async () => {
    await renderAccount({ projects: [baseProject({ generationRuns: [] })] })

    fireEvent.click(await screen.findByRole('button', { name: /^GO$/ }))

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledWith(
      '/api/customers/generation-projects/project-1/start-generation',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({
          deployToRuntime: true,
        }),
      }),
    ))
    expect(screen.queryByRole('button', { name: /Create staging environment/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /Create ERP environment/ })).toBeNull()
  })

  it('shows a terminal internal incident without GO or customer retry controls', async () => {
    await renderAccount({
      projects: [baseProject({
        generationRuns: [baseRun({
          status: 'failed',
          generatedErp: undefined,
          lastError: 'Agent generation failed.',
        })],
      })],
    })

    expect(await screen.findByText('Generation stopped before your ERP could be verified. Your evidence is saved and your prepared demo remains available.')).toBeTruthy()
    expect(screen.queryByRole('button', { name: /^GO$/ })).toBeNull()
    expect(screen.queryByText('Resume')).toBeNull()
  })

  it('keeps the generation action primary and secondary surfaces collapsed', async () => {
    const project = baseProject({ generationRuns: [] })
    await renderAccount({ projects: [project] })

    const go = await screen.findByRole('button', { name: /^GO$/ })
    const connect = screen.getByRole('button', { name: /Connect source/ })
    expect(Boolean(go.compareDocumentPosition(connect) & Node.DOCUMENT_POSITION_FOLLOWING)).toBe(true)

    expect(screen.getByRole('heading', { name: 'Profile' }).closest('details')?.open).toBe(false)
    expect(screen.getByRole('heading', { name: 'Demo' }).closest('details')?.open).toBe(false)
    expect(screen.getByRole('heading', { name: 'Feedback & improvements' }).closest('details')?.open).toBe(false)

    expect(screen.getByRole('link', { name: 'Create another ERP' }).getAttribute('href')).toBe('/en/account/create')
    expect(screen.queryByRole('heading', { name: 'ERP lifecycle / environments' })).toBeNull()
  })

  it('offers Notion only even when the control plane advertises legacy integrations', async () => {
    await renderAccount({ projects: [baseProject({ availableConnectors: ['notion', 'google-drive', 'sharepoint', 'slack'], generationRuns: [] })] })
    openConnectorPicker()
    const cards = document.querySelectorAll('[data-slot="connector-card"]')
    expect(cards).toHaveLength(1)
    expect(within(cards[0] as HTMLElement).getByText('Notion')).toBeTruthy()
  })

  it('does not present unconfigured connectors as customer health problems', async () => {
    await renderAccount({
      projects: [baseProject({
        availableConnectors: [],
        generationRuns: [],
      })],
    })

    await screen.findByText('Company data')
    expect(screen.getByRole('button', { name: /Connect source/ })).toHaveProperty('disabled', true)
    expect(screen.queryByText(/Coming soon/)).toBeNull()
  })

  it('shows Notion as enabled only when the control plane advertises it', async () => {
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        generationRuns: [],
      })],
    })

    const sharePointCard = (await screen.findAllByText('Notion'))
      .map((element) => element.closest('[data-slot="connector-card"]'))
      .find((card): card is HTMLElement => Boolean(card))
    expect(sharePointCard).toBeTruthy()
    expect(within(sharePointCard as HTMLElement).getByText('Available')).toBeTruthy()
    expect(within(sharePointCard as HTMLElement).getByRole('button', { name: /Connect source/ })).toHaveProperty('disabled', false)

    expect(screen.queryByText(/Coming soon/)).toBeNull()
  })

  it('shows the real connected state for a Notion source', async () => {
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [{
          id: 'source-notion',
          provider: 'notion',
          displayName: 'Company Notion',
          status: 'ready',
          lastConnectionStatus: 'ready',
        }],
        generationRuns: [],
      })],
    })

    openConnectorPicker()
    const sharePointCard = (await screen.findAllByText('Notion'))
      .map((element) => element.closest('[data-slot="connector-card"]'))
      .find((card): card is HTMLElement => Boolean(card))
    expect(sharePointCard).toBeTruthy()
    expect(within(sharePointCard as HTMLElement).getAllByText('Connected').length).toBeGreaterThan(0)
    expect(within(sharePointCard as HTMLElement).getByRole('button', { name: 'Connected' })).toHaveProperty('disabled', true)


  })

  it('renders a connected source once with its connector logo and one status', async () => {
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [{
          id: 'source-notion',
          provider: 'notion',
          displayName: 'Notion',
          status: 'ready',
          lastConnectionStatus: 'ready',
        }],
        generationRuns: [],
      })],
    })

    const source = document.querySelector<HTMLElement>('[data-slot="connected-source"]')
    expect(source).toBeTruthy()
    expect(within(source as HTMLElement).getAllByText('Notion')).toHaveLength(1)
    expect(within(source as HTMLElement).getAllByText(/^Connected$/i)).toHaveLength(1)
    expect(source?.querySelector('img[src="/connectors/notion.svg"]')).toBeTruthy()
  })

  it('shows clear progress while checking a connected source', async () => {
    let finishCheck!: (response: Response) => void
    const checkResponse = new Promise<Response>((resolve) => {
      finishCheck = resolve
    })
    const inputSources = [{
      id: 'source-notion',
      provider: 'notion',
      displayName: 'Notion',
      status: 'ready',
      lastConnectionStatus: 'ready',
    }]

    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources,
        generationRuns: [],
      })],
      refreshSourceResponse: () => checkResponse,
    })

    fireEvent.click(screen.getByRole('button', { name: 'Check source' }))

    const checking = await screen.findByRole('button', { name: 'Checking…' })
    expect(checking).toHaveProperty('disabled', true)
    expect(checking.getAttribute('aria-busy')).toBe('true')
    expect(globalThis.fetch).toHaveBeenCalledWith(
      '/api/customers/generation-projects/project-1/input-sources/source-notion/refresh',
      { method: 'POST' },
    )

    finishCheck(jsonResponse(inputSources[0]))

    await waitFor(() => {
      const ready = screen.getByRole('button', { name: 'Check source' })
      expect(ready).toHaveProperty('disabled', false)
      expect(ready.getAttribute('aria-busy')).toBe('false')
    })
  })

  it('disables GO while the API reports that connector evidence is synchronizing', async () => {
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [{
          id: 'source-notion',
          provider: 'notion',
          displayName: 'Notion',
          status: 'syncing',
        }],
        generationRuns: [],
        canStartGeneration: false,
        generationBlockedReason: 'connector_syncing',
      })],
    })

    const go = await screen.findByRole('button', { name: 'GO' })
    expect(go).toHaveProperty('disabled', true)
    expect(screen.getByText('Waiting for connected company data to finish syncing.')).toBeTruthy()
    fireEvent.click(go)
    expect(vi.mocked(globalThis.fetch).mock.calls.some(
      ([request]) => String(request).endsWith('/start-generation'),
    )).toBe(false)
  })

  it('shows synchronization failures as retryable without claiming authorization was lost', async () => {
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [{
          id: 'source-notion',
          provider: 'notion',
          displayName: 'Notion',
          status: 'sync_failed',
          lastError: 'The connector synchronization failed. Check the source and try again.',
        }],
        generationRuns: [],
        canStartGeneration: false,
        generationBlockedReason: 'connector_sync_failed',
      })],
    })

    expect(await screen.findByText('Sync failed')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Check source' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Reconnect' })).toBeNull()
    expect(screen.queryByText('Needs reconnect')).toBeNull()
    expect(screen.getByText(/could not be synchronized/)).toBeTruthy()
  })

  it('offers reconnection only for a real connector authorization failure', async () => {
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [{
          id: 'source-notion',
          provider: 'notion',
          displayName: 'Notion',
          status: 'requires_reconnect',
          lastError: 'The connector needs to be authorized again.',
        }],
        generationRuns: [],
        canStartGeneration: false,
        generationBlockedReason: 'connector_reconnect_required',
      })],
    })

    expect(await screen.findByText('Needs reconnect')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Reconnect' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Check source' })).toBeNull()
    expect(screen.getByText(/must be authorized again/)).toBeTruthy()
  })

  it('checks a pending Notion authorization without reconnecting or starting synchronization', async () => {
    const source = {
      id: 'source-notion',
      provider: 'notion',
      displayName: 'Notion',
      status: 'connecting',
      scopeMode: 'unconfigured',
    }
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [source],
        generationRuns: [],
        canStartGeneration: false,
      })],
      reconcileSourcesResponse: async () => jsonResponse({ sources: [{
        ...source,
        status: 'awaiting_scope',
        requiresSelection: true,
        generationBlockReason: 'connector_scope_required',
      }] }),
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Check source' }))

    expect(await screen.findByRole('button', { name: 'Choose content' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Reconnect' })).toBeNull()
    const requests = vi.mocked(fetch).mock.calls.map(([url]) => String(url))
    expect(requests.some((url) => url.endsWith('/input-sources/reconcile'))).toBe(true)
    expect(requests.some((url) => url.endsWith('/refresh') || url.endsWith('/nango/connect-session'))).toBe(false)
  })

  it('imports completed Notion synchronization during guest polling and then stops checking the ready source', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    setSignedInCustomer()
    bookingState.current.customer!.isGuest = true
    const source = {
      id: 'source-notion',
      provider: 'notion',
      displayName: 'Notion',
      status: 'syncing',
      scopeMode: 'selected_roots',
      generationBlockReason: 'connector_syncing',
    }
    let project = baseProject({
      inputSources: [source],
      generationRuns: [],
      canStartGeneration: false,
      generationBlockedReason: 'connector_syncing',
    })
    mockAccountFetch({
      generationProjectsResponse: async () => jsonResponse({ projects: [project] }),
      reconcileSourcesResponse: async () => {
        const ready = { ...source, status: 'ready', generationBlockReason: undefined, lastRecordCount: 161, lastTextRecordCount: 161 }
        project = { ...project, inputSources: [ready], canStartGeneration: true, generationBlockedReason: undefined }
        return jsonResponse({ sources: [ready] })
      },
    })
    render(React.createElement(CustomerAccountPage, { locale: 'en' }))
    const go = await screen.findByRole('button', { name: 'GO' })
    expect(go).toHaveProperty('disabled', true)

    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })

    expect(screen.getByRole('button', { name: 'GO' })).toHaveProperty('disabled', false)
    expect(screen.queryByText('Waiting for connected company data to finish syncing.')).toBeNull()
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    const requests = vi.mocked(fetch).mock.calls.map(([url]) => String(url))
    expect(requests.filter((url) => url.endsWith('/input-sources/reconcile'))).toHaveLength(1)
    expect(requests.some((url) => url.endsWith('/refresh') || url.endsWith('/nango/connect-session'))).toBe(false)
  })

  it.each(['resources', 'selection'])('recovers a missing Notion authorization after %s fails', async (operation) => {
    const source = {
      id: 'source-notion',
      provider: 'notion',
      displayName: 'Notion',
      status: 'awaiting_scope',
      requiresSelection: true,
      scopeMode: 'unconfigured',
      lastWarning: 'Choose which Notion content ERP Generator may use.',
    }
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [source],
        generationRuns: [],
        canStartGeneration: false,
        generationBlockedReason: 'connector_scope_required',
      })],
      connectorResourcesResponse: async () => jsonResponse({ message: 'Notion content could not be listed.' }, 409),
      connectorSelectionResponse: async () => jsonResponse({ message: 'The connector content selection could not be saved.' }, 409),
      reconcileSourcesResponse: async () => jsonResponse({ sources: [{
        ...source,
        status: 'requires_reconnect',
        lastError: 'The connector authorization no longer exists.',
        generationBlockReason: 'connector_reconnect_required',
      }] }),
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Choose content' }))
    if (operation === 'resources')
      fireEvent.click(await screen.findByRole('radio', { name: /Choose specific areas/ }))
    else
      fireEvent.click(await screen.findByRole('button', { name: 'Save and sync' }))

    expect(await screen.findByRole('button', { name: 'Reconnect' })).toBeTruthy()
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByText('Needs reconnect')).toBeTruthy()
    expect(screen.getByText('The connector authorization no longer exists.')).toBeTruthy()
    expect(screen.queryByText(source.lastWarning)).toBeNull()
    expect(screen.queryByRole('button', { name: 'Choose content' })).toBeNull()
    expect(screen.getByRole('button', { name: 'GO' })).toHaveProperty('disabled', true)
  })

  it('defaults Notion to an unlimited all-accessible scope before starting its first sync', async () => {
    const source = {
      id: 'source-notion',
      provider: 'notion',
      displayName: 'Notion',
      status: 'awaiting_scope',
      requiresSelection: true,
      scopeMode: 'unconfigured',
      selectedResourceCount: 0,
      lastRecordCount: 0,
      lastTextRecordCount: 0,
    }
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [source],
        generationRuns: [],
        canStartGeneration: false,
        generationBlockedReason: 'connector_scope_required',
      })],
      connectorResourcesResponse: async () => jsonResponse({
        resources: [
          {
            id: 'page-operations',
            title: 'Operations handbook',
            kind: 'page',
            selected: false,
          },
          {
            id: 'database-suppliers',
            title: 'Supplier backlog',
            kind: 'database',
            selected: false,
          },
          {
            id: 'supplier-row-1',
            title: 'Supplier order 001',
            kind: 'database_row',
            parentId: 'database-suppliers',
            selected: false,
          },
        ],
      }),
      connectorSelectionResponse: async () => jsonResponse({
        ...source,
        status: 'syncing',
        requiresSelection: false,
        scopeMode: 'all_accessible',
        selectedResourceCount: 0,
        lastSyncRequestedAt: '2026-07-26T12:00:00Z',
        generationBlockReason: 'connector_syncing',
      }),
    })

    const sourceCard = document.querySelector<HTMLElement>('[data-slot="connected-source"]')
    expect(sourceCard).toBeTruthy()
    expect(screen.getByRole('button', { name: 'GO' })).toHaveProperty('disabled', true)
    expect(screen.getByText(/Finish selecting or synchronizing connected company data/)).toBeTruthy()
    fireEvent.click(within(sourceCard as HTMLElement).getByRole('button', { name: 'Choose content' }))

    expect(await screen.findByRole('heading', { name: 'Choose what ERP Generator can use' })).toBeTruthy()
    expect(screen.getByText('Everything shared with ERP Generator — Recommended')).toBeTruthy()
    expect(screen.getByText('Everything shared with this connection will be synchronized.')).toBeTruthy()
    expect(screen.getByText('You do not need to select or review individual items.')).toBeTruthy()
    expect(screen.queryByText('Operations handbook')).toBeNull()
    expect(screen.queryByText('Supplier backlog')).toBeNull()
    expect(screen.queryByText('Supplier order 001')).toBeNull()
    const save = screen.getByRole('button', { name: 'Save and sync' })
    expect(save).toHaveProperty('disabled', false)

    expect(vi.mocked(globalThis.fetch).mock.calls.some(
      ([request]) => String(request).endsWith('/input-sources/source-notion/resources'),
    )).toBe(false)

    fireEvent.click(save)

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledWith(
      '/api/customers/generation-projects/project-1/input-sources/source-notion/selection',
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({
          mode: 'all_accessible',
          includedRootIds: [],
          excludedIds: [],
          includeDescendants: true,
          includeNewContent: true,
        }),
      }),
    ))
    expect((await screen.findAllByText('Syncing…')).length).toBeGreaterThan(0)
  })

  it('shows specific Notion content as one stable tree with implicit descendants', async () => {
    const source = {
      id: 'source-notion',
      provider: 'notion',
      displayName: 'Notion',
      status: 'awaiting_scope',
      requiresSelection: true,
      scopeMode: 'unconfigured',
      selectedResourceCount: 0,
    }
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [source],
        generationRuns: [],
        canStartGeneration: false,
        generationBlockedReason: 'connector_scope_required',
      })],
      connectorResourcesResponse: async () => jsonResponse({
        resources: [
          {
            id: 'root',
            title: 'Dandelion Chocolate',
            kind: 'page',
            selected: false,
          },
          {
            id: 'page-00',
            title: '00_Start_Here',
            kind: 'subpage',
            parentId: 'root',
            sortIndex: 0,
            selected: false,
          },
          {
            id: 'page-01',
            title: '01_Company_Profile',
            kind: 'subpage',
            parentId: 'root',
            sortIndex: 1,
            selected: false,
          },
          {
            id: 'database-workflows',
            title: 'Workflows',
            kind: 'database',
            parentId: 'root',
            sortIndex: 2,
            selected: false,
          },
          {
            id: 'workflow-row',
            title: 'WF-001',
            kind: 'database_row',
            parentId: 'database-workflows',
            selected: false,
          },
        ],
      }),
      connectorSelectionResponse: async () => jsonResponse({
        ...source,
        status: 'syncing',
        requiresSelection: false,
        scopeMode: 'selected_roots',
        includedRootIds: ['root'],
        selectedResourceCount: 1,
        generationBlockReason: 'connector_syncing',
      }),
    })

    const sourceCard = document.querySelector<HTMLElement>('[data-slot="connected-source"]')
    fireEvent.click(within(sourceCard as HTMLElement).getByRole('button', { name: 'Choose content' }))
    fireEvent.click(await screen.findByRole('radio', { name: /Choose specific areas/ }))

    const resourceList = await waitFor(() => {
      const list = screen.getByRole('dialog')
        .querySelector<HTMLElement>('[data-slot="connector-resource-list"]')
      expect(list).toBeTruthy()
      expect(within(list as HTMLElement).getByText('01_Company_Profile')).toBeTruthy()
      return list as HTMLElement
    })
    expect(
      [...resourceList.querySelectorAll('strong')].map((element) => element.textContent),
    ).toEqual([
      'Dandelion Chocolate',
      '00_Start_Here',
      '01_Company_Profile',
      'Workflows',
    ])
    expect(within(resourceList).queryByText('WF-001')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull()
    expect(screen.queryByText('Include subpages and database rows')).toBeNull()
    expect(screen.getByText(
      'Selecting a page includes its subpages. Selecting a database includes all rows. New content inside a selected area is included automatically.',
    )).toBeTruthy()

    const checkboxes = within(resourceList).getAllByRole('checkbox')
    fireEvent.click(checkboxes[0])
    expect(checkboxes[1]).toHaveProperty('disabled', true)
    expect(checkboxes[1].getAttribute('data-state')).toBe('checked')
    fireEvent.click(screen.getByRole('button', { name: 'Save and sync' }))

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledWith(
      '/api/customers/generation-projects/project-1/input-sources/source-notion/selection',
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({
          mode: 'selected_roots',
          includedRootIds: ['root'],
          excludedIds: [],
          includeDescendants: true,
          includeNewContent: true,
        }),
      }),
    ))
  })

  it('disconnects the provider connection and removes its source card after confirmation', async () => {
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [{
          id: 'source-notion',
          provider: 'notion',
          displayName: 'Notion',
          status: 'ready',
          selectedResourceCount: 1,
          lastRecordCount: 1,
          lastTextRecordCount: 1,
        }],
        generationRuns: [],
      })],
    })

    const sourceCard = document.querySelector<HTMLElement>('[data-slot="connected-source"]')
    expect(sourceCard).toBeTruthy()
    fireEvent.click(within(sourceCard as HTMLElement).getByRole('button', { name: 'Disconnect' }))

    const dialog = await screen.findByRole('alertdialog')
    expect(within(dialog).getByText(/temporary synchronized content is removed from ERP Generator immediately/i)).toBeTruthy()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Disconnect' }))

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledWith(
      '/api/customers/generation-projects/project-1/input-sources/source-notion',
      { method: 'DELETE' },
    ))
    await waitFor(() =>
      expect(document.querySelector('[data-slot="connected-source"]')).toBeNull())
  })

  it('connects Notion without requiring customer-entered connection details', async () => {
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null)

    await renderAccount({ projects: [baseProject()] })

    openConnectorPicker()
    const notionCard = (await screen.findByText('Available')).closest('article')
    expect(notionCard).toBeTruthy()
    fireEvent.click(within(notionCard as HTMLElement).getByRole('button', { name: /Connect source/ }))
    expect(await screen.findByRole('heading', { name: 'Before connecting company data' })).toBeTruthy()
    const continueButton = screen.getByRole('button', { name: 'Continue to connector' })
    expect(continueButton).toHaveProperty('disabled', true)
    expect(vi.mocked(globalThis.fetch).mock.calls.some(
      ([request]) => String(request).includes('/nango/connect-session'),
    )).toBe(false)
    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(continueButton)

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledWith(
      '/api/customers/generation-projects/project-1/nango/connect-session',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({
          connectorKey: 'notion',
          displayName: 'Notion',
          authorizationAccepted: true,
        }),
      }),
    ))
    expect(openSpy).toHaveBeenCalledWith('https://connect.nango.test/?session_token=session-token', '_blank', 'noopener,noreferrer')
    expect((await screen.findAllByText('Notion')).length).toBeGreaterThan(0)
    // Connecting collapses the picker once the source is attached; reopen it to
    // confirm the connector card now reflects the in-flight connecting state.
    openConnectorPicker()
    const connectingButtons = await screen.findAllByRole('button', { name: /Connecting/ })
    expect(connectingButtons[0]).toHaveProperty('disabled', true)
    expect(screen.queryByText('Provider config key')).toBeNull()
  })

  it('shows a Notion connection failure inside the active connector panel', async () => {
    const failureMessage = 'Document connection could not be started. Please retry or contact support.'
    await renderAccount({
      projects: [baseProject({
        availableConnectors: ['notion'],
        inputSources: [],
      })],
      connectSourceResponse: async () => jsonResponse({ message: failureMessage }, 502),
    })

    const sharePointCard = (await screen.findAllByText('Notion'))
      .map((element) => element.closest('[data-slot="connector-card"]'))
      .find((card): card is HTMLElement => Boolean(card))
    expect(sharePointCard).toBeTruthy()
    fireEvent.click(within(sharePointCard as HTMLElement).getByRole('button', { name: /Connect source/ }))
    fireEvent.click(await screen.findByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Continue to connector' }))

    const connectorPanel = screen.getByText('Company data').closest('[data-slot="connectors-panel"]')
    expect(connectorPanel).toBeTruthy()
    expect((await within(connectorPanel as HTMLElement).findByRole('alert')).textContent).toBe(failureMessage)
  })

  it('renders pre-created Notion source as connecting instead of available', async () => {
    await renderAccount({
      projects: [baseProject({
        inputSources: [{
          id: 'source-1',
          sourceType: 'nango',
          provider: 'notion',
          sourceFamily: 'files_documents',
          displayName: 'Notion',
          status: 'draft',
          lastConnectionStatus: 'connect_session_created',
          nangoProviderConfigKey: 'notion-files',
        }],
      })],
    })

    openConnectorPicker()
    const connectingButtons = await screen.findAllByRole('button', { name: /Connecting/ })
    expect(connectingButtons[0]).toHaveProperty('disabled', true)
    const googleDriveCard = connectorCardByStatus('Notion', 'Connecting...')
    expect(googleDriveCard).toBeDefined()
    expect(within(googleDriveCard!).queryByRole('button', { name: /^Connect source$/ })).toBeNull()
  })

  it('renders connected Notion source as connected instead of reconnectable', async () => {
    await renderAccount({
      projects: [baseProject({
        inputSources: [{
          id: 'source-1',
          sourceType: 'nango',
          provider: 'notion',
          sourceFamily: 'files_documents',
          displayName: 'Notion',
          status: 'connected',
          lastConnectionStatus: 'auth_connected',
          nangoProviderConfigKey: 'notion-files',
        }],
      })],
    })

    openConnectorPicker()
    const connectedButtons = await screen.findAllByRole('button', { name: /^Connected$/ })
    expect(connectedButtons[0]).toHaveProperty('disabled', true)
    const googleDriveCard = connectorCardByStatus('Notion', 'Connected')
    expect(googleDriveCard).toBeDefined()
    expect(within(googleDriveCard!).queryByRole('button', { name: /^Connect source$/ })).toBeNull()
  })

  it.each([
    {
      connector: 'Notion',
      provider: 'notion',
      availableConnectors: ['notion'],
      newerStatus: 'error',
      newerConnectionStatus: 'auth_failed',
    },
    {
      connector: 'Notion',
      provider: 'notion',
      availableConnectors: ['notion'],
      newerStatus: 'connecting',
      newerConnectionStatus: 'connect_session_created',
    },
  ])('keeps an existing ready $connector source connected when a newer source is $newerStatus', async ({
    connector,
    provider,
    availableConnectors,
    newerStatus,
    newerConnectionStatus,
  }) => {
    await renderAccount({
      projects: [baseProject({
        inputSourceCount: 2,
        availableConnectors,
        inputSources: [
          {
            id: 'source-newer',
            provider,
            displayName: connector,
            status: newerStatus,
            lastConnectionStatus: newerConnectionStatus,
          },
          {
            id: 'source-ready',
            provider,
            displayName: connector,
            status: 'ready',
            lastConnectionStatus: 'sync_success',
          },
        ],
      })],
    })

    openConnectorPicker()
    const connectorCard = connectorCardByStatus(connector, 'Connected')
    expect(within(connectorCard!).getByRole('button', { name: 'Connected' })).toHaveProperty('disabled', true)
    expect(within(connectorCard!).queryByRole('button', { name: /^Connect source$/ })).toBeNull()
  })

  it('does not show manual interview planning controls to normal customers', async () => {
    await renderAccount({ projects: [baseProject()] })

    expect(await screen.findByText('Acme Foods')).toBeTruthy()
    expect(screen.queryByText('Interview planning')).toBeNull()
    expect(screen.queryByText('Plan CEO interview')).toBeNull()
    expect(screen.queryByText('Plan next interviews')).toBeNull()
  })

  it('keeps auto-created pending interview work actionable without manual create controls', async () => {
    const writeText = vi.fn()
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    })
    const projectWithMissingEmailInvite = baseProject({
      requests: [{
        id: 'request-1',
        status: 'planned',
        questionSetKey: 'finance_lead',
        inviteDelivery: {
          status: 'skipped_no_email',
          provider: 'none',
          attemptCount: 0,
        },
      }],
      generationRuns: [
        baseRun({
          status: 'waiting_for_input',
          pendingWorkItems: [{
            id: 'pending-interview',
            kind: 'interview_request',
            status: 'in_progress',
            blocking: true,
            targetRole: 'finance_lead',
            question: 'Who approves invoice drafts?',
            sourceStage: 'discovery_assessment',
            linkedInterviewRequestId: 'request-1',
          }],
        }),
      ],
    })

    await renderAccount({
      projects: [projectWithMissingEmailInvite],
      sendInviteResponse: {
        requestId: 'request-1',
        inviteLink: 'http://localhost/interviews/regenerated-token',
        inviteDelivery: {
          status: 'skipped_no_email',
          provider: 'none',
          attemptCount: 0,
        },
      },
    })

    // Interview work is no longer duplicated into the pending-work list; the
    // Interviews panel is the single place it lives, so it must carry the whole
    // job: who is being asked, what they were asked, and how to reach them.
    const interviews = sectionByHeading('Interviews')
    expect(await within(interviews).findByText('Who approves invoice drafts?')).toBeTruthy()
    expect(within(interviews).getByText('Not sent yet')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Generate with assumptions' })).toBeTruthy()

    expect(screen.queryByText('Interview planning')).toBeNull()
    expect(screen.queryByRole('button', { name: /Create interview/ })).toBeNull()

    // No email is known for this role, so the only way out is a link the owner
    // can carry to whatever channel they actually reach this person on.
    fireEvent.click(within(interviews).getByRole('button', { name: 'Regenerate invite link' }))
    expect(await within(interviews).findByRole('button', { name: 'Resend invite' })).toBeTruthy()

    fireEvent.click(within(interviews).getByRole('button', { name: 'Copy invite link' }))
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('http://localhost/interviews/regenerated-token'))
  })

  it('shows an unresolved interview even when it is the oldest of five requests', async () => {
    const completedRequests = Array.from({ length: 4 }, (_, index) => ({
      id: `completed-request-${index + 1}`,
      status: 'completed',
      questionSetKey: `completed_role_${index + 1}`,
      completedAt: `2026-07-${String(16 - index).padStart(2, '0')}T12:00:00.000Z`,
    }))

    await renderAccount({
      projects: [baseProject({
        // The API returns newest requests first. The still-actionable request
        // can therefore be last and must not disappear behind a display cap.
        requests: [
          ...completedRequests,
          {
            id: 'oldest-unresolved-request',
            status: 'invited',
            questionSetKey: 'sales_lead',
            sentAt: '2026-07-10T12:00:00.000Z',
            inviteDelivery: {
              status: 'sent',
              recipientEmail: 'sales@example.test',
              provider: 'fake',
              attemptCount: 1,
            },
          },
        ],
        generationRuns: [baseRun({
          status: 'waiting_for_input',
          pendingWorkItems: [{
            id: 'oldest-pending-interview',
            kind: 'interview_request',
            status: 'in_progress',
            blocking: true,
            targetRole: 'sales_lead',
            question: 'How does an inquiry become a confirmed order?',
            sourceStage: 'discovery_assessment',
            linkedInterviewRequestId: 'oldest-unresolved-request',
          }],
        })],
      })],
    })

    const interviews = sectionByHeading('Interviews')
    expect(within(interviews).getAllByRole('listitem')).toHaveLength(5)
    expect(within(interviews).getByText('How does an inquiry become a confirmed order?')).toBeTruthy()
    expect(within(interviews).getByText('sales@example.test')).toBeTruthy()
    expect(within(interviews).getByText('Waiting for their answer')).toBeTruthy()
  })

  it('keeps a sent known-email interview copyable without exposing retry controls', async () => {
    const writeText = vi.fn()
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    })
    const inviteLink = 'https://app.erpgenerator.example/interviews/legacy-copy-token'
    await renderAccount({
      copyInviteResponse: { inviteLink },
      projects: [baseProject({
        requests: [{
          id: 'request-1',
          status: 'invited',
          questionSetKey: 'finance_lead',
          inviteDelivery: {
            status: 'sent',
            recipientEmail: 'finance@example.test',
            provider: 'fake',
            attemptCount: 1,
          },
        }],
        generationRuns: [baseRun({
          status: 'waiting_for_input',
          pendingWorkItems: [{
            id: 'pending-interview',
            kind: 'interview_request',
            status: 'in_progress',
            blocking: true,
            targetRole: 'finance_lead',
            question: 'Who approves invoice drafts?',
            sourceStage: 'erp_design',
            linkedInterviewRequestId: 'request-1',
          }],
        })],
      })],
    })

    expect((await screen.findAllByText(/finance@example\.test/)).length).toBeGreaterThan(0)
    expect(screen.queryByRole('button', { name: /Regenerate invite link/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /Resend invite/ })).toBeNull()
    const copyInvite = screen.getAllByRole('button', { name: /Copy invite/ })
    expect(copyInvite.length).toBeGreaterThan(0)
    fireEvent.click(copyInvite[0])
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(inviteLink))
    expect((globalThis.fetch as any).mock.calls.some(([request]: [RequestInfo | URL]) =>
      String(request).includes('/copy-link'))).toBe(true)
    expect((globalThis.fetch as any).mock.calls.some(([request]: [RequestInfo | URL]) =>
      String(request).includes('/send-invite'))).toBe(false)
  })

  it('uses targeted project refresh only when a realtime event arrives', async () => {
    await renderAccount({
      projects: [baseProject({ generationRuns: [baseRun({ status: 'running' })] })],
    })
    expect(await screen.findByText('Acme Foods')).toBeTruthy()

    const broadListCallsBefore = (globalThis.fetch as any).mock.calls
      .filter(([request]: [RequestInfo | URL]) => String(request) === '/api/customers/generation-projects')
      .length

    const targetedCallsBefore = (globalThis.fetch as any).mock.calls
      .filter(([request]: [RequestInfo | URL]) =>
        String(request) === '/api/customers/generation-projects/project-1')
      .length
    expect(targetedCallsBefore).toBe(0)

    for (const handler of realtimeState.handlers.get('projectChanged') ?? []) {
      handler({ projectId: 'project-1' })
    }

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledWith(
      '/api/customers/generation-projects/project-1',
      expect.objectContaining({ method: 'GET' }),
    ))
    const broadListCallsAfter = (globalThis.fetch as any).mock.calls
      .filter(([request]: [RequestInfo | URL]) => String(request) === '/api/customers/generation-projects')
      .length

    expect(broadListCallsAfter).toBe(broadListCallsBefore)
  })

  it('renders contradiction pending work as clarification needed', async () => {
    await renderAccount({
      projects: [
        baseProject({
          generationRuns: [
            baseRun({
              status: 'waiting_for_input',
              pendingWorkItems: [{
                id: 'pending-contradiction',
                kind: 'contradiction_resolution_request',
                status: 'open',
                blocking: true,
                targetRole: 'COO or Purchasing Manager',
                question: 'Who approves purchase orders above 10000 EUR?',
                reason: 'Approval owner conflict for purchase orders above 10000.',
                sourceStage: 'discovery_supervision',
              }],
            }),
          ],
        }),
      ],
    })

    expect(await screen.findByText('Who approves purchase orders above 10000 EUR?')).toBeTruthy()
    expect(screen.getByText(/COO or Purchasing Manager/)).toBeTruthy()
    expect(screen.getByText('We found conflicting information about this workflow. Answer the question or add a resolution note to continue.')).toBeTruthy()
    expect(screen.queryByText('contradiction_resolution_request')).toBeNull()
    expect(screen.queryByText('Company Map')).toBeNull()
  })

  it('does not show GO and Resume together for a resumable waiting run', async () => {
    await renderAccount({
      projects: [
        baseProject({
          generationRuns: [
            baseRun({
              status: 'waiting_for_input',
              generatedErp: undefined,
              pendingWorkItems: [],
            }),
          ],
        }),
      ],
    })

    // The progress panel holds the bar where it is and names what we are waiting
    // for, instead of the old disabled-action box that implied the owner had a
    // move to make.
    const progress = await screen.findByRole('region', { name: 'Where things stand' })
    expect(within(progress).getByText('Waiting for more company documents before we continue.')).toBeTruthy()
    expect(screen.queryByText('Resume')).toBeNull()
    expect(screen.queryByText('GO')).toBeNull()
  })

  it('keeps internal artifact repair out of the answer-needed journey step', () => {
    const project = baseProject({
      generationRuns: [baseRun({
        status: 'running',
        generatedErp: undefined,
        pendingWorkItems: [],
      })],
    })

    expect(discoveryJourneyStep(project as any)).toBe(2)
  })

  it('narrates ERP design as work in progress without customer actions', async () => {
    await renderAccount({
      projects: [baseProject({
        generationRuns: [baseRun({
          status: 'running',
          stages: [{ id: 'stage-1', stage: 'erp_design', status: 'running' }],
          generatedErp: undefined,
          pendingWorkItems: [],
        })],
      })],
    })

    const progress = await screen.findByRole('region', { name: 'Where things stand' })
    expect(within(progress).getByText('Building your ERP')).toBeTruthy()
    expect(within(progress).getByText('ERP Generator is building and testing your ERP.')).toBeTruthy()

    // Repair is the agent's problem, not the owner's: the run recovers itself and
    // must never ask for a hand.
    expect(screen.queryByText('Support needed')).toBeNull()
    expect(screen.queryByLabelText('Resolution note')).toBeNull()
    expect(screen.queryByRole('button', { name: /^GO$/ })).toBeNull()
    expect(screen.queryByText('Resume')).toBeNull()
  })

  // Staging and production used to be offered side by side. A live production ERP
  // makes staging a detour, so the card now commits to one primary action and the
  // owner-activation block rides along with it.
  it('offers one ERP open action after acceptance passes', async () => {
    await renderAccount({
      tenants: [baseTenant({
        productionDeploymentStatus: 'production',
        activeProductionBundleVersion: '0.1.0',
      })],
      projects: [
        baseProject({
          tenantId: 'tenant-1',
          generationRuns: [
            baseRun({
              status: 'erp_ready',
              erp: {
                status: 'ready', testsStatus: 'passed',
                url: 'http://acme-staging.erpgenerator.test',
                bundleVersion: '0.1.0',
                activation: {
                  status: 'pending',
                  activationUrlAvailable: false,
                  activationEnvironment: 'staging',
                  activationEmail: 'owner@example.test',
                },
              },

            }),
          ],
        }),
      ],
    })

    const discoverySection = sectionByHeading('ERP discovery projects')

    expect(await within(discoverySection).findByText('Open ERP')).toBeTruthy()
    expect(within(discoverySection).queryByText('Open staging ERP')).toBeNull()

    expect(within(discoverySection).queryByText(/Owner activation:/)).toBeNull()
    expect(screen.queryByRole('heading', { name: 'ERP lifecycle / environments' })).toBeNull()
  })

  it('keeps one ERP action without manual environment controls', async () => {
    await renderAccount({
      tenants: [baseTenant({ productionDeploymentStatus: 'production' })],
      projects: [
        baseProject({
          tenantId: 'tenant-1',
          generationRuns: [
            baseRun({
              status: 'erp_ready',
              erp: {
                status: 'ready',
                url: 'http://acme-staging.erpgenerator.test',
                bundleVersion: '0.1.0',
                testsStatus: 'passed',
              },

            }),
          ],
        }),
      ],
    })

    const discoverySection = sectionByHeading('ERP discovery projects')
    expect(await within(discoverySection).findByText('Open ERP')).toBeTruthy()
    expect(within(discoverySection).queryByRole('button', { name: /Reset staging/ })).toBeNull()
    expect(within(discoverySection).queryByRole('button', { name: /Deploy to staging/ })).toBeNull()
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([request]) =>
      /\/generation-runs\/run-1\/(reset|deploy)-staging/.test(String(request)))).toBe(false)
  })

  // The "resetting" status box is gone with the rest of the environment boxes, so
  // there is no longer a reset state to read. What still matters is what stays
  // absent: a reset that resumes on its own after a web-process restart must not
  // grow a button inviting the owner to kick it.
  it('does not expose an ERP while preparation is restarting', async () => {
    await renderAccount({
      projects: [
        baseProject({
          tenantId: 'tenant-1',
          generationRuns: [baseRun({
            status: 'completed',
            erp: { status: 'resetting' },

          })],
        }),
      ],
    })

    const discoverySection = sectionByHeading('ERP discovery projects')
    expect(await within(discoverySection).findByText('Acme Foods')).toBeTruthy()
    expect(within(discoverySection).queryByRole('button', { name: /Resetting|Reset staging/ })).toBeNull()
    // Nothing to open either: the staging URL is meaningless mid-reset.
    expect(within(discoverySection).queryByText('Open staging ERP')).toBeNull()
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([request]) =>
      /\/generation-runs\/run-1\/(reset|deploy)-staging/.test(String(request)))).toBe(false)
  })




  it('does not offer ERP access before readiness', async () => {
    await renderAccount({
      tenants: [baseTenant({
        stagingDeploymentStatus: 'deploying',
      })],
      projects: [
        baseProject({
          tenantId: 'tenant-1',
          generationRuns: [
            baseRun({
              status: 'completed',
              erp: {
                status: 'deploying',
                bundleVersion: '0.1.0',
                bundleHash: 'hash-safe',
              },

            }),
          ],
        }),
      ],
    })

    const discoverySection = sectionByHeading('ERP discovery projects')

    expect(await within(discoverySection).findByText('Acme Foods')).toBeTruthy()
    expect(within(discoverySection).queryByRole('button', { name: /Promote to production/ })).toBeNull()
    expect(within(discoverySection).queryByText('Open production ERP')).toBeNull()
    // `promoteDisabledReason` is the engine explaining itself to us, not to the
    // owner — it must not reach the screen verbatim.
    expect(screen.queryByText(/Staging ERP is not ready yet/)).toBeNull()
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([request]) =>
      String(request).includes('/promote-production'))).toBe(false)
  })

  it('does not render Company Map or raw artifact JSON if they are present in run data', async () => {
    await renderAccount({
      projects: [
        baseProject({
          generationRuns: [
            baseRun({
              artifacts: [
                { id: 'artifact-1', kind: 'company_map', label: 'Company Map', status: 'completed' },
                { id: 'artifact-2', kind: 'raw_artifact_json', label: 'Raw artifact JSON', status: 'completed' },
                { id: 'artifact-3', kind: 'compile_result', label: 'Compile result', status: 'completed' },
              ],
            }),
          ],
        }),
      ],
    })

    // The account UI stopped listing run artifacts entirely, so there is no safe
    // artifact left to act as a positive control. The company name stands in for
    // one: it proves the card really rendered and these labels were withheld,
    // rather than the whole project silently failing to mount.
    expect(await screen.findByText('Acme Foods')).toBeTruthy()
    expect(screen.queryByText(/Company Map/)).toBeNull()
    expect(screen.queryByText(/Raw artifact JSON/)).toBeNull()
    expect(screen.queryByText(/company_map/)).toBeNull()
    expect(screen.queryByText(/raw_artifact_json/)).toBeNull()
  })
})
