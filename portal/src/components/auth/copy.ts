import type { Locale } from '@/i18n/config'
import type { PasswordRuleId } from '@/lib/auth/password-rules'

export type AuthCopy = {
  login: {
    title: string
    description: string
    emailLabel: string
    emailPlaceholder: string
    passwordLabel: string
    passwordPlaceholder: string
    submit: string
    submitting: string
    invalidCredentials: string
    genericError: string
    noAccountPrompt: string
    noAccountAction: string
  }
  register: {
    title: string
    description: string
    nameLabel: string
    namePlaceholder: string
    emailLabel: string
    emailPlaceholder: string
    passwordLabel: string
    passwordPlaceholder: string
    passwordRulesTitle: string
    submit: string
    submitting: string
    emailTakenError: string
    genericError: string
    haveAccountPrompt: string
    haveAccountAction: string
  }
  passwordRules: Record<PasswordRuleId, string>
  validation: {
    nameRequired: string
    emailInvalid: string
    passwordWeak: string
  }
  legal: {
    prefix: string
    terms: string
    and: string
    privacy: string
  }
  showcase: {
    features: { title: string; caption: string }[]
  }
  showPassword: string
  hidePassword: string
}

const en: AuthCopy = {
  login: {
    title: 'Welcome back',
    description: 'Sign in to your existing account',
    emailLabel: 'Email address',
    emailPlaceholder: 'joe@company.com',
    passwordLabel: 'Password',
    passwordPlaceholder: 'Password',
    submit: 'Log In',
    submitting: 'Signing in…',
    invalidCredentials: 'That email and password don’t match. Please try again.',
    genericError: 'We couldn’t sign you in right now. Please try again in a moment.',
    noAccountPrompt: 'Don’t have an account?',
    noAccountAction: 'Sign up',
  },
  register: {
    title: 'Create an account',
    description: 'Sign up to get started',
    nameLabel: 'Full Name',
    namePlaceholder: 'John Doe',
    emailLabel: 'Email address',
    emailPlaceholder: 'joe@company.com',
    passwordLabel: 'Password',
    passwordPlaceholder: 'Password',
    passwordRulesTitle: 'Password requirements:',
    submit: 'Sign Up',
    submitting: 'Creating your account…',
    emailTakenError:
      'We couldn’t create an account with this email. If you already have one, sign in instead.',
    genericError: 'We couldn’t create your account right now. Please try again in a moment.',
    haveAccountPrompt: 'Already have an account?',
    haveAccountAction: 'Log in.',
  },
  passwordRules: {
    length: 'At least 10 characters',
    lowercase: 'At least one lowercase letter (a-z)',
    uppercase: 'At least one uppercase letter (A-Z)',
    number: 'At least one number (0-9)',
    special: 'At least one special character (!@#$%^&*()_+-=[]{};\':"|<>?,./`~)',
    notCommon: 'Not a commonly used password',
  },
  validation: {
    nameRequired: 'Please enter your name.',
    emailInvalid: 'Please enter a valid email address.',
    passwordWeak: 'Please meet all the password requirements below.',
  },
  legal: {
    prefix: 'By signing up, you agree to our',
    terms: 'Terms of Service',
    and: 'and',
    privacy: 'Privacy Policy',
  },
  showcase: {
    features: [
      {
        title: 'Connect your company',
        caption: 'ERP Generator links your systems and documents and extracts the evidence automatically.',
      },
      {
        title: 'AI interviews your team',
        caption: 'Short guided conversations fill the gaps — no workshops, no consultants.',
      },
      {
        title: 'A working ERP, generated',
        caption: 'Typed, tested, and deployed to staging for your review before going live.',
      },
      {
        title: 'It keeps improving',
        caption: 'After go-live, ERP Generator proposes improvements from real usage — you approve, it ships.',
      },
    ],
  },
  showPassword: 'Show password',
  hidePassword: 'Hide password',
}

const bg: AuthCopy = {
  login: {
    title: 'Добре дошли отново',
    description: 'Влезте в съществуващия си акаунт',
    emailLabel: 'Имейл адрес',
    emailPlaceholder: 'joe@company.com',
    passwordLabel: 'Парола',
    passwordPlaceholder: 'Парола',
    submit: 'Вход',
    submitting: 'Влизане…',
    invalidCredentials: 'Имейлът и паролата не съвпадат. Моля, опитайте отново.',
    genericError: 'В момента не можем да ви впишем. Моля, опитайте отново след малко.',
    noAccountPrompt: 'Нямате акаунт?',
    noAccountAction: 'Регистрация',
  },
  register: {
    title: 'Създайте акаунт',
    description: 'Регистрирайте се, за да започнете',
    nameLabel: 'Пълно име',
    namePlaceholder: 'Иван Иванов',
    emailLabel: 'Имейл адрес',
    emailPlaceholder: 'joe@company.com',
    passwordLabel: 'Парола',
    passwordPlaceholder: 'Парола',
    passwordRulesTitle: 'Изисквания за паролата:',
    submit: 'Регистрация',
    submitting: 'Създаване на акаунта…',
    emailTakenError:
      'Не можахме да създадем акаунт с този имейл. Ако вече имате акаунт, влезте вместо това.',
    genericError: 'В момента не можем да създадем акаунта ви. Моля, опитайте отново след малко.',
    haveAccountPrompt: 'Вече имате акаунт?',
    haveAccountAction: 'Вход.',
  },
  passwordRules: {
    length: 'Поне 10 символа',
    lowercase: 'Поне една малка буква (a-z)',
    uppercase: 'Поне една главна буква (A-Z)',
    number: 'Поне една цифра (0-9)',
    special: 'Поне един специален символ (!@#$%^&*()_+-=[]{};\':"|<>?,./`~)',
    notCommon: 'Не е често използвана парола',
  },
  validation: {
    nameRequired: 'Моля, въведете името си.',
    emailInvalid: 'Моля, въведете валиден имейл адрес.',
    passwordWeak: 'Моля, изпълнете всички изисквания за паролата по-долу.',
  },
  legal: {
    prefix: 'С регистрацията си приемате нашите',
    terms: 'Общи условия',
    and: 'и',
    privacy: 'Политика за поверителност',
  },
  showcase: {
    features: [
      {
        title: 'Свържете компанията си',
        caption: 'ERP Generator свързва системите и документите ви и извлича доказателствата автоматично.',
      },
      {
        title: 'AI интервюира екипа ви',
        caption: 'Кратки насочени разговори запълват празнините — без работни срещи и консултанти.',
      },
      {
        title: 'Работеща ERP система',
        caption: 'Типизирана, тествана и внедрена в staging за преглед, преди да влезе в продукция.',
      },
      {
        title: 'Непрекъснато подобрение',
        caption: 'След пускането ERP Generator предлага подобрения от реалната употреба — вие одобрявате.',
      },
    ],
  },
  showPassword: 'Покажи паролата',
  hidePassword: 'Скрий паролата',
}

export function getAuthCopy(locale: Locale): AuthCopy {
  return locale === 'bg' ? bg : en
}
