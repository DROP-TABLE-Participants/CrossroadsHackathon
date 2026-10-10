'use client'

import { useMemo, useState } from 'react'
import Link from 'next/link'
import { useRouter, useSearchParams } from 'next/navigation'
import { useForm, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { WarningCircle as AlertCircle, Eye, EyeSlash as EyeOff } from '@phosphor-icons/react'

import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Field, FieldError, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
} from '@/components/ui/input-group'
import { Spinner } from '@/components/ui/spinner'
import { normalizeCustomer, saveAuthSession } from '@/components/booking/customer-session'
import { isPasswordValid } from '@/lib/auth/password-rules'
import type { Locale } from '@/i18n/config'
import type { AuthCopy } from './copy'
import { PasswordRequirements } from './PasswordRequirements'
import { sanitizeNextPath, useRedirectIfAuthenticated } from './auth-client'

export function RegisterForm({ locale, copy }: { locale: Locale; copy: AuthCopy }) {
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
        name: z.string().trim().min(1, copy.validation.nameRequired).max(120),
        email: z.email(copy.validation.emailInvalid),
        password: z.string().refine(isPasswordValid, copy.validation.passwordWeak),
      }),
    [copy],
  )

  const form = useForm<z.infer<typeof schema>>({
    resolver: zodResolver(schema),
    // Judge the answer when it's submitted, not while it's being typed — a
    // half-typed email isn't a wrong one. The password checklist below still
    // updates live, because green ticks encourage where red borders accuse.
    mode: 'onSubmit',
    reValidateMode: 'onSubmit',
    defaultValues: { name: '', email: '', password: '' },
  })
  const { errors, isSubmitting } = form.formState
  const passwordValue = useWatch({ control: form.control, name: 'password' }) ?? ''

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
      const response = await fetch('/api/customers/register', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          name: values.name,
          email: values.email,
          password: values.password,
        }),
      })

      if (!response.ok) {
        setFormError(
          response.status === 400 ? copy.register.emailTakenError : copy.register.genericError,
        )
        return
      }

      const data = await response.json()
      const user = normalizeCustomer(data?.user)
      if (!user) {
        // The account exists now, so send them to sign in rather than a retry loop.
        router.replace(`/${locale}/login`)
        return
      }

      saveAuthSession(user)
      router.replace(target)
    } catch {
      setFormError(copy.register.genericError)
    }
  })

  const loginHref =
    `/${locale}/login` +
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

        <Field data-invalid={errors.name ? true : undefined}>
          <FieldLabel htmlFor="register-name">{copy.register.nameLabel}</FieldLabel>
          <Input
            id="register-name"
            autoComplete="name"
            placeholder={copy.register.namePlaceholder}
            aria-invalid={errors.name ? true : undefined}
            {...editableField('name')}
          />
          <FieldError errors={[errors.name]} />
        </Field>

        <Field data-invalid={errors.email ? true : undefined}>
          <FieldLabel htmlFor="register-email">{copy.register.emailLabel}</FieldLabel>
          <Input
            id="register-email"
            type="email"
            autoComplete="email"
            placeholder={copy.register.emailPlaceholder}
            aria-invalid={errors.email ? true : undefined}
            {...editableField('email')}
          />
          <FieldError errors={[errors.email]} />
        </Field>

        <Field data-invalid={errors.password ? true : undefined}>
          <FieldLabel htmlFor="register-password">{copy.register.passwordLabel}</FieldLabel>
          <InputGroup>
            <InputGroupInput
              id="register-password"
              type={showPassword ? 'text' : 'password'}
              autoComplete="new-password"
              placeholder={copy.register.passwordPlaceholder}
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
          {passwordValue.length > 0 ? (
            <PasswordRequirements
              password={passwordValue}
              title={copy.register.passwordRulesTitle}
              labels={copy.passwordRules}
            />
          ) : null}
        </Field>

        {/* Always clickable: with submit-time validation there's nothing to gate
            on yet, and a dead button that won't say why is worse than a clear
            error after a press. */}
        <Button type="submit" className="w-full" disabled={isSubmitting || checkingSession}>
          {isSubmitting ? <Spinner data-icon="inline-start" /> : null}
          {isSubmitting ? copy.register.submitting : copy.register.submit}
        </Button>

        <p className="text-center text-sm text-muted-foreground">
          {copy.register.haveAccountPrompt}{' '}
          <Link
            href={loginHref}
            className="font-medium text-primary underline-offset-4 hover:underline"
          >
            {copy.register.haveAccountAction}
          </Link>
        </p>
      </FieldGroup>
    </form>
  )
}
