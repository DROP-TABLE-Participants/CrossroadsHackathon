/**
 * Whether the assistant still owes the participant a reply.
 *
 * This is conversation state, not request state: it remains true after a saved
 * answer if the assistant response was interrupted and must be retried.
 */
export function assistantOwesReply(input: {
  completed: boolean
  terminal: boolean
  /** The answer is posting but is not in the transcript yet. */
  sending: boolean
  lastMessageRole?: string
}): boolean {
  if (input.completed || input.terminal) return false
  return input.sending || input.lastMessageRole === 'user'
}
