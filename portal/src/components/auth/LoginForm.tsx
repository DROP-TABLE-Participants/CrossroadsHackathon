'use client'

import { useMemo, useState } from 'react'
import Link from 'next/link'
import { useRouter, useSearchParams } from 'next/navigation'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { WarningCircle as AlertCircle, Eye, EyeSlash as EyeOff } from '@phosphor-icons/react'

import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import {
  Field,
  FieldError,
  FieldGroup,
  FieldLabel,
} from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
} from '@/components/ui/input-group'
import { Spinner } from '@/components/ui/spinner'
import { normalizeCustomer, saveAuthSession } from '@/components/booking/customer-session'
import type { Locale } from '@/i18n/config'
import type { AuthCopy } from './copy'
import { sanitizeNextPath, useRedirectIfAuthenticated } from './auth-client'

export function LoginForm({ locale, copy }: { locale: Locale; copy: AuthCopy }) {
  const router = useRouter()
  const searchParams = useSearchParams()
  const target = useMemo(
    () => sanitizeNextPath(searchParams.get('next'), locale),
    [searchParams, locale],
  )
  const checkingSession = useRedirectIfAuthenticated(target)

  const [formError, setFormError] = useState('')
  const [showPassword, setShowPassword] = useState(false)

  const schema = useMemo(
    () =>
      z.object({
        email: z.string().trim().email(copy.validation.emailInvalid),
        password: z.string().min(1, copy.login.invalidCredentials),
      }),
    [copy],
  )

  const form = useForm<z.infer<typeof schema>>({
    resolver: zodResolver(schema),
    // Judge the answer when it's submitted, not while it's being typed —
    // "esemenov" isn't a wrong email, it's an unfinished one, and going red
    // mid-keystroke reads as "you got it wrong" before the customer is done.
    mode: 'onSubmit',
    reValidateMode: 'onSubmit',
    defaultValues: { email: '', password: '' },
  })
  const { errors, isSubmitting } = form.formState

  // Once they start fixing a rejected field, drop the complaint immediately
  // rather than making them submit again to see the red go away.
  const editableField = (name: keyof z.infer<typeof schema>) => {
    const field = form.register(name)
    return {
      ...field,
      onChange: (event: React.ChangeEvent<HTMLInputElement>) => {
        field.onChange(event)
        if (errors[name]) form.clearErrors(name)
        if (formError) setFormError('')
      },
    }
  }

  const onSubmit = form.handleSubmit(async (values) => {
    setFormError('')
    try {
      const response = await fetch('/api/customers/login', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ email: values.email, password: values.password }),
      })

      if (!response.ok) {
        setFormError(
          response.status === 401 || response.status === 400
            ? copy.login.invalidCredentials
            : copy.login.genericError,
        )
        return
      }

      const data = await response.json()
      const user = normalizeCustomer(data?.user)
      if (!user) {
        setFormError(copy.login.genericError)
        return
      }

      saveAuthSession(user)
      router.replace(target)
    } catch {
      setFormError(copy.login.genericError)
    }
  })

  const registerHref =
    `/${locale}/register` +
    (searchParams.get('next') ? `?next=${encodeURIComponent(searchParams.get('next') ?? '')}` : '')

  return (
    <form onSubmit={onSubmit} noValidate aria-busy={checkingSession || undefined}>
      <FieldGroup className="gap-5">
        {formError ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription>{formError}</AlertDescription>
          </Alert>
        ) : null}

        <Field data-invalid={errors.email ? true : undefined}>
          <FieldLabel htmlFor="login-email">{copy.login.emailLabel}</FieldLabel>
          <Input
            id="login-email"
            type="email"
            autoComplete="email"
            placeholder={copy.login.emailPlaceholder}
            aria-invalid={errors.email ? true : undefined}
            {...editableField('email')}
          />
          <FieldError errors={[errors.email]} />
        </Field>

        <Field data-invalid={errors.password ? true : undefined}>
          <FieldLabel htmlFor="login-password">{copy.login.passwordLabel}</FieldLabel>
          <InputGroup>
            <InputGroupInput
              id="login-password"
              type={showPassword ? 'text' : 'password'}
              autoComplete="current-password"
              placeholder={copy.login.passwordPlaceholder}
              aria-invalid={errors.password ? true : undefined}
              {...editableField('password')}
            />
            <InputGroupAddon align="inline-end">
              <InputGroupButton
                type="button"
                aria-label={showPassword ? copy.hidePassword : copy.showPassword}
                onClick={() => setShowPassword((value) => !value)}
              >
                {showPassword ? <EyeOff /> : <Eye />}
              </InputGroupButton>
            </InputGroupAddon>
          </InputGroup>
          <FieldError errors={[errors.password]} />
        </Field>

        {/* Always clickable: with submit-time validation there's nothing to gate
            on yet, and a dead button that won't say why is worse than a clear
            error after a press. */}
        <Button type="submit" className="w-full" disabled={isSubmitting || checkingSession}>
          {isSubmitting ? <Spinner data-icon="inline-start" /> : null}
          {isSubmitting ? copy.login.submitting : copy.login.submit}
        </Button>

        <Link
          href={`/${locale}/forgot-password`}
          className="text-center text-sm text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
        >
          {locale === 'bg' ? 'Забравена парола?' : 'Forgot password?'}
        </Link>

        <p className="text-center text-sm text-muted-foreground">
          {copy.login.noAccountPrompt}{' '}
          <Link
            href={registerHref}
            className="font-medium text-primary underline-offset-4 hover:underline"
          >
            {copy.login.noAccountAction}
          </Link>
        </p>
      </FieldGroup>
    </form>
  )
}
