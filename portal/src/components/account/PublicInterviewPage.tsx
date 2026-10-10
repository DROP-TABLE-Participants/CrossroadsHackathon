'use client'

import Image from 'next/image'
import Link from 'next/link'
import { ChatText as MessageSquareText } from '@phosphor-icons/react'
import { FormEvent, useEffect, useMemo, useRef, useState } from 'react'

import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { InterviewStage } from '@/components/interviews/InterviewStage'
import { assistantOwesReply } from '@/lib/interviews/assistant-state'

type PublicInterviewMessage = {
  id: string
  role: 'user' | 'assistant' | 'system'
  content: string
  messageKind: 'question' | 'answer' | 'follow_up' | 'completion' | 'system'
  questionKey?: string
  createdAt: string
}

type PublicInterview = {
  project: {
    companyName: string
    industry?: string
    goal?: string
  }
  participant: {
    name: string
    roleTitle?: string
    department?: string
  }
  request: {
    status: string
    questionSetKey: string
    expiresAt?: string
  }
  pendingWork?: {
    id?: string
    question?: string
    targetRole?: string
    sourceStage?: string
    status?: string
  }
  completion?: {
    completed: boolean
    completedAt?: string
  }
  turn?: {
    turnKey: string
    status: string
    availableAt?: string
  }
  messages: PublicInterviewMessage[]
}

export function PublicInterviewPage({ token }: { token: string }) {
  const [interview, setInterview] = useState<PublicInterview | null>(null)
  const [messages, setMessages] = useState<PublicInterviewMessage[]>([])
  const [composer, setComposer] = useState('')
  const [loading, setLoading] = useState(true)
  const [sending, setSending] = useState(false)
  const [assistantThinking, setAssistantThinking] = useState(false)
  const [assistantRetryPending, setAssistantRetryPending] = useState(false)
  const [assistantTerminal, setAssistantTerminal] = useState(false)
  const [completed, setCompleted] = useState(false)
  const [error, setError] = useState('')
  const transcriptEndRef = useRef<HTMLDivElement>(null)
  const assistantPollInFlightRef = useRef(false)
  const activeTurnKeyRef = useRef('')
  const submissionTurnKeyRef = useRef('')

  useEffect(() => {
    let cancelled = false

    async function loadInterview() {
      setLoading(true)
      setError('')

      try {
        const response = await fetch(`/api/interviews/${encodeURIComponent(token)}`, {
          cache: 'no-store',
        })
        const data = await response.json().catch(() => ({}))

        if (!response.ok) {
          setError(data?.message || 'This interview link is not available.')
          return
        }

        if (cancelled) return
        applyInterview(data.interview)

        if (data.interview?.request?.status !== 'completed') {
          const startResponse = await fetch(`/api/interviews/${encodeURIComponent(token)}/start`, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
          })
          const startData = await startResponse.json().catch(() => ({}))
          if (!cancelled && startResponse.ok && startData.interview) {
            applyInterview(startData.interview)
          }
        }
      } catch {
        if (!cancelled) setError('Unable to load this interview.')
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    loadInterview()

    return () => {
      cancelled = true
    }
  }, [token])

  useEffect(() => {
    if (typeof transcriptEndRef.current?.scrollIntoView === 'function') {
      transcriptEndRef.current.scrollIntoView({ block: 'end' })
    }
  }, [assistantThinking, messages.length])

  // Keep "Thinking…" tied to the saved conversation, so an interrupted request
  // still shows that the assistant owes a response after a reload.
  const awaitingAssistant = assistantOwesReply({
    completed,
    terminal: assistantTerminal,
    sending,
    lastMessageRole: messages.at(-1)?.role,
  })

  const participantText = useMemo(
    () => interview ? participantLabel(interview.participant) : 'Interview participant',
    [interview],
  )
  // Just the first name for the greeting — "Hello, Maria" reads warmer than the
  // full "name, role, department" label the header already carries.
  const greetingName = useMemo(() => {
    const name = interview?.participant.name?.trim()
    return name ? name.split(/\s+/)[0] : ''
  }, [interview])
  const pendingQuestion = interview?.pendingWork?.question || interview?.messages?.find((message) => message.messageKind === 'question')?.content || ''

  function applyInterview(nextInterview: PublicInterview | undefined) {
    if (!nextInterview) return
    setInterview(nextInterview)
    setMessages(nextInterview.messages || [])
    const isCompleted = nextInterview.completion?.completed === true || nextInterview.request?.status === 'completed'
    setCompleted(isCompleted)
    activeTurnKeyRef.current = isCompleted ? '' : nextInterview.turn?.turnKey || ''
    if (!isCompleted && activeTurnKeyRef.current) {
      setAssistantRetryPending(true)
      setError('Your saved answer is waiting for the assistant. Select Try again to continue.')
    }
  }

  // `spokenText` lets a finished utterance submit itself directly, instead of
  // writing to `composer` state and racing the next render to read it back.
  async function sendMessage(event?: FormEvent<HTMLFormElement>, spokenText?: string) {
    event?.preventDefault()
    if (!interview || completed || sending || assistantThinking || assistantRetryPending) return

    const content = (spokenText ?? composer).replace(/\s+/g, ' ').trim()
    if (!content) return

    setSending(true)
    setError('')
    setComposer('')
    const submissionTurnKey = submissionTurnKeyRef.current || `interview-submission-${crypto.randomUUID()}`
    submissionTurnKeyRef.current = submissionTurnKey

    try {
      const messageResponse = await fetch(`/api/interviews/${encodeURIComponent(token)}/messages`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ content, turnKey: submissionTurnKey }),
      })
      const messageData = await messageResponse.json().catch(() => ({}))
      if (!messageResponse.ok || !messageData.message) {
        if (messageData?.turn?.turnKey) activeTurnKeyRef.current = String(messageData.turn.turnKey)
        setError(messageData?.message || 'Unable to save your answer.')
        setComposer(content)
        return
      }

      const savedTurnKey = typeof messageData?.turn?.turnKey === 'string' ? messageData.turn.turnKey : ''
      if (!savedTurnKey) {
        setError('The saved answer turn could not be verified. Please retry the same answer.')
        setComposer(content)
        return
      }
      submissionTurnKeyRef.current = ''
      activeTurnKeyRef.current = savedTurnKey
      setMessages((current) => [...current, messageData.message])
      setSending(false)
      await requestAssistantResponse()
    } catch {
      setComposer(content)
      setError('We could not continue this interview right now. Please try again.')
    } finally {
      setSending(false)
    }
  }

  async function requestAssistantResponse() {
    const expectedTurnKey = activeTurnKeyRef.current
    if (!expectedTurnKey || assistantPollInFlightRef.current || completed || assistantTerminal) return
    assistantPollInFlightRef.current = true
    setAssistantThinking(true)
    setError('')

    try {
      const assistantResponse = await fetch(`/api/interviews/${encodeURIComponent(token)}/assistant-response`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ turnKey: expectedTurnKey }),
      })
      const assistantData = await assistantResponse.json().catch(() => ({}))
      if (assistantData?.turn?.turnKey && assistantData.turn.turnKey !== expectedTurnKey) {
        setAssistantRetryPending(false)
        setAssistantTerminal(true)
        setError('The assistant returned a different saved answer turn, so it was rejected.')
        return
      }
      if (assistantResponse.status === 202 &&
          (assistantData.decision === 'processing' || assistantData.decision === 'awaiting_improvement')) {
        setAssistantRetryPending(true)
        setError('Your answer is saved and still processing. Try again in a moment.')
        return
      }
      if (!assistantResponse.ok || !assistantData.message || typeof assistantData.message === 'string') {
        const retryable = assistantData.retryable !== false
        setAssistantRetryPending(retryable)
        setAssistantTerminal(!retryable)
        setError(
          typeof assistantData.message === 'string'
            ? assistantData.message
            : 'We could not continue this interview right now. Please try again.',
        )
        return
      }

      setMessages((current) => current.some((message) => message.id === assistantData.message.id)
        ? current
        : [...current, assistantData.message])
      setAssistantRetryPending(false)
      setAssistantTerminal(false)
      activeTurnKeyRef.current = ''
      if (assistantData.completed === true) {
        setCompleted(true)
      }
    } catch {
      setAssistantRetryPending(true)
      setError('Your answer is saved, but the assistant could not respond. Try again in a moment.')
    } finally {
      assistantPollInFlightRef.current = false
      setAssistantThinking(false)
    }
  }

  if (loading) {
    return (
      <div className="font-sans flex h-svh flex-col items-center justify-center gap-3 bg-background text-muted-foreground">
        <Spinner className="size-6" />
        <span className="text-sm">Loading interview...</span>
      </div>
    )
  }

  if (error && !interview) {
    return (
      <section className="font-sans flex min-h-svh items-center justify-center p-4">
        <article className="flex max-w-md flex-col gap-4 rounded-lg border p-4">
          <h1 className="text-2xl font-semibold">This interview link is not available</h1>
          <p role="alert" className="text-sm text-muted-foreground">{error}</p>
          <Button asChild variant="outline"><Link href="/">ERP Generator home</Link></Button>
        </article>
      </section>
    )
  }

  if (!interview) return null

  return (
    // h-svh (not h-screen) so mobile browser chrome can't push the composer off
    // the bottom. Nothing here scrolls; the conversation scrolls inside itself.
    <div className="font-sans flex h-svh flex-col overflow-hidden bg-background">
      <header className="flex shrink-0 items-center justify-between gap-3 border-b px-4 py-2.5 sm:px-6">
        <Link href="/" aria-label="ERP Generator home" className="shrink-0">
          <Image src="/erpgenerator-logo.svg" alt="ERP Generator" width={104} height={24} style={{ height: 'auto' }} />
        </Link>
        <div className="flex min-w-0 flex-col text-right">
          <strong className="truncate text-sm leading-tight font-semibold">
            {interview.project.companyName}
          </strong>
          <span className="truncate text-xs leading-tight text-muted-foreground">{participantText}</span>
        </div>
      </header>

      <main className="flex min-h-0 flex-1 flex-col gap-2 p-3 sm:p-4">
        <div className="shrink-0 px-1">
          <h1 className="text-2xl font-semibold">
            {greetingName ? `Hello, ${greetingName}` : 'Hello'}
          </h1>
          <p className="text-sm text-muted-foreground">
            {interview.project.companyName} would like your help. Please answer the question below — out loud or by typing.
          </p>
        </div>

        <InterviewStage
          className="min-h-0 flex-1"
          interviewToken={token}
          messages={messages}
          question={pendingQuestion || 'Loading the first question…'}
          assistantThinking={awaitingAssistant}
          completed={completed}
          canAnswer={!completed && !awaitingAssistant && !assistantTerminal}
          composer={composer}
          onComposerChange={setComposer}
          onSubmit={sendMessage}
          sending={sending}
        />

        {/* The response is normally synchronous. If the request is interrupted,
            the saved turn can be retried explicitly without background polling. */}
        {assistantRetryPending && error && !completed ? (
          <div className="shrink-0">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={assistantThinking}
              onClick={() => void requestAssistantResponse()}
            >
              {assistantThinking ? <Spinner data-icon="inline-start" /> : <MessageSquareText data-icon="inline-start" />}
              {assistantThinking ? 'Checking…' : 'Try again'}
            </Button>
          </div>
        ) : null}

        {error ? (
          <Alert variant="destructive" className="shrink-0">
            <MessageSquareText />
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}
      </main>

      {/* Completion replaces the active interview with its saved status. */}
      {completed ? (
        <div
          role="status"
          aria-live="polite"
          className="fixed inset-0 z-50 flex items-center justify-center bg-background p-4"
        >
          <div className="flex max-w-sm flex-col items-center gap-4 text-center">
            <div className="relative flex size-12 items-center justify-center">
              <svg
                viewBox="0 0 52 52"
                className="relative size-12"
                fill="none"
                aria-hidden="true"
              >
                <circle cx="26" cy="26" r="24" className="stroke-success/25" strokeWidth="3" />
                <path
                  d="M15.5 27 l7 7 l14 -15"
                  className="stroke-success"
                  strokeWidth="4"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
              </svg>
            </div>
            <div className="flex flex-col gap-1.5">
              <h2 className="text-2xl font-semibold">
                {greetingName ? `Thank you, ${greetingName}` : 'Thank you'}
              </h2>
              <p className="text-sm text-muted-foreground">
                We have everything we need for {interview.project.companyName}. You can close this window now.
              </p>
            </div>
            <Link
              href="/"
              className="text-sm font-medium text-primary underline-offset-4 hover:underline"
            >
              ERP Generator home
            </Link>
          </div>
        </div>
      ) : null}
    </div>
  )
}

function participantLabel(participant: PublicInterview['participant']) {
  const name = participant.name?.trim()
  const role = participant.roleTitle?.trim()
  const department = participant.department?.trim()
  const parts = [
    name && role && name.toLowerCase() === role.toLowerCase() ? '' : name,
    role,
    department && role && department.toLowerCase() === role.toLowerCase() ? '' : department,
  ].filter(Boolean)

  return parts.length > 0 ? parts.join(', ') : 'Interview participant'
}
