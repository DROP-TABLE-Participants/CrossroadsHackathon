/**
 * Shared password policy — imported by the client-side live checklist
 * (PasswordRequirements), the register form's zod schema, AND the server-side
 * register route so the rules are enforced in one place and can never drift.
 *
 * Framework-agnostic: no React, no 'use client', no Next imports.
 */

export type PasswordRuleId =
  | 'length'
  | 'lowercase'
  | 'uppercase'
  | 'number'
  | 'special'
  | 'notCommon'

export const PASSWORD_MIN_LENGTH = 10

/** Order the rules are shown to the user (and validated). */
export const PASSWORD_RULE_ORDER: PasswordRuleId[] = [
  'length',
  'lowercase',
  'uppercase',
  'number',
  'special',
  'notCommon',
]

const SPECIAL_CHAR = /[!@#$%^&*()_+\-=[\]{};':"\\|,.<>/?`~]/

/**
 * A small blocklist of the most common leaked passwords (lowercased). This is a
 * pragmatic guard, not a full HIBP check — enough to stop "Password123!" and the
 * usual suspects. Extend as needed.
 */
const COMMON_PASSWORDS = new Set<string>([
  'password',
  'password1',
  'password12',
  'password123',
  'password1234',
  'passw0rd',
  'p@ssword',
  'p@ssw0rd',
  'passw0rd!',
  'password!',
  'password1!',
  'password123!',
  'qwerty',
  'qwertyui',
  'qwertyuiop',
  'qwerty123',
  'qwerty1234',
  '1q2w3e4r',
  '1q2w3e4r5t',
  'zaq12wsx',
  'qazwsx',
  'qazwsxedc',
  '12345678',
  '123456789',
  '1234567890',
  'abcd1234',
  'abc12345',
  'a1b2c3d4',
  'letmein',
  'letmein123',
  'welcome',
  'welcome1',
  'welcome123',
  'iloveyou',
  'admin123',
  'administrator',
  'changeme',
  'changeme123',
  'trustno1',
  'sunshine',
  'princess',
  'football',
  'baseball',
  'superman',
  'batman123',
  'starwars',
  'whatever',
  'monkey123',
  'dragon123',
  'shadow123',
  'master123',
  'michael1',
  'jennifer',
])

/** Evaluate each rule against a candidate password. */
export function evaluatePassword(password: string): Record<PasswordRuleId, boolean> {
  const value = password ?? ''
  return {
    length: value.length >= PASSWORD_MIN_LENGTH,
    lowercase: /[a-z]/.test(value),
    uppercase: /[A-Z]/.test(value),
    number: /[0-9]/.test(value),
    special: SPECIAL_CHAR.test(value),
    notCommon: value.length > 0 && !COMMON_PASSWORDS.has(value.trim().toLowerCase()),
  }
}

/** True only when every rule passes. */
export function isPasswordValid(password: string): boolean {
  const results = evaluatePassword(password)
  return PASSWORD_RULE_ORDER.every((id) => results[id])
}
