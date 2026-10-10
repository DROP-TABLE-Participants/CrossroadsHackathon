import type { Locale } from '@/i18n/config'

export type AccountLabels = {
  eyebrow: string
  title: string
  description: string
  profileTitle: string
  demoTitle: string
  name: string
  email: string
  phone: string
  company: string
  position: string
  save: string
  saving: string
  saved: string
  signInTitle: string
  signInDescription: string
  signIn: string
  home: string
  logout: string
  noDemo: string
  bookDemo: string
  currentDemo: string
  status: string
  trialTitle: string
  trialDaysLeft: (days: number) => string
  trialLastDay: string
  trialDayOf: (used: number, total: number) => string
  trialStartedOn: string
  trialEndsOn: string
  trialEnded: string
  trialEndedHelper: string
  trialNotStarted: string
  trialNotStartedHelper: string
  trialCreateErp: string
  demoBookTitle: string
  demoBookHelper: string
  demoBookWith: (name: string) => string
  demoBookCta: string
  demoBookDialogHelper: string
  demoSlotLength: string
  demoManageNote: string
  runtimeTitle: string
  runtimeEmpty: string
  tenantName: string
  tenantSlug: string
  tenantIndustry: string
  tenantEmployees: string
  tenantGoal: string
  erpBundle: string
  demoBundle: string
  generatedErp: string
  generatedErpReady: string
  createTenant: string
  creatingTenant: string
  refreshTenants: string
  runtime: string
  activation: string
  deployment: string
  activeBundle: string
  stagedBundle: string
  deploymentHistory: string
  discoveryTitle: string
  discoveryEmpty: string
  projectIndustry: string
  projectCompanyType: string
  projectGoal: string
  projectLeadership: string
  createProject: string
  creatingProject: string
  refreshProjects: string
  startGeneration: string
  startingGeneration: string
  resumingGeneration: string
  generateWithAssumptions: string
  generatingWithAssumptions: string
  generateWithAssumptionsHelper: string
  generationProgress: string
  generationMode: string
  fallbackUsed: string
  yes: string
  pendingWork: string
  createInterviewRequest: string
  interviewBeingPrepared: string
  addEvidenceDocument: string
  addEvidenceHelper: string
  submitEvidence: string
  markResolved: string
  answerHelper: string
  confirmResolved: string
  answerSubmitted: string
  evidenceSubmitted: string
  continuingGeneration: string
  actionFailed: string
  evidenceTitle: string
  evidenceTitlePlaceholder: string
  evidenceText: string
  evidenceTextPlaceholder: string
  resolutionNote: string
  resolutionNotePlaceholder: string
  actionInProgress: string
  agentWorking: string
  waitingForAutomaticResume: string
  actionNeeded: string
  actionNeededHelper: string
  technicalProgress: string
  artifacts: string
  noGenerationRuns: string
  copyInvite: string
  sendInvite: string
  resendInvite: string
  regenerateInvite: string
  invite: string
  recipient: string
  target: string
  inviteSent: string
  inviteFailed: string
  inviteNoEmail: string
  inviteNotSent: string
  inviteQueued: string
  noEmailKnown: string
  noEmailCopyFallback: string
  participants: string
  interviews: string
  stalledRun: string
  connectedSources: string
  connectCompanyData: string
  connectorHelper: string
  searchConnectors: string
  googleDrive: string
  sharePoint: string
  microsoftTeams: string
  notion: string
  slack: string
  documentsAndSharedDriveFiles: string
  messagesAndConversations: string
  available: string
  comingSoon: string
  notConfigured: string
  connectorConnected: string
  connectorNeedsReconnect: string
  connectorSyncFailed: string
  reconnectSource: string
  checkSource: string
  checkingSource: string
  syncingSource: string
  connectorCheckFailed: string
  connectorResourcesFailed: string
  connectorSelectionFailed: string
  connectorDisconnectFailed: string
  connectorSetupBeforeGeneration: string
  connectorSyncingBeforeGeneration: string
  connectorSyncFailedBeforeGeneration: string
  connectorReconnectBeforeGeneration: string
  connectorExpiredBeforeGeneration: string
  connectorDataNoticeTitle: string
  connectorDataNoticeDescription: string
  connectorDataNoticeScope: string
  connectorDataNoticeStorage: string
  connectorDataNoticeAi: string
  connectorDataNoticeConfirm: string
  continueToConnector: string
  selectConnectorContent: string
  connectorScopeRequired: string
  manageConnectorContent: string
  connectorContentTitle: string
  connectorContentDescription: string
  searchPages: string
  selectAllPages: string
  clearPageSelection: string
  pageSelectionLimit: string
  saveAndSync: string
  selectedPages: string
  noConnectorResources: string
  allAccessibleContent: string
  allAccessibleContentDescription: string
  specificContentAreas: string
  specificContentAreasDescription: string
  everythingAccessible: string
  uncheckedContentExcluded: string
  includesDatabaseRows: string
  includesDescendants: string
  includedThroughParent: string
  includeDescendantsDescription: string
  retryConnectorResources: string
  disconnectSource: string
  disconnectSourceConfirmTitle: string
  disconnectSourceConfirmDescription: string
  phaseUnderstanding: string
  phaseQuestions: string
  phaseBuilding: string
  phaseReviewing: string
  progressUnderstandingHeadline: string
  progressQuestionsHeadline: string
  progressBuildingHeadline: string
  progressReviewingHeadline: string
  progressDoneHeadline: string
  progressFailedHeadline: string
  progressResumeFailedHeadline: string
  progressCancellingHeadline: string
  progressCancelledHeadline: string
  progressWaitingInterview: string
  progressWaitingEvidence: string
  progressTitle: string
  progressStepOf: (current: number, total: number) => string
  connectorsLockedDuringGeneration: string
  interviewInviteSent: string
  interviewAwaitingReply: string
  interviewCompleted: string
  interviewNotSentYet: string
  interviewsHelper: string
  liveSince: string
  createNewErp: string
  inviteLinkCopied: string
  inviteLinkCopyFailed: string
  projectStatus: (status: string) => string
  runStatusPlain: (status: string) => string
  sourceRecords: string
  textRecords: string
  lastChecked: string
  connectedButNoDocuments: string
  documentsFoundNoReadableText: string
  connectDataSource: string
  connectingDataSource: string
  connectOpened: string
  noConnectedSources: string
  runtimeActivationNotice: string
  ownerActivation: string
  activationPending: string
  activationActivated: string
  activationExpired: string
  activationNotCreated: string
  regenerateActivationLink: string
  copyActivationLink: string
  activationLinkReady: string
  generatedModules: string
  knownLimitations: string
  tests: string
  feedbackImprovements: string
  recentFeedback: string
  usageObservations: string
  feedbackPageIntro: string
  feedbackNothingYet: string
  feedbackNothingYetHelper: string
  noObservations: string
  noProposals: string
  dismissObservation: string
  changeProposals: string
  acceptProposal: string
  rejectProposal: string
  preparingErp: string
  stagedForReview: string
  erpPreparationFailed: string
  acceptedNotApplied: string
  unsupportedRuntimeNeed: string
  noFeedback: string
  loadingAccount: string
  accountSettings: string
  demoAndSupport: string
  afterErpReady: string
  primaryAction: string
  manageConnectors: string
  addDataSource: string
  hideDataSources: string
  createYourErp: string
  createAnotherErp: string
  deleteErp: string
  deleteErpConfirmTitle: string
  deleteErpConfirmDescription: string
  deletingErp: string
  erpDeleted: string
  emptyStateExplainer: string
  navDashboard: string
  navWorkspace: string
  navPages: string
  backToSite: string
  heroBadge: string
  heroStepConnect: string
  heroStepInterview: string
  heroStepGenerate: string
  heroStepGoLive: string
  feedbackLockedHint: string
  feedbackEmptyDescription: string
  cancel: string
  evidenceExplainer: string
  contradictionExplainer: string
}

export function getLabels(locale: Locale): AccountLabels {
  if (locale === 'bg') {
    return {
      eyebrow: 'ERP workspace',
      title: 'Изградете ERP от реални доказателства',
      description: 'Свържете системите си. ERP Generator ще зададе само нужните въпроси, ще генерира и тества ERP и ще го подготви за преглед в стейджинг.',
      profileTitle: 'Профил',
      demoTitle: 'Демо',
      name: 'Име',
      email: 'Имейл',
      phone: 'Телефон',
      company: 'Компания',
      position: 'Позиция',
      save: 'Запази промените',
      saving: 'Запазване...',
      saved: 'Промените са запазени.',
      signInTitle: 'Влезте в акаунта си',
      signInDescription: 'Влезте, за да видите профила и демо заявката си.',
      signIn: 'Вход',
      home: 'Начало',
      logout: 'Изход',
      noDemo: 'Нямате активна демо заявка.',
      bookDemo: 'Запази демо',
      currentDemo: 'Активна демо заявка',
      status: 'Статус',
      trialTitle: 'Безплатен период',
      trialDaysLeft: (days: number) => `Остават ${days} дни`,
      trialLastDay: 'Днес е последният ден',
      trialDayOf: (used: number, total: number) => `Ден ${used} от ${total}`,
      trialStartedOn: 'Започна',
      trialEndsOn: 'Завършва',
      trialEnded: 'Безплатният период приключи',
      trialEndedHelper: 'Свържете се с екипа ни, за да продължите работа с вашия ERP.',
      trialNotStarted: 'Безплатният ви период още не е започнал',
      trialNotStartedHelper:
        'Четиринадесетте дни започват да текат в деня, в който създадете първия си ERP.',
      trialCreateErp: 'Създайте своя ERP',
      demoBookTitle: 'Запазете разговор с екипа',
      demoBookHelper:
        'Изберете човек от екипа и свободен час. Ще получите потвърждение по имейл с връзка за среща.',
      demoBookWith: (name: string) => `Запазете разговор с ${name}`,
      demoBookCta: 'Вижте свободните часове',
      demoBookDialogHelper: 'Изберете ден и час, който ви е удобен.',
      demoSlotLength: '30 минути',
      demoManageNote:
        'След запазването получавате имейл с връзки за отказ или преместване на разговора.',
      runtimeTitle: 'ERP среди / lifecycle',
      runtimeEmpty: 'ERP средите ще се появят тук, когато генерираният ERP е готов.',
      tenantName: 'Име на среда',
      tenantSlug: 'Поддомейн',
      tenantIndustry: 'Индустрия',
      tenantEmployees: 'Брой служители',
      tenantGoal: 'Основна цел',
      erpBundle: 'ERP пакет',
      demoBundle: 'Демо IT services ERP',
      generatedErp: 'Генериран ERP',
      generatedErpReady: 'Генериран ERP е готов',
      createTenant: 'Създай ERP среда',
      creatingTenant: 'Създаване...',
      refreshTenants: 'Обнови',
      runtime: 'ERP',
      activation: 'Активация',
      deployment: 'Деплоймънт',
      activeBundle: 'Активна версия',
      stagedBundle: 'Стейджната версия',
      deploymentHistory: 'История',
      discoveryTitle: 'ERP discovery проекти',
      discoveryEmpty: 'Все още няма discovery проект.',
      projectIndustry: 'Индустрия',
      projectCompanyType: 'Тип компания',
      projectGoal: 'Цел на ERP проекта',
      projectLeadership: 'Известни ръководители',
      createProject: 'Създай discovery проект',
      creatingProject: 'Създаване...',
      refreshProjects: 'Обнови',
      startGeneration: 'GO',
      startingGeneration: 'Стартиране...',
      resumingGeneration: 'Продължаване...',
      generateWithAssumptions: 'Генерирай с допускания',
      generatingWithAssumptions: 'Продължаване...',
      generateWithAssumptionsHelper: 'Може да продължите без тези отговори. Липсите ще останат видими като допускания за по-късно уточняване.',
      generationProgress: 'Генериране',
      generationMode: 'Режим на генериране',
      fallbackUsed: 'Fallback използван',
      yes: 'Да',
      pendingWork: 'Чакащи въпроси',
      createInterviewRequest: 'Създай интервю',
      interviewBeingPrepared: 'Интервюто се подготвя автоматично.',
      addEvidenceDocument: 'Добави информация',
      addEvidenceHelper: 'Прикачете документ или поставете данни, които можем да прочетем.',
      submitEvidence: 'Изпрати',
      markResolved: 'Отговори писмено',
      answerHelper: 'Отговорете на въпроса със свои думи.',
      confirmResolved: 'Изпрати отговор',
      answerSubmitted: 'Отговорът е изпратен',
      evidenceSubmitted: 'Информацията е добавена',
      continuingGeneration: 'Благодарим — продължаваме оттам, докъдето стигнахме.',
      actionFailed: 'Нещо се обърка. Опитайте отново.',
      evidenceTitle: 'Заглавие',
      evidenceTitlePlaceholder: 'Кратко заглавие (по избор)',
      evidenceText: 'Детайли',
      evidenceTextPlaceholder: 'Поставете детайлите тук или опишете накратко.',
      resolutionNote: 'Вашият отговор',
      resolutionNotePlaceholder: 'Отговорете на въпроса или обяснете как да продължим.',
      actionInProgress: 'Изпълнение...',
      agentWorking: 'Агентът работи...',
      waitingForAutomaticResume: 'Изчаква автоматично продължаване след получаване на нужната информация.',
      actionNeeded: 'Изисква вашето внимание',
      actionNeededHelper: 'ERP Generator има нужда от няколко неща, за да продължи.',
      technicalProgress: 'Технически детайли',
      artifacts: 'Резултати',
      noGenerationRuns: 'Генерирането още не е стартирано.',
      copyInvite: 'Копирай линк',
      sendInvite: 'Изпрати покана',
      resendInvite: 'Изпрати отново',
      regenerateInvite: 'Генерирай нов линк',
      invite: 'Покана',
      recipient: 'Получател',
      target: 'Цел',
      inviteSent: 'Изпратена',
      inviteFailed: 'Неуспешна',
      inviteNoEmail: 'Няма имейл',
      inviteNotSent: 'Не е изпратена',
      inviteQueued: 'На опашка',
      noEmailKnown: 'Няма известен имейл за тази роля.',
      noEmailCopyFallback: 'Генерирайте нов линк и го споделете през предпочитания от вас канал.',
      participants: 'Участници',
      interviews: 'Интервюта',
      stalledRun: 'Това генериране изглежда спряло. Системата ще го възстанови автоматично, когато устойчивият му lease може безопасно да бъде поет отново.',
      connectedSources: 'Свързани източници',
      connectCompanyData: 'Фирмени данни',
      connectorHelper: 'По избор добавете страници от Notion.',
      searchConnectors: 'Търсене на конектори',
      googleDrive: 'Google Drive',
      sharePoint: 'SharePoint',
      microsoftTeams: 'Microsoft Teams',
      notion: 'Notion',
      slack: 'Slack',
      documentsAndSharedDriveFiles: 'Страници и бази данни',
      messagesAndConversations: 'Съобщения и разговори от екипни канали',
      available: 'Наличен',
      comingSoon: 'Очаквайте скоро',
      notConfigured: 'Не е конфигуриран',
      connectorConnected: 'Свързан',
      connectorNeedsReconnect: 'Нужно е повторно свързване',
      connectorSyncFailed: 'Неуспешно синхронизиране',
      reconnectSource: 'Свържи отново',
      checkSource: 'Провери източника',
      checkingSource: 'Проверка…',
      syncingSource: 'Синхронизиране…',
      connectorCheckFailed: 'В момента не можем да проверим тази връзка. Опитайте отново след малко.',
      connectorResourcesFailed: 'Съдържанието на този източник не можа да бъде заредено.',
      connectorSelectionFailed: 'Обхватът на съдържанието не можа да бъде запазен.',
      connectorDisconnectFailed: 'Източникът не можа да бъде изключен.',
      connectorSetupBeforeGeneration: 'Завършете избора или синхронизирането на свързаните фирмени данни, преди да стартирате. Може и да изключите източник, който не искате да използвате.',
      connectorSyncingBeforeGeneration: 'Изчакайте синхронизирането на фирмените данни да приключи.',
      connectorSyncFailedBeforeGeneration: 'Свързан източник не можа да се синхронизира. Проверете го или го изключете, преди да продължите.',
      connectorReconnectBeforeGeneration: 'Свързан източник трябва да бъде оторизиран отново или изключен, преди да продължите.',
      connectorExpiredBeforeGeneration: 'Свързаните фирмени данни са изтекли. Обновете източника или го изключете.',
      connectorDataNoticeTitle: 'Преди да свържете фирмени данни',
      connectorDataNoticeDescription: 'ERP Generator ще използва съдържанието от тази връзка само за този ERP проект.',
      connectorDataNoticeScope: 'След свързването изберете цялото споделено съдържание от Notion или конкретни страници и бази данни.',
      connectorDataNoticeStorage: 'Синхронизираният текст се съхранява временно в ERP Generator и се изтрива при изключване или изтичане на срока.',
      connectorDataNoticeAi: 'Избраното доказателство може да бъде изпратено към Nango и Claude API за генериране на вашата ERP система. То не се използва за обучение на общ модел.',
      connectorDataNoticeConfirm: 'Потвърждавам, че имам право да предоставя избраното съдържание за тази цел.',
      continueToConnector: 'Продължи към свързване',
      selectConnectorContent: 'Избери съдържание',
      connectorScopeRequired: 'Нужен е обхват',
      manageConnectorContent: 'Управление на съдържанието',
      connectorContentTitle: 'Изберете какво може да използва ERP Generator',
      connectorContentDescription: 'Обхватът се запазва като правило и може да бъде променен по всяко време.',
      searchPages: 'Търсене в Notion',
      selectAllPages: 'Избери всички основни области',
      clearPageSelection: 'Изчисти избора',
      pageSelectionLimit: 'Няма ограничение за броя страници.',
      saveAndSync: 'Запази и синхронизирай',
      selectedPages: 'Избрани области',
      noConnectorResources: 'Няма достъпни страници. Споделете страници с интеграцията и опитайте отново.',
      allAccessibleContent: 'Всичко споделено с ERP Generator — препоръчително',
      allAccessibleContentDescription: 'Включва всички текущи и бъдещи страници, подстраници, бази данни и редове, достъпни за връзката.',
      specificContentAreas: 'Само конкретни области',
      specificContentAreasDescription: 'Изберете само страниците или базите данни, които ERP Generator трябва да използва.',
      everythingAccessible: 'Всичко споделено с тази връзка ще бъде синхронизирано.',
      uncheckedContentExcluded: 'Не е необходимо да избирате или преглеждате отделни елементи.',
      includesDatabaseRows: 'всички редове са включени',
      includesDescendants: 'всичко вътре е включено',
      includedThroughParent: 'включено чрез избраната родителска страница',
      includeDescendantsDescription: 'Избрана страница включва подстраниците си, а база данни включва всички редове. Новото съдържание в избрана област се добавя автоматично.',
      retryConnectorResources: 'Опитай отново',
      disconnectSource: 'Изключи',
      disconnectSourceConfirmTitle: 'Да изключим ли този източник?',
      disconnectSourceConfirmDescription: 'Връзката ще стане недостъпна и временно синхронизираното съдържание ще бъде премахнато веднага от ErpGenerator. Nango прилага своя срок за окончателно изтриване. Доказателства, вече използвани в завършено генериране, остават в историята на проекта до изтриването му.',
      phaseUnderstanding: 'Опознаване на компанията',
      phaseQuestions: 'Уточняващи въпроси',
      phaseBuilding: 'Изграждане на вашата ERP система',
      phaseReviewing: 'Подготовка за преглед',
      progressUnderstandingHeadline: 'ERP Generator чете описанието, отговорите и документите ви, за да разбере как работи компанията.',
      progressQuestionsHeadline: 'ERP Generator запълва празнините — при нужда ще попитаме точния човек.',
      progressBuildingHeadline: 'ERP Generator изгражда и проверява вашата ERP система.',
      progressReviewingHeadline: 'ERP Generator подготвя вашата ERP система за преглед.',
      progressDoneHeadline: 'Вашата ERP система е готова за преглед.',
      progressFailedHeadline: 'Генерирането спря. Информацията ви е запазена.',
      progressResumeFailedHeadline: 'Получихме всичко необходимо, но автоматичното продължаване изисква намеса от екипа ни.',
      progressCancellingHeadline: 'Генерирането се спира безопасно.',
      progressCancelledHeadline: 'Генерирането беше отказано. Завършената работа е запазена.',
      progressWaitingInterview: 'Отговорете на въпросите по-долу, за да продължим.',
      progressWaitingEvidence: 'Чакаме още фирмени документи, за да продължим.',
      progressTitle: 'Докъде сме стигнали',
      progressStepOf: (current, total) => `Стъпка ${current} от ${total}`,
      connectorsLockedDuringGeneration: 'Свързването е заключено, докато ERP Generator работи, за да не се промени материалът под текущия анализ. Ще се отключи, щом приключи.',
      interviewInviteSent: 'Поканата е изпратена',
      interviewAwaitingReply: 'Чака отговор',
      interviewCompleted: 'Отговорено',
      interviewNotSentYet: 'Още не е изпратена',
      interviewsHelper: 'ERP Generator задава кратки въпроси на хората, които познават работата ви най-добре.',
      liveSince: 'На линия от',
      createNewErp: 'Нова ERP система',
      inviteLinkCopied: 'Линкът за интервю е копиран',
      inviteLinkCopyFailed: 'Линкът не можа да бъде копиран. Опитайте отново.',
      projectStatus: (status) => ({
        draft: 'Чернова',
        waiting_for_evidence: 'Изчаква фирмени данни',
        needs_information: 'Събираме информация',
        generated: 'ERP системата е генерирана',
        cancelling: 'Спиране',
        cancelled: 'Отказано',
        failed: 'Спряно',
      } as Record<string, string>)[status] || 'Чернова',
      runStatusPlain: (status) => ({
        queued: 'Започва',
        running: 'Работи',
        waiting_for_input: 'Чака информация',
        cancellation_requested: 'Спира безопасно',
        failed: 'Спряно',
        cancelled: 'Отказано',
        erp_ready: 'Готово за преглед',
        completed: 'Готово',
      } as Record<string, string>)[status] || 'В ход',
      sourceRecords: 'Записи',
      textRecords: 'Текстови записи',
      lastChecked: 'Последна проверка',
      connectedButNoDocuments: 'Свързано, но не са намерени документи',
      documentsFoundNoReadableText: 'Намерени са документи, но без четим текст',
      connectDataSource: 'Свържи източник',
      connectingDataSource: 'Свързване...',
      connectOpened: 'Прозорецът за свързване е отворен. Източникът ще се появи след потвърждение.',
      noConnectedSources: 'Няма свързани документи.',
      runtimeActivationNotice: 'ERP използва отделни runtime акаунти. Активирайте owner акаунта преди вход.',
      ownerActivation: 'Owner активация',
      activationPending: 'Чака активация',
      activationActivated: 'Активирана',
      activationExpired: 'Изтекла',
      activationNotCreated: 'Линк не е генериран',
      regenerateActivationLink: 'Генерирай нов линк',
      copyActivationLink: 'Копирай линк',
      activationLinkReady: 'Активационният линк е готов. Използвайте го сега или генерирайте нов при нужда.',
      generatedModules: 'Генериран обхват',
      knownLimitations: 'Ограничения / отворени въпроси',
      tests: 'Тестове',
      feedbackImprovements: 'Обратна връзка и подобрения',
      recentFeedback: 'Последна обратна връзка',
      usageObservations: 'Какво забеляза ERP Generator',
      feedbackPageIntro:
        'Прегледайте обратната връзка от вашето ERP.',
      feedbackNothingYet: 'Още няма нищо за преглед',
      feedbackNothingYetHelper:
        'Обратната връзка, изпратена от вашето ERP, се показва тук.',
      noObservations: 'Още няма забелязано нищо.',
      noProposals: 'Още няма предложени промени.',
      dismissObservation: 'Отхвърли',
      changeProposals: 'Предложени промени',
      acceptProposal: 'Приеми',
      rejectProposal: 'Отхвърли',
      preparingErp: 'Подготвяме и проверяваме ERP...',
      stagedForReview: 'Стейджнато за преглед',
      erpPreparationFailed: 'Подготовката на ERP е неуспешна',
      acceptedNotApplied: 'Всичко, което приемете, се тества първо върху копие. Реалният ви ERP се променя само след ваше потвърждение.',
      unsupportedRuntimeNeed: 'Нужна е runtime възможност',
      noFeedback: 'Още никой не е изпратил обратна връзка.',
      loadingAccount: 'Зареждане на акаунта...',
      accountSettings: 'Настройки на акаунта',
      demoAndSupport: 'Демо и поддръжка',
      afterErpReady: 'След подготовката на ERP',
      primaryAction: 'Основно действие',
      manageConnectors: 'Добавете или управлявайте конектори',
      addDataSource: 'Добави източник на данни',
      hideDataSources: 'Скрий източниците',
      createYourErp: 'Създайте вашия ERP',
      createAnotherErp: 'Създайте друг ERP',
      deleteErp: 'Изтрий ERP',
      deleteErpConfirmTitle: 'Изтриване на този ERP?',
      deleteErpConfirmDescription: 'Това ще премахне за постоянно този ERP проект и цялата му discovery история. Действието е необратимо.',
      deletingErp: 'Изтриване...',
      erpDeleted: 'ERP проектът е изтрит.',
      emptyStateExplainer: 'Отговорете на няколко кратки въпроса и ERP Generator ще изгради и тества ERP за вашата компания.',
      navDashboard: 'Табло',
      navWorkspace: 'Работно пространство',
      navPages: 'Страници',
      backToSite: 'Обратно към сайта',
      heroBadge: 'AI-native ERP двигател',
      heroStepConnect: 'Свържете системи и документи',
      heroStepInterview: 'AI интервюира екипа ви',
      heroStepGenerate: 'ERP се генерира и тества',
      heroStepGoLive: 'Пускате в продукция',
      feedbackLockedHint: 'Създайте първата си ERP система, за да използвате това',
      feedbackEmptyDescription:
        'Обратната връзка, изпратена от вашето ERP, се показва тук.',
      cancel: 'Отказ',
      evidenceExplainer: 'Не намерихме достатъчно използваема фирмена информация от свързаните източници. Добавете доказателство или отговорете на въпроса, за да продължим.',
      contradictionExplainer: 'Открихме противоречива информация за този процес. Отговорете на въпроса или добавете бележка за решение, за да продължим.',
    }
  }

  return {
    eyebrow: 'ERP workspace',
    title: 'Build your ERP from real evidence',
    description: 'Connect your systems. ERP Generator asks only what remains, generates and tests your ERP, and prepares it for use.',
    profileTitle: 'Profile',
    demoTitle: 'Demo',
    name: 'Name',
    email: 'Email',
    phone: 'Phone',
    company: 'Company',
    position: 'Position',
    save: 'Save changes',
    saving: 'Saving...',
    saved: 'Changes saved.',
    signInTitle: 'Sign in to your account',
    signInDescription: 'Sign in to view your profile and demo request.',
    signIn: 'Login',
    home: 'Home',
    logout: 'Logout',
    noDemo: 'You do not have an active demo request.',
    bookDemo: 'Book demo',
    currentDemo: 'Active demo request',
    status: 'Status',
    trialTitle: 'Free trial',
    trialDaysLeft: (days: number) => `${days} days left`,
    trialLastDay: 'Today is your last day',
    trialDayOf: (used: number, total: number) => `Day ${used} of ${total}`,
    trialStartedOn: 'Started',
    trialEndsOn: 'Ends',
    trialEnded: 'Your free trial has ended',
    trialEndedHelper: 'Talk to our team to keep working with your ERP.',
    trialNotStarted: 'Your free trial has not started yet',
    trialNotStartedHelper: 'The 14 days begin on the day you create your first ERP.',
    trialCreateErp: 'Create your ERP',
    demoBookTitle: 'Book time with the team',
    demoBookHelper:
      'Pick someone from the team and a time that suits you. You will get an email with the meeting link.',
    demoBookWith: (name: string) => `Book a call with ${name}`,
    demoBookCta: 'See available times',
    demoBookDialogHelper: 'Choose a day and a time that works for you.',
    demoSlotLength: '30 minutes',
    demoManageNote:
      'After booking, you will get an email with links to reschedule or cancel the call.',
    runtimeTitle: 'ERP lifecycle / environments',
    runtimeEmpty: 'ERP environments will appear here after a generated ERP is ready.',
    tenantName: 'Environment name',
    tenantSlug: 'Subdomain',
    tenantIndustry: 'Industry',
    tenantEmployees: 'Employees',
    tenantGoal: 'Primary goal',
    erpBundle: 'ERP bundle',
    demoBundle: 'Demo IT services ERP',
    generatedErp: 'Generated ERP',
    generatedErpReady: 'Generated ERP ready',
    createTenant: 'Create ERP environment',
    creatingTenant: 'Creating...',
    refreshTenants: 'Refresh',
    runtime: 'ERP',
    activation: 'Activation',
    deployment: 'Deployment',
    activeBundle: 'Active version',
    stagedBundle: 'Staged version',
    deploymentHistory: 'History',
    discoveryTitle: 'ERP discovery projects',
    discoveryEmpty: 'No discovery project has been created yet.',
    projectIndustry: 'Industry',
    projectCompanyType: 'Company type',
    projectGoal: 'ERP project goal',
    projectLeadership: 'Known leaders',
    createProject: 'Create discovery project',
    creatingProject: 'Creating...',
    refreshProjects: 'Refresh',
    startGeneration: 'GO',
    startingGeneration: 'Starting...',
    resumingGeneration: 'Resuming...',
    generateWithAssumptions: 'Generate with assumptions',
    generatingWithAssumptions: 'Continuing...',
    generateWithAssumptionsHelper: 'You can continue without these answers. The gaps will stay visible as assumptions to refine later.',
    generationProgress: 'Generation',
    generationMode: 'Generation mode',
    fallbackUsed: 'Fallback used',
    yes: 'Yes',
    pendingWork: 'Pending work',
    createInterviewRequest: 'Create interview',
    interviewBeingPrepared: 'The interview is being prepared automatically.',
    addEvidenceDocument: 'Add evidence',
    addEvidenceHelper: 'Attach a document, or paste data we can read.',
    submitEvidence: 'Submit',
    markResolved: 'Answer in writing',
    answerHelper: 'Reply to the question in your own words.',
    confirmResolved: 'Submit answer',
    answerSubmitted: 'Answer submitted',
    evidenceSubmitted: 'Evidence added',
    continuingGeneration: 'Thanks — continuing where we left off.',
    actionFailed: 'Something went wrong. Please try again.',
    evidenceTitle: 'Title',
    evidenceTitlePlaceholder: 'Short title (optional)',
    evidenceText: 'Details',
    evidenceTextPlaceholder: 'Paste the details here, or describe them briefly.',
    resolutionNote: 'Your answer',
    resolutionNotePlaceholder: 'Answer the question, or explain how we should continue.',
    actionInProgress: 'Working...',
    agentWorking: 'Agent working...',
    waitingForAutomaticResume: 'Waiting to continue automatically when the requested information is accepted.',
    actionNeeded: 'Needs your attention',
    actionNeededHelper: 'ERP Generator needs a few things from you before it can continue.',
    technicalProgress: 'Technical details',
    artifacts: 'Results',
    noGenerationRuns: 'Generation has not started yet.',
      copyInvite: 'Copy invite link',
    sendInvite: 'Send invite',
    resendInvite: 'Resend invite',
    regenerateInvite: 'Regenerate invite link',
    invite: 'Invite',
    recipient: 'Recipient',
    target: 'Target',
    inviteSent: 'Sent',
    inviteFailed: 'Failed',
    inviteNoEmail: 'No email',
    inviteNotSent: 'Not sent',
      inviteQueued: 'Queued',
      noEmailKnown: 'No email known for this role.',
      noEmailCopyFallback: 'Create a fresh invite link and share it through your chosen channel.',
      participants: 'Participants',
    interviews: 'Interviews',
    stalledRun: 'This generation appears stalled. It will recover automatically when its durable lease can be reclaimed safely.',
    connectedSources: 'Connected sources',
    connectCompanyData: 'Company data',
    connectorHelper: 'Optionally add pages from Notion.',
    searchConnectors: 'Search connectors',
    googleDrive: 'Google Drive',
    sharePoint: 'SharePoint',
    microsoftTeams: 'Microsoft Teams',
    notion: 'Notion',
    slack: 'Slack',
    documentsAndSharedDriveFiles: 'Pages and databases',
    messagesAndConversations: 'Messages and conversations from team channels',
    available: 'Available',
    comingSoon: 'Coming soon',
    notConfigured: 'Not configured',
    connectorConnected: 'Connected',
    connectorNeedsReconnect: 'Needs reconnect',
    connectorSyncFailed: 'Sync failed',
    reconnectSource: 'Reconnect',
    checkSource: 'Check source',
    checkingSource: 'Checking…',
    syncingSource: 'Syncing…',
    connectorCheckFailed: 'We couldn’t check this connection right now. Please try again in a moment.',
    connectorResourcesFailed: 'We couldn’t load the content available through this source.',
    connectorSelectionFailed: 'We couldn’t save the content scope.',
    connectorDisconnectFailed: 'We couldn’t disconnect this source.',
    connectorSetupBeforeGeneration: 'Finish selecting or synchronizing connected company data before starting. You can also disconnect a source you do not want to use.',
    connectorSyncingBeforeGeneration: 'Waiting for connected company data to finish syncing.',
    connectorSyncFailedBeforeGeneration: 'A connected source could not be synchronized. Check or disconnect it before continuing.',
    connectorReconnectBeforeGeneration: 'A connected source must be authorized again or disconnected before continuing.',
    connectorExpiredBeforeGeneration: 'Connected company data has expired. Refresh or disconnect the source before continuing.',
    connectorDataNoticeTitle: 'Before connecting company data',
    connectorDataNoticeDescription: 'ERP Generator will use content from this connection only for this ERP project.',
    connectorDataNoticeScope: 'After authorization, choose all shared Notion content or specific pages and databases.',
    connectorDataNoticeStorage: 'Synchronized text is stored temporarily by ERP Generator and removed when you disconnect it or its retention period expires.',
    connectorDataNoticeAi: 'Selected evidence may be sent through Nango and the Claude API to generate your ERP. It is not used to train a general model.',
    connectorDataNoticeConfirm: 'I confirm that I am authorized to provide the selected content for this purpose.',
    continueToConnector: 'Continue to connector',
    selectConnectorContent: 'Choose content',
    connectorScopeRequired: 'Scope required',
    manageConnectorContent: 'Manage content',
    connectorContentTitle: 'Choose what ERP Generator can use',
    connectorContentDescription: 'The scope is saved as a rule and can be changed at any time.',
    searchPages: 'Search Notion',
    selectAllPages: 'Select all top-level areas',
    clearPageSelection: 'Clear all',
    pageSelectionLimit: 'There is no page-count limit.',
    saveAndSync: 'Save and sync',
    selectedPages: 'Selected areas',
    noConnectorResources: 'No pages are available. Share pages with the integration and try again.',
    allAccessibleContent: 'Everything shared with ERP Generator — Recommended',
    allAccessibleContentDescription: 'Includes every current and future page, subpage, database, and row this connection can access.',
    specificContentAreas: 'Choose specific areas',
    specificContentAreasDescription: 'Choose only the pages or databases ERP Generator should use.',
    everythingAccessible: 'Everything shared with this connection will be synchronized.',
    uncheckedContentExcluded: 'You do not need to select or review individual items.',
    includesDatabaseRows: 'all rows included',
    includesDescendants: 'everything inside included',
    includedThroughParent: 'included through selected parent',
    includeDescendantsDescription: 'Selecting a page includes its subpages. Selecting a database includes all rows. New content inside a selected area is included automatically.',
    retryConnectorResources: 'Try loading again',
    disconnectSource: 'Disconnect',
    disconnectSourceConfirmTitle: 'Disconnect this source?',
    disconnectSourceConfirmDescription: 'The connection becomes inaccessible and temporary synchronized content is removed from ERP Generator immediately. Nango applies its service retention before hard deletion. Evidence already used by a completed generation remains in project history until that project is deleted.',
    phaseUnderstanding: 'Getting to know your company',
    phaseQuestions: 'Filling in the gaps',
    phaseBuilding: 'Building your ERP',
    phaseReviewing: 'Preparing it for your review',
    progressUnderstandingHeadline: 'ERP Generator is reading your description, answers, and documents to learn how your company runs.',
    progressQuestionsHeadline: 'ERP Generator is filling in gaps, and will ask the right person if it needs to.',
    progressBuildingHeadline: 'ERP Generator is building and testing your ERP.',
    progressReviewingHeadline: 'ERP Generator is getting your ERP ready for you to review.',
    progressDoneHeadline: 'Your ERP is ready for review.',
    progressFailedHeadline: 'Generation stopped. Your evidence is saved.',
    progressResumeFailedHeadline: 'We received everything required, but automatic resume needs attention from our team.',
    progressCancellingHeadline: 'Generation is stopping safely.',
    progressCancelledHeadline: 'Generation was cancelled. Completed work was preserved.',
    progressWaitingInterview: 'Answer the questions below so we can continue.',
    progressWaitingEvidence: 'Waiting for more company documents before we continue.',
    progressTitle: 'Where things stand',
    progressStepOf: (current, total) => `Step ${current} of ${total}`,
    connectorsLockedDuringGeneration: 'Adding sources is paused while ERP Generator works, so the evidence can’t change underneath the current analysis. It unlocks as soon as this finishes.',
    interviewInviteSent: 'Invite sent',
    interviewAwaitingReply: 'Waiting for their answer',
    interviewCompleted: 'Answered',
    interviewNotSentYet: 'Not sent yet',
    interviewsHelper: 'ERP Generator asks short questions of the people who know your operation best.',
    liveSince: 'Live since',
    createNewErp: 'Create new ERP',
    inviteLinkCopied: 'Interview link copied',
    inviteLinkCopyFailed: 'We couldn’t copy the link. Please try again.',
    projectStatus: (status) => ({
      draft: 'Draft',
      waiting_for_evidence: 'Waiting for company data',
      needs_information: 'Gathering information',
      generated: 'ERP generated',
      cancelling: 'Stopping',
      cancelled: 'Cancelled',
      failed: 'Stopped',
    } as Record<string, string>)[status] || 'Draft',
    runStatusPlain: (status) => ({
      queued: 'Starting',
      running: 'Working',
      waiting_for_input: 'Waiting for information',
      cancellation_requested: 'Stopping safely',
      failed: 'Stopped',
      cancelled: 'Cancelled',
      erp_ready: 'Ready for your review',
      completed: 'Done',
    } as Record<string, string>)[status] || 'In progress',
    sourceRecords: 'Records',
    textRecords: 'Text records',
    lastChecked: 'Last checked',
    connectedButNoDocuments: 'Connected but no documents found',
    documentsFoundNoReadableText: 'Documents found, but no readable text',
    connectDataSource: 'Connect source',
    connectingDataSource: 'Connecting...',
    connectOpened: 'Connector window opened. The source will appear after it is confirmed.',
    noConnectedSources: 'No connected documents yet.',
    runtimeActivationNotice: 'Your ERP uses separate runtime accounts. Activate the owner account before signing in.',
    ownerActivation: 'Owner activation',
    activationPending: 'Pending activation',
    activationActivated: 'Activated',
    activationExpired: 'Expired',
    activationNotCreated: 'No link generated',
    regenerateActivationLink: 'Regenerate link',
    copyActivationLink: 'Copy link',
    activationLinkReady: 'Activation link ready. Use it now or regenerate it later if it is lost.',
    generatedModules: 'Generated scope',
    knownLimitations: 'Limitations / open questions',
    tests: 'Tests',
    feedbackImprovements: 'Feedback & improvements',
    recentFeedback: 'Recent feedback',
    usageObservations: 'What ERP Generator noticed',
    feedbackPageIntro:
      'Review feedback from your ERP.',
    feedbackNothingYet: 'Nothing to review yet',
    feedbackNothingYetHelper:
      'Feedback submitted inside your ERP appears here.',
    noObservations: 'Nothing noticed yet.',
    noProposals: 'No changes suggested yet.',
    dismissObservation: 'Dismiss',
    changeProposals: 'Suggested changes',
    acceptProposal: 'Accept',
    rejectProposal: 'Reject',
    preparingErp: 'Preparing and testing your ERP...',
    stagedForReview: 'Staged for review',
    erpPreparationFailed: 'ERP preparation failed',
    acceptedNotApplied: 'Anything you accept is tested on a copy first. Your live ERP only changes when you confirm.',
    unsupportedRuntimeNeed: 'Runtime support needed',
    noFeedback: 'No one has sent feedback yet.',
    loadingAccount: 'Loading account...',
    accountSettings: 'Account settings',
    demoAndSupport: 'Demo and support',
    afterErpReady: 'After your ERP is ready',
    primaryAction: 'Primary action',
    manageConnectors: 'Add or manage connectors',
    addDataSource: 'Add a data source',
    hideDataSources: 'Hide data sources',
    createYourErp: 'Create your ERP',
    createAnotherErp: 'Create another ERP',
    deleteErp: 'Delete ERP',
    deleteErpConfirmTitle: 'Delete this ERP?',
    deleteErpConfirmDescription: 'This permanently removes this ERP project and its discovery history. This cannot be undone.',
    deletingErp: 'Deleting...',
    erpDeleted: 'ERP project deleted.',
    emptyStateExplainer: 'Answer a few short questions and ERP Generator will build and test an ERP for your company.',
    navDashboard: 'Dashboard',
    navWorkspace: 'Workspace',
    navPages: 'Pages',
    backToSite: 'Back to site',
    heroBadge: 'AI-native ERP engine',
    heroStepConnect: 'Connect systems & documents',
    heroStepInterview: 'AI interviews your team',
    heroStepGenerate: 'Your ERP is generated & tested',
    heroStepGoLive: 'You go live',
    feedbackLockedHint: 'Create your first ERP to use this',
    feedbackEmptyDescription:
      'Feedback submitted inside your ERP appears here.',
    cancel: 'Cancel',
    evidenceExplainer: 'We could not find enough usable company information from the connected data sources. Add evidence or answer the requested question to continue.',
    contradictionExplainer: 'We found conflicting information about this workflow. Answer the question or add a resolution note to continue.',
  }
}
