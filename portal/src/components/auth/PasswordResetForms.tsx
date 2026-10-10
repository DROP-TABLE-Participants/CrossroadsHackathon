'use client'

import { useState } from 'react'
import Link from 'next/link'
import { useRouter, useSearchParams } from 'next/navigation'

import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Field, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import type { Locale } from '@/i18n/config'

type PasswordResetRealm = 'customer' | 'administrator'

function routes(locale: Locale, realm: PasswordResetRealm) {
  return realm === 'administrator'
    ? {
        forgotEndpoint: '/api/admin-auth/forgot-password',
        resetEndpoint: '/api/admin-auth/reset-password',
        login: `/${locale}/admin/login`,
      }
    : {
        forgotEndpoint: '/api/customers/forgot-password',
        resetEndpoint: '/api/customers/reset-password',
        login: `/${locale}/login`,
      }
}

export function ForgotPasswordForm({
  locale,
  realm,
}: {
  locale: Locale
  realm: PasswordResetRealm
}) {
  const [email, setEmail] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [sent, setSent] = useState(false)
  const [error, setError] = useState('')
  const realmRoutes = routes(locale, realm)

  return (
    <form
      onSubmit={async (event) => {
        event.preventDefault()
        setSubmitting(true)
        setError('')
        try {
          const response = await fetch(realmRoutes.forgotEndpoint, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ email, locale }),
          })
          if (!response.ok) throw new Error()
          setSent(true)
        } catch {
          setError(locale === 'bg' ? 'Заявката не може да бъде изпратена в момента.' : 'The request could not be sent right now.')
        } finally {
          setSubmitting(false)
        }
      }}
    >
      <FieldGroup className="gap-5">
        {sent ? (
          <Alert>
            <AlertDescription>
              {locale === 'bg'
                ? 'Ако този акаунт съществува, изпратихме линк за смяна на паролата.'
                : 'If that account exists, a password-reset link has been sent.'}
            </AlertDescription>
          </Alert>
        ) : null}
        {error ? <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert> : null}
        <Field>
          <FieldLabel htmlFor={`${realm}-reset-email`}>{locale === 'bg' ? 'Имейл' : 'Email'}</FieldLabel>
          <Input
            id={`${realm}-reset-email`}
            type="email"
            autoComplete="email"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            required
          />
        </Field>
        <Button type="submit" disabled={submitting || sent}>
          {submitting ? <Spinner data-icon="inline-start" /> : null}
          {locale === 'bg' ? 'Изпрати линк' : 'Send reset link'}
        </Button>
        <Link className="text-center text-sm text-muted-foreground hover:text-foreground" href={realmRoutes.login}>
          {locale === 'bg' ? 'Обратно към вход' : 'Back to sign in'}
        </Link>
      </FieldGroup>
    </form>
  )
}

export function ResetPasswordForm({
  locale,
  realm,
}: {
  locale: Locale
  realm: PasswordResetRealm
}) {
  const router = useRouter()
  const searchParams = useSearchParams()
  const email = searchParams.get('email') || ''
  const token = searchParams.get('token') || ''
  const realmRoutes = routes(locale, realm)
  const [password, setPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')

  return (
    <form
      onSubmit={async (event) => {
        event.preventDefault()
        if (password !== confirmation) {
          setError(locale === 'bg' ? 'Паролите не съвпадат.' : 'The passwords do not match.')
          return
        }
        setSubmitting(true)
        setError('')
        try {
          const response = await fetch(realmRoutes.resetEndpoint, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ email, token, newPassword: password }),
          })
          const payload = await response.json().catch(() => null)
          if (!response.ok) {
            setError(
              payload?.message ||
                (locale === 'bg'
                  ? 'Линкът е невалиден или е изтекъл.'
                  : 'The reset link is invalid or expired.'),
            )
            return
          }
          router.replace(`${realmRoutes.login}?reset=complete`)
        } catch {
          setError(locale === 'bg' ? 'Паролата не може да бъде сменена в момента.' : 'The password could not be reset right now.')
        } finally {
          setSubmitting(false)
        }
      }}
    >
      <FieldGroup className="gap-5">
        {error ? <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert> : null}
        {!email || !token ? (
          <Alert variant="destructive">
            <AlertDescription>
              {locale === 'bg' ? 'Линкът за смяна на паролата е непълен.' : 'The password-reset link is incomplete.'}
            </AlertDescription>
          </Alert>
        ) : null}
        <Field>
          <FieldLabel htmlFor={`${realm}-new-password`}>{locale === 'bg' ? 'Нова парола' : 'New password'}</FieldLabel>
          <Input
            id={`${realm}-new-password`}
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            minLength={realm === 'administrator' ? 12 : 10}
            required
          />
        </Field>
        <Field>
          <FieldLabel htmlFor={`${realm}-confirm-password`}>
            {locale === 'bg' ? 'Потвърдете паролата' : 'Confirm password'}
          </FieldLabel>
          <Input
            id={`${realm}-confirm-password`}
            type="password"
            autoComplete="new-password"
            value={confirmation}
            onChange={(event) => setConfirmation(event.target.value)}
            required
          />
        </Field>
        <Button type="submit" disabled={submitting || !email || !token}>
          {submitting ? <Spinner data-icon="inline-start" /> : null}
          {locale === 'bg' ? 'Смени паролата' : 'Reset password'}
        </Button>
      </FieldGroup>
    </form>
  )
}
