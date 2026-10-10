import { describe, expect, it } from 'vitest'

import { readApiErrorMessage } from '@/lib/api-error'

describe('API error messages', () => {
  it('returns a bounded JSON API message', async () => {
    const response = Response.json({
      message: 'Project goal is required and must be 4000 characters or fewer.',
      code: null,
    }, { status: 400 })

    await expect(readApiErrorMessage(response, 'Fallback error')).resolves.toBe(
      'Project goal is required and must be 4000 characters or fewer.',
    )
  })

  it('falls back for non-JSON, missing, or unbounded messages', async () => {
    await expect(readApiErrorMessage(
      new Response('<h1>Gateway error</h1>', {
        status: 502,
        headers: { 'content-type': 'text/html' },
      }),
      'Fallback error',
    )).resolves.toBe('Fallback error')

    await expect(readApiErrorMessage(
      Response.json({ message: 'x'.repeat(1_001) }, { status: 500 }),
      'Fallback error',
    )).resolves.toBe('Fallback error')
  })
})
