'use client'

import { useEffect, useMemo, useState } from 'react'
import Link from 'next/link'
import { useRouter, useSearchParams } from 'next/navigation'

import { normalizeAdmin, useAdminSession } from '@/components/admin/AdminSessionProvider'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Field, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import type { Locale } from '@/i18n/config'

function adminTarget(value: string | null, locale: Locale) {
  const fallback = `/${locale}/admin`
  if (!value?.startsWith(fallback) || value.startsWith(`/${locale}/admin/login`)) return fallback
  return value
}

export function AdminLoginForm({ locale }: { locale: Locale }) {
  const router = useRouter()
  const searchParams = useSearchParams()
  const { admin, sessionResolved, acceptSession } = useAdminSession()
  const target = useMemo(() => adminTarget(searchParams.get('next'), locale), [locale, searchParams])
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    if (sessionResolved && admin) router.replace(target)
  }, [admin, router, sessionResolved, target])

  return (
    <form
      onSubmit={async (event) => {
        event.preventDefault()
        setSubmitting(true)
        setError('')
        try {
          const response = await fetch('/api/admin-auth/login', {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ email, password }),
          })
          const payload = await response.json().catch(() => null)
          const current = normalizeAdmin(payload?.user)
          if (!response.ok || !current) {
            setError(locale === 'bg' ? 'Невалидни данни за вход.' : 'Invalid administrator credentials.')
            return
          }
          acceptSession(current)
          router.replace(target)
        } catch {
          setError(
            locale === 'bg'
              ? 'Входът временно не е достъпен.'
              : 'Administrator login is temporarily unavailable.',
          )
        } finally {
          setSubmitting(false)
        }
      }}
    >
      <FieldGroup className="gap-5">
        {error ? <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert> : null}
        <Field>
          <FieldLabel htmlFor="admin-email">
            {locale === 'bg' ? 'Служебен имейл' : 'Administrator email'}
          </FieldLabel>
          <Input
            id="admin-email"
            type="email"
            autoComplete="username"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            required
          />
        </Field>
        <Field>
          <FieldLabel htmlFor="admin-password">{locale === 'bg' ? 'Парола' : 'Password'}</FieldLabel>
          <Input
            id="admin-password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            required
          />
        </Field>
        <Button type="submit" disabled={submitting || !sessionResolved}>
          {submitting ? <Spinner data-icon="inline-start" /> : null}
          {locale === 'bg' ? 'Вход в администрацията' : 'Sign in to administration'}
        </Button>
        <Link
          href={`/${locale}/admin/forgot-password`}
          className="text-center text-sm text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
        >
          {locale === 'bg' ? 'Забравена администраторска парола?' : 'Forgot administrator password?'}
        </Link>
      </FieldGroup>
    </form>
  )
}
