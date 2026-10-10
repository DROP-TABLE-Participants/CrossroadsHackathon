import type { Locale } from '@/i18n/config'

export type AdminLabels = {
  workspace: string
  administration: string
  overview: string
  customers: string
  projects: string
  orchestration: string
  administrators: string
  demoBookings: string
  auditLog: string
  signOut: string
  loading: string
  retry: string
  search: string
  allStatuses: string
  active: string
  inactive: string
  activate: string
  deactivate: string
  passwordReset: string
  previous: string
  next: string
  page: string
  of: string
  noResults: string
  created: string
  updated: string
  status: string
  customer: string
  company: string
  email: string
  role: string
  overviewDescription: string
  activeCustomers: string
  inactiveCustomers: string
  totalProjects: string
  needsAttention: string
  failedProjects: string
  pendingNotifications: string
  failedNotifications: string
  activeAdministrators: string
  recentProjects: string
  recentActivity: string
  customersDescription: string
  customerDetails: string
  projectsOwned: string
  projectsDescription: string
  projectDetails: string
  diagnostics: string
  discovery: string
  evidence: string
  interviews: string
  deployments: string
  pendingWork: string
  noPendingWork: string
  noDeployments: string
  inputSources: string
  noSources: string
  goal: string
  latestRun: string
  failure: string
  runtime: string
  tests: string
  administratorsDescription: string
  inviteAdministrator: string
  inviteDescription: string
  name: string
  sendInvite: string
  sending: string
  currentAdministrator: string
  adminSafety: string
  demoDescription: string
  bookingStatus: string
  notificationStatus: string
  notifications: string
  resend: string
  attempts: string
  recipient: string
  auditDescription: string
  action: string
  resource: string
  actor: string
  details: string
  dateFrom: string
  dateTo: string
  confirmDeactivateTitle: string
  confirmCustomerDeactivate: string
  confirmAdministratorDeactivate: string
  confirmResendTitle: string
  confirmResendDescription: string
  cancel: string
  confirm: string
  resetAccepted: string
  inviteAccepted: string
  orchestrationDescription: string
  queuedRuns: string
  runningRuns: string
  resumingRuns: string
  resumeFailedRuns: string
  waitingForInput: string
  cancellationRequestedRuns: string
  retryScheduled: string
  failedLast24Hours: string
  staleLeases: string
  oldestQueued: string
  generationRuns: string
  currentStage: string
  requestedMode: string
  actualMode: string
  claims: string
  failures: string
  heartbeat: string
  lease: string
  timeline: string
  modelCalls: string
  agentWorkItems: string
  artifacts: string
  noAgentWorkItems: string
  noArtifacts: string
  evidenceSnapshot: string
  retryNow: string
  retryNowTitle: string
  retryNowDescription: string
  cancelRun: string
  cancelRunTitle: string
  cancelRunDescription: string
  resumeRun: string
  resumeRunTitle: string
  resumeRunDescription: string
  actionReason: string
  actionReasonPlaceholder: string
  cancellationRequested: string
  runResumed: string
  runDetails: string
  stages: string
  providerResponse: string
  noStages: string
  noEvents: string
  noModelCalls: string
}

const en: AdminLabels = {
  workspace: 'Control plane',
  administration: 'Administration',
  overview: 'Overview',
  customers: 'Customers',
  projects: 'ERP projects',
  orchestration: 'Orchestration',
  administrators: 'Administrators',
  demoBookings: 'Demo bookings',
  auditLog: 'Audit log',
  signOut: 'Sign out',
  loading: 'Loading administrator data…',
  retry: 'Try again',
  search: 'Search',
  allStatuses: 'All statuses',
  active: 'Active',
  inactive: 'Inactive',
  activate: 'Activate',
  deactivate: 'Deactivate',
  passwordReset: 'Send password reset',
  previous: 'Previous',
  next: 'Next',
  page: 'Page',
  of: 'of',
  noResults: 'No matching records',
  created: 'Created',
  updated: 'Updated',
  status: 'Status',
  customer: 'Customer',
  company: 'Company',
  email: 'Email',
  role: 'Role',
  overviewDescription: 'A concise view of customers, ERP discovery, delivery health, and recent administrator activity.',
  activeCustomers: 'Active customers',
  inactiveCustomers: 'Inactive customers',
  totalProjects: 'ERP projects',
  needsAttention: 'Needs attention',
  failedProjects: 'Failed projects',
  pendingNotifications: 'Pending notifications',
  failedNotifications: 'Failed notifications',
  activeAdministrators: 'Active administrators',
  recentProjects: 'Recently updated projects',
  recentActivity: 'Recent administrator activity',
  customersDescription: 'Manage customer access and review the ERP projects owned by each account.',
  customerDetails: 'Customer details',
  projectsOwned: 'Owned ERP projects',
  projectsDescription: 'Inspect discovery, evidence, generation, and deployment state without changing orchestration.',
  projectDetails: 'ERP project details',
  diagnostics: 'Diagnostics',
  discovery: 'Discovery',
  evidence: 'Evidence',
  interviews: 'Interviews',
  deployments: 'Deployments',
  pendingWork: 'Pending work',
  noPendingWork: 'No pending work is recorded.',
  noDeployments: 'No deployment has been recorded.',
  inputSources: 'Connected sources',
  noSources: 'No connected sources are recorded.',
  goal: 'Project goal',
  latestRun: 'Latest run',
  failure: 'Failure',
  runtime: 'Runtime',
  tests: 'Tests',
  administratorsDescription: 'Invite and manage separate platform administrator identities.',
  inviteAdministrator: 'Invite administrator',
  inviteDescription: 'The administrator receives a secure link to set their password.',
  name: 'Name',
  sendInvite: 'Create and send invite',
  sending: 'Sending…',
  currentAdministrator: 'You',
  adminSafety: 'The last active administrator and your own account cannot be deactivated.',
  demoDescription: 'Review demo reservations and retry individual notification deliveries.',
  bookingStatus: 'Booking status',
  notificationStatus: 'Notification status',
  notifications: 'Notifications',
  resend: 'Resend',
  attempts: 'Attempts',
  recipient: 'Recipient',
  auditDescription: 'Read-only history of administrator actions. Secrets and credentials are never stored here.',
  action: 'Action',
  resource: 'Resource',
  actor: 'Actor',
  details: 'Safe details',
  dateFrom: 'From date',
  dateTo: 'To date',
  confirmDeactivateTitle: 'Confirm deactivation',
  confirmCustomerDeactivate: 'This revokes the customer’s active sessions. Their projects and evidence remain unchanged.',
  confirmAdministratorDeactivate: 'This revokes the administrator’s active sessions. They will no longer be able to sign in.',
  confirmResendTitle: 'Resend this notification?',
  confirmResendDescription: 'The delivery will be returned to the pending queue.',
  cancel: 'Cancel',
  confirm: 'Confirm',
  resetAccepted: 'Password-reset request accepted.',
  inviteAccepted: 'Administrator created and invitation requested.',
  orchestrationDescription: 'Monitor durable generation runs, stage progress, recovery, and failures without exposing customer evidence.',
  queuedRuns: 'Queued runs',
  runningRuns: 'Running runs',
  resumingRuns: 'Resuming runs',
  resumeFailedRuns: 'Resume needs attention',
  waitingForInput: 'Waiting for input',
  cancellationRequestedRuns: 'Cancellation requested',
  retryScheduled: 'Retry scheduled',
  failedLast24Hours: 'Failed in 24 hours',
  staleLeases: 'Stale leases',
  oldestQueued: 'Oldest queued',
  generationRuns: 'Generation runs',
  currentStage: 'Current stage',
  requestedMode: 'Requested mode',
  actualMode: 'Actual mode',
  claims: 'Claims',
  failures: 'Failures',
  heartbeat: 'Heartbeat',
  lease: 'Worker lease',
  timeline: 'Timeline',
  modelCalls: 'Model calls',
  agentWorkItems: 'Agent work items',
  artifacts: 'Artifact lineage',
  noAgentWorkItems: 'No agent work items are recorded.',
  noArtifacts: 'No versioned artifacts are recorded.',
  evidenceSnapshot: 'Evidence snapshot',
  retryNow: 'Retry now',
  retryNowTitle: 'Retry this generation run now?',
  retryNowDescription: 'The same durable run will be queued immediately. Existing evidence, stages, model calls, and failure history will be preserved.',
  cancelRun: 'Cancel run',
  cancelRunTitle: 'Cancel this generation run?',
  cancelRunDescription: 'The current worker will be fenced and the run will stop at a durable boundary. Existing evidence and completed work will be preserved.',
  resumeRun: 'Resume run',
  resumeRunTitle: 'Resume this cancelled generation run?',
  resumeRunDescription: 'The same durable run will return to the queue and continue only from incomplete work.',
  actionReason: 'Administrative reason',
  actionReasonPlaceholder: 'Explain why this action is necessary.',
  cancellationRequested: 'Cancellation requested.',
  runResumed: 'Generation run resumed.',
  runDetails: 'Generation run details',
  stages: 'Stages',
  providerResponse: 'Provider response',
  noStages: 'No generation stages are recorded.',
  noEvents: 'No orchestration events are recorded.',
  noModelCalls: 'No model calls are recorded.',
}

const bg: AdminLabels = {
  ...en,
  workspace: 'Контролен център',
  administration: 'Администрация',
  overview: 'Преглед',
  customers: 'Клиенти',
  projects: 'ERP проекти',
  orchestration: 'Оркестрация',
  administrators: 'Администратори',
  demoBookings: 'Демо срещи',
  auditLog: 'Одитен дневник',
  signOut: 'Изход',
  loading: 'Зареждане на администраторските данни…',
  retry: 'Опитай отново',
  search: 'Търсене',
  allStatuses: 'Всички статуси',
  active: 'Активен',
  inactive: 'Неактивен',
  activate: 'Активирай',
  deactivate: 'Деактивирай',
  passwordReset: 'Изпрати смяна на парола',
  previous: 'Назад',
  next: 'Напред',
  page: 'Страница',
  of: 'от',
  noResults: 'Няма съвпадащи записи',
  created: 'Създаден',
  updated: 'Обновен',
  status: 'Статус',
  customer: 'Клиент',
  company: 'Компания',
  email: 'Имейл',
  role: 'Роля',
  overviewDescription: 'Кратък преглед на клиентите, ERP откриването, доставките и последните действия.',
  activeCustomers: 'Активни клиенти',
  inactiveCustomers: 'Неактивни клиенти',
  totalProjects: 'ERP проекти',
  needsAttention: 'Изисква внимание',
  failedProjects: 'Неуспешни проекти',
  pendingNotifications: 'Чакащи известия',
  failedNotifications: 'Неуспешни известия',
  activeAdministrators: 'Активни администратори',
  recentProjects: 'Наскоро обновени проекти',
  recentActivity: 'Последни администраторски действия',
  customersDescription: 'Управление на достъпа на клиентите и техните ERP проекти.',
  customerDetails: 'Детайли за клиента',
  projectsOwned: 'ERP проекти на клиента',
  projectsDescription: 'Преглед на откриването, доказателствата, генерирането и внедряването без промяна на оркестрацията.',
  projectDetails: 'Детайли за ERP проекта',
  diagnostics: 'Диагностика',
  discovery: 'Откриване',
  evidence: 'Доказателства',
  interviews: 'Интервюта',
  deployments: 'Внедрявания',
  pendingWork: 'Чакащи задачи',
  noPendingWork: 'Няма записани чакащи задачи.',
  noDeployments: 'Няма записано внедряване.',
  inputSources: 'Свързани източници',
  noSources: 'Няма записани свързани източници.',
  goal: 'Цел на проекта',
  latestRun: 'Последно изпълнение',
  failure: 'Грешка',
  runtime: 'Среда',
  tests: 'Тестове',
  administratorsDescription: 'Покани и управлявай отделните платформени администраторски профили.',
  inviteAdministrator: 'Покани администратор',
  inviteDescription: 'Администраторът получава сигурен линк за задаване на парола.',
  name: 'Име',
  sendInvite: 'Създай и изпрати покана',
  sending: 'Изпращане…',
  currentAdministrator: 'Ти',
  adminSafety: 'Последният активен администратор и собственият ти профил не могат да бъдат деактивирани.',
  demoDescription: 'Преглед на демо срещите и повторно изпращане на отделни известия.',
  bookingStatus: 'Статус на срещата',
  notificationStatus: 'Статус на известието',
  notifications: 'Известия',
  resend: 'Изпрати отново',
  attempts: 'Опити',
  recipient: 'Получател',
  auditDescription: 'Дневник само за четене. Тайни и идентификационни данни не се съхраняват тук.',
  action: 'Действие',
  resource: 'Ресурс',
  actor: 'Извършил',
  details: 'Безопасни детайли',
  dateFrom: 'От дата',
  dateTo: 'До дата',
  confirmDeactivateTitle: 'Потвърди деактивирането',
  confirmCustomerDeactivate: 'Активните сесии на клиента ще бъдат прекратени. Проектите и доказателствата остават непроменени.',
  confirmAdministratorDeactivate: 'Активните сесии на администратора ще бъдат прекратени и той няма да може да влиза.',
  confirmResendTitle: 'Да се изпрати ли известието отново?',
  confirmResendDescription: 'Известието ще бъде върнато в чакащата опашка.',
  cancel: 'Отказ',
  confirm: 'Потвърди',
  resetAccepted: 'Заявката за смяна на парола е приета.',
  inviteAccepted: 'Администраторът е създаден и поканата е заявена.',
  orchestrationDescription: 'Наблюдение на устойчивите изпълнения, етапите, възстановяването и грешките без показване на клиентски доказателства.',
  queuedRuns: 'Чакащи изпълнения',
  runningRuns: 'Активни изпълнения',
  resumingRuns: 'Продължаващи изпълнения',
  resumeFailedRuns: 'Продължаването изисква намеса',
  waitingForInput: 'Чака информация',
  cancellationRequestedRuns: 'Заявено отказване',
  retryScheduled: 'Планиран нов опит',
  failedLast24Hours: 'Неуспешни за 24 часа',
  staleLeases: 'Изтекли работни заключвания',
  oldestQueued: 'Най-старо чакащо',
  generationRuns: 'Изпълнения за генериране',
  currentStage: 'Текущ етап',
  requestedMode: 'Заявен режим',
  actualMode: 'Реален режим',
  claims: 'Поемания',
  failures: 'Грешки',
  heartbeat: 'Последен сигнал',
  lease: 'Работно заключване',
  timeline: 'Хронология',
  modelCalls: 'AI заявки',
  agentWorkItems: 'Задачи на агентите',
  artifacts: 'Версии на артефактите',
  noAgentWorkItems: 'Няма записани задачи на агенти.',
  noArtifacts: 'Няма записани версии на артефакти.',
  evidenceSnapshot: 'Версия на доказателствата',
  retryNow: 'Опитай сега',
  retryNowTitle: 'Да се стартира ли нов опит сега?',
  retryNowDescription: 'Същото устойчиво изпълнение ще бъде поставено незабавно в опашката. Доказателствата, етапите, AI заявките и историята на грешките ще бъдат запазени.',
  cancelRun: 'Откажи изпълнението',
  cancelRunTitle: 'Да се откаже ли това изпълнение?',
  cancelRunDescription: 'Текущият работник ще бъде спрян безопасно и изпълнението ще приключи на устойчива граница. Завършената работа ще бъде запазена.',
  resumeRun: 'Възобнови',
  resumeRunTitle: 'Да се възобнови ли отказаното изпълнение?',
  resumeRunDescription: 'Същото устойчиво изпълнение ще се върне на опашката и ще продължи само от незавършената работа.',
  actionReason: 'Административна причина',
  actionReasonPlaceholder: 'Обяснете защо това действие е необходимо.',
  cancellationRequested: 'Отказването е заявено.',
  runResumed: 'Изпълнението е възобновено.',
  runDetails: 'Детайли за изпълнението',
  stages: 'Етапи',
  providerResponse: 'Отговор от доставчика',
  noStages: 'Няма записани етапи.',
  noEvents: 'Няма записани събития от оркестрацията.',
  noModelCalls: 'Няма записани AI заявки.',
}

export function getAdminLabels(locale: Locale): AdminLabels {
  return locale === 'bg' ? bg : en
}
