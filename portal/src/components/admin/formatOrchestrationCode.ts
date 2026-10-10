import type { Locale } from '@/i18n/config'

const bulgarian: Record<string, string> = {
  queued: 'На опашка',
  running: 'Изпълнява се',
  resuming: 'Продължава след получена информация',
  resume_failed: 'Продължаването изисква намеса',
  waiting_for_input: 'Чака информация',
  cancellation_requested: 'Отказването се завършва',
  cancelled: 'Отказано',
  retry_scheduled: 'Планиран нов опит',
  completed: 'Завършен',
  failed: 'Неуспешен',
  retryable: 'Възстановима',
  terminal: 'Терминална',
  configuration: 'Конфигурация',
  company_understanding: 'Разбиране на компанията',
  erp_design: 'ERP дизайн',
  queued_generation: 'Генерирането е поставено на опашка',
  lease_claimed: 'Поето от работник',
  lease_recovered: 'Възстановено работно заключване',
  stage_started: 'Етапът започна',
  stage_completed: 'Етапът завърши',
  stage_reused: 'Преизползван завършен етап',
  input_received: 'Получена информация',
  admin_retry_requested: 'Администратор поиска нов опит',
  admin_cancellation_requested: 'Администратор поиска отказване',
  admin_resume_requested: 'Администратор възобнови изпълнението',
  discovery_assessment: 'Оценка на откриването',
  erp_authoring: 'Създаване на ERP',
  interview_request: 'Интервю',
  evidence_collection_request: 'Заявка за доказателства',
  resolved: 'Решено',
  pending: 'Чака',
  waived: 'Продължено с допускания',
  ready: 'Готово',
  passed: 'Успешни',
  staging: 'Тестова среда',
  production: 'Продукция',
  simple: 'Опростен',
  default: 'По подразбиране',
}

export function formatOrchestrationCode(value: string, locale: Locale) {
  if (locale === 'bg' && value.startsWith('erp_authoring_attempt_')) {
    return `Създаване на ERP · опит ${value.slice('erp_authoring_attempt_'.length)}`
  }
  if (locale === 'bg' && bulgarian[value]) return bulgarian[value]
  const readable = value.replaceAll('_', ' ')
  return readable.charAt(0).toUpperCase() + readable.slice(1)
}
