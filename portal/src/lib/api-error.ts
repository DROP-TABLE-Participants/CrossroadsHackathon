const MAXIMUM_API_ERROR_MESSAGE_LENGTH = 1_000

export async function readApiErrorMessage(response: Response, fallback: string) {
  if (!response.headers.get('content-type')?.toLowerCase().includes('application/json')) {
    return fallback
  }

  try {
    const payload = await response.json() as { message?: unknown }
    const message = typeof payload.message === 'string' ? payload.message.trim() : ''
    return message && message.length <= MAXIMUM_API_ERROR_MESSAGE_LENGTH
      ? message
      : fallback
  } catch {
    return fallback
  }
}
