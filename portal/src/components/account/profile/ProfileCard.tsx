'use client'

import { useEffect } from 'react'
import { WarningCircle as AlertCircle } from '@phosphor-icons/react'
import { toast } from 'sonner'

import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Field, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import type { ProfileApi } from '@/components/account/api-types'
import type { AccountLabels } from '@/components/account/labels'

type ProfileCardProps = {
  labels: AccountLabels
  profile: ProfileApi
}

export function ProfileCard({ labels, profile }: ProfileCardProps) {
  const {
    name,
    setName,
    email,
    setEmail,
    phoneNumber,
    setPhoneNumber,
    companyName,
    setCompanyName,
    position,
    setPosition,
    feedback,
    error,
    saving,
    handleProfileSubmit,
  } = profile

  useEffect(() => {
    if (feedback) toast.success(feedback)
  }, [feedback])

  return (
    <form onSubmit={handleProfileSubmit}>
      <FieldGroup className="gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field>
            <FieldLabel htmlFor="profile-name">{labels.name}</FieldLabel>
            <Input
              id="profile-name"
              value={name}
              onChange={(event) => setName(event.target.value)}
              autoComplete="name"
              required
            />
          </Field>
          <Field>
            <FieldLabel htmlFor="profile-email">{labels.email}</FieldLabel>
            <Input
              id="profile-email"
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              autoComplete="email"
              required
            />
          </Field>
          <Field>
            <FieldLabel htmlFor="profile-phone">{labels.phone}</FieldLabel>
            <Input
              id="profile-phone"
              value={phoneNumber}
              onChange={(event) => setPhoneNumber(event.target.value)}
              autoComplete="tel"
              required
            />
          </Field>
          <Field>
            <FieldLabel htmlFor="profile-company">{labels.company}</FieldLabel>
            <Input
              id="profile-company"
              value={companyName}
              onChange={(event) => setCompanyName(event.target.value)}
              autoComplete="organization"
              required
            />
          </Field>
          <Field className="sm:col-span-2">
            <FieldLabel htmlFor="profile-position">{labels.position}</FieldLabel>
            <Input
              id="profile-position"
              value={position}
              onChange={(event) => setPosition(event.target.value)}
              autoComplete="organization-title"
            />
          </Field>
        </div>
        {error ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}
        <p aria-live="polite" role="status" className="sr-only">
          {saving ? labels.saving : feedback}
        </p>
        <div>
          <Button type="submit" disabled={saving}>
            {saving ? <Spinner data-icon="inline-start" /> : null}
            {saving ? labels.saving : labels.save}
          </Button>
        </div>
      </FieldGroup>
    </form>
  )
}
