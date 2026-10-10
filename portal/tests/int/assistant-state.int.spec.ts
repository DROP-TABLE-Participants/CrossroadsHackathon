import { describe, expect, it } from 'vitest'

import { assistantOwesReply } from '@/lib/interviews/assistant-state'

describe('assistantOwesReply', () => {
  const idle = { completed: false, terminal: false, sending: false }

  it('waits after the participant answer is saved', () => {
    expect(assistantOwesReply({ ...idle, lastMessageRole: 'user' })).toBe(true)
  })

  it('waits as soon as the answer is posting, before it joins the transcript', () => {
    expect(assistantOwesReply({ ...idle, sending: true, lastMessageRole: 'assistant' })).toBe(true)
  })

  it('does not wait when the assistant has already spoken last', () => {
    expect(assistantOwesReply({ ...idle, lastMessageRole: 'assistant' })).toBe(false)
  })

  it('does not wait on a fresh interview that has only a question', () => {
    expect(assistantOwesReply({ ...idle, lastMessageRole: undefined })).toBe(false)
  })

  it('stops waiting once the interview completes, even mid-turn', () => {
    expect(assistantOwesReply({ ...idle, completed: true, lastMessageRole: 'user' })).toBe(false)
  })

  it('stops waiting on a terminal failure', () => {
    expect(assistantOwesReply({ ...idle, terminal: true, lastMessageRole: 'user' })).toBe(false)
    expect(assistantOwesReply({ ...idle, terminal: true, sending: true })).toBe(false)
  })
})
