'use client'

import { CheckCircle, Keyboard, PaperPlaneTilt } from '@phosphor-icons/react'
import { useCallback, useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'

import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'
import { useVoiceDictation } from './useVoiceDictation'
import { VoiceAura } from './VoiceAura'

export type InterviewMessage = {
  id: string
  role: string
  content: string
}

/**
 * The interview surface: a voice stage on the left, the conversation and a
 * keyboard fallback on the right, split by a rule.
 *
 * The flow is meant to feel like a phone call. Once a question is on screen the
 * microphone arms itself and waits — ElevenLabs' voice detection ignores an
 * empty room, so nothing is transcribed until someone actually speaks. Stop
 * talking for two and a half seconds and the answer posts itself into the
 * conversation. No buttons in the happy path.
 */
export function InterviewStage({
  interviewToken,
  messages,
  question,
  assistantThinking,
  completed,
  canAnswer,
  composer,
  onComposerChange,
  onSubmit,
  sending,
  className,
}: {
  interviewToken: string
  messages: InterviewMessage[]
  question: string
  assistantThinking: boolean
  completed: boolean
  /** False while the assistant is thinking or the interview is over. */
  canAnswer: boolean
  composer: string
  onComposerChange: (value: string) => void
  onSubmit: (event?: FormEvent<HTMLFormElement>, spokenText?: string) => void | Promise<void>
  sending: boolean
  className?: string
}) {
  const transcriptEndRef = useRef<HTMLDivElement>(null)
  const busy = sending || assistantThinking

  // A finished utterance is the answer — post it and let the agent reply.
  const submitSpoken = useCallback((text: string) => {
    void onSubmit(undefined, text)
  }, [onSubmit])

  const voice = useVoiceDictation({
    interviewToken,
    languageCode: 'en',
    onCommitted: submitSpoken,
  })

  const { start, stop, status } = voice
  const voiceUnavailable = status === 'unsupported' || status === 'denied' || status === 'error'

  // Nothing listens until the participant asks for it. Once they have opted in,
  // the mic re-arms between turns on its own so the conversation keeps flowing —
  // the click is consent to use the microphone, not a push-to-talk button.
  const [voiceEnabled, setVoiceEnabled] = useState(false)

  useEffect(() => {
    if (!voiceEnabled || voiceUnavailable) return
    if (canAnswer && status === 'idle') {
      void start()
      return
    }
    // Drop the mic while the agent is thinking so we never transcribe dead air.
    if (!canAnswer && (status === 'listening' || status === 'connecting')) {
      stop()
    }
  }, [voiceEnabled, canAnswer, status, start, stop, voiceUnavailable])

  const toggleVoice = () => {
    if (voiceEnabled) {
      stop()
      setVoiceEnabled(false)
      return
    }
    setVoiceEnabled(true)
    if (canAnswer) void start()
  }

  useEffect(() => {
    transcriptEndRef.current?.scrollIntoView({ block: 'end' })
  }, [messages.length, assistantThinking])

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      if (composer.trim() && canAnswer && !busy) void onSubmit()
    }
  }

  const listening = status === 'listening'
  const voiceHeadline = completed
    ? 'Interview complete'
    : voiceUnavailable
      ? 'Type your answers on the right'
      : !voiceEnabled
        ? 'Click the circle to answer out loud'
        : assistantThinking
          ? 'One moment…'
          : listening
            ? 'Listening — just answer out loud'
            : status === 'connecting'
              ? 'Getting ready…'
              : 'Waiting for the next question…'

  return (
    // Fills the viewport slot it is given; the thread scrolls inside, the page
    // itself never does.
    <div
      className={cn(
        'font-sans grid h-full min-h-0 grid-rows-[auto_1fr] overflow-hidden rounded-xl border bg-card lg:grid-cols-[3fr_2fr] lg:grid-rows-1',
        className,
      )}
    >
      {/* Voice — the main event, and the bigger half. */}
      <div className="flex min-h-0 flex-col items-center justify-center gap-3 overflow-hidden bg-gradient-to-b from-primary/[0.05] to-transparent p-4 sm:gap-5 sm:p-8">
        <button
          type="button"
          onClick={toggleVoice}
          disabled={completed || voiceUnavailable}
          aria-pressed={voiceEnabled}
          aria-label={voiceEnabled ? 'Turn the microphone off' : 'Click to answer out loud'}
          className={cn(
            'rounded-full outline-none transition-transform focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-4 focus-visible:ring-offset-background',
            !completed && !voiceUnavailable && 'cursor-pointer hover:scale-[1.02] active:scale-100',
            // Resting state reads as an invitation rather than a live mic.
            !voiceEnabled && 'opacity-70 hover:opacity-100',
          )}
        >
          {/* Scales with the viewport so the whole page fits without scrolling —
              the xl variant's fixed 448px would blow out a laptop or a phone. */}
          <VoiceAura
            status={status}
            mediaStream={voice.mediaStream}
            size="xl"
            className="h-[7.5rem] sm:h-[11rem] lg:h-[14rem] xl:h-[18rem]"
          />
        </button>

        <div className="flex flex-col items-center gap-1.5 text-center">
          <p className="text-base font-medium">{voiceHeadline}</p>
          {!voiceEnabled && !completed && !voiceUnavailable ? (
            <p className="text-sm text-muted-foreground">
              Your microphone stays off until you do.
            </p>
          ) : null}
          {listening ? (
            <p className="text-sm text-muted-foreground">
              Pause for a moment when you’re done and I’ll write it down.
            </p>
          ) : null}
          {voice.interimText ? (
            <p aria-live="polite" className="max-w-md pt-2 text-sm text-muted-foreground italic">
              {voice.interimText}…
            </p>
          ) : null}
        </div>

        {voiceUnavailable && !completed ? (
          <p className="max-w-xs text-center text-xs text-muted-foreground">
            {status === 'denied'
              ? 'Your browser blocked the microphone. Allow it to talk, or type your answer instead.'
              : 'Speaking isn’t available on this device — typing works just as well.'}
          </p>
        ) : null}
      </div>

      {/* Conversation + keyboard fallback — the smaller half, behind the rule. */}
      <div className="flex min-h-0 flex-col border-t lg:border-t-0 lg:border-l">
        {/* A label, never the question itself. The question lives in the thread
            below — echoing it up here is what made the page look broken. */}
        <div className="flex items-center justify-between gap-2 border-b px-5 py-3">
          <p className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
            Conversation
          </p>
          {!completed ? (
            <p className="text-xs text-muted-foreground">
              {messages.filter((message) => message.role === 'user').length
                ? 'Answers saved as you go'
                : 'Answer out loud or type'}
            </p>
          ) : null}
        </div>

        <div className="flex-1 space-y-3 overflow-y-auto px-5 py-4" aria-live="polite">
          {/* Before the first message loads, show the question we already know
              so the participant is never staring at an empty panel. */}
          {!messages.length ? (
            <div className="max-w-[92%] rounded-lg bg-muted px-3 py-2 text-sm leading-relaxed">
              {question}
            </div>
          ) : null}
          {messages.map((message) => (
            <div
              key={message.id}
              className={cn(
                'max-w-[92%] rounded-lg px-3 py-2 text-sm leading-relaxed',
                message.role === 'user'
                  ? 'ml-auto bg-primary text-primary-foreground'
                  : 'bg-muted text-foreground',
              )}
            >
              {message.content}
            </div>
          ))}
          {assistantThinking ? (
            <div className="flex items-center gap-2 text-sm text-muted-foreground">
              <Spinner className="size-3.5" />
              Thinking…
            </div>
          ) : null}
          <div ref={transcriptEndRef} />
        </div>

        {completed ? (
          <div className="flex items-center gap-2 border-t bg-success/5 px-5 py-4 text-sm">
            <CheckCircle aria-hidden="true" className="size-4 shrink-0 text-success" weight="fill" />
            <span>Thank you — we have what we need.</span>
          </div>
        ) : (
          <form className="flex flex-col gap-2 border-t p-4" onSubmit={onSubmit}>
            <label className="sr-only" htmlFor="interview-answer">Type your answer</label>
            <Textarea
              id="interview-answer"
              rows={2}
              value={composer}
              onChange={(event) => onComposerChange(event.target.value)}
              onKeyDown={onKeyDown}
              placeholder="…or type your answer here"
              disabled={busy}
              className="resize-none"
            />
            <div className="flex items-center justify-between gap-2">
              <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
                <Keyboard aria-hidden="true" className="size-3.5" />
                Prefer typing? Go ahead.
              </span>
              <Button type="submit" size="sm" disabled={!composer.trim() || !canAnswer || busy}>
                {busy ? <Spinner data-icon="inline-start" /> : <PaperPlaneTilt data-icon="inline-start" />}
                Send
              </Button>
            </div>
          </form>
        )}
      </div>
    </div>
  )
}
