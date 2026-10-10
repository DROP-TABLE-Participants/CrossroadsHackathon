import type { Locale } from '@/i18n/config'

/**
 * A pickable answer. `key` is the locale-stable value held in the draft; `label`
 * is what the owner reads *and* what gets composed into the string sent to the
 * API — so the wire value stays in the customer's own language, exactly as it
 * did when these were free-text fields.
 */
export type WizardOption = { key: string; label: string }

/** Reveals the industry free-text escape hatch. */
export const INDUSTRY_OTHER_KEY = 'other'

export type WizardCopy = {
  pageTitle: string
  stepLabel: (current: number, total: number) => string
  back: string
  next: string
  skip: string
  cancel: string
  leaveConfirm: string
  optional: string
  steps: {
    company: {
      title: string
      explainer: string
      label: string
      placeholder: string
      required: string
    }
    business: {
      title: string
      explainer: string
      industryOptions: WizardOption[]
      industryRequired: string
      otherLabel: string
      otherPlaceholder: string
      otherRequired: string
      profileHeading: string
      profileExplainer: string
      sizeLabel: string
      sizeOptions: WizardOption[]
      ownershipLabel: string
      ownershipOptions: WizardOption[]
      sitesLabel: string
      sitesOptions: WizardOption[]
      profileRequired: string
    }
    goal: {
      title: string
      explainer: string
      painsLabel: string
      painOptions: WizardOption[]
      notesLabel: string
      notesPlaceholder: string
      required: string
      composedPainsHeading: string
      composedNotesHeading: string
    }
    people: {
      title: string
      explainer: string
      nameLabel: string
      emailLabel: string
      roleLabel: string
      namePlaceholder: string
      emailPlaceholder: string
      rolePlaceholder: string
      addPerson: string
      removePerson: string
      emailInvalid: string
      emptyHint: string
    }
    connect: {
      title: string
      explainer: string
      detail: string
      sources: string[]
    }
    review: {
      title: string
      explainer: string
      companyHeading: string
      businessHeading: string
      goalHeading: string
      peopleHeading: string
      peopleNone: string
      edit: string
      launch: string
      launching: string
      submitError: string
    }
  }
}

const en: WizardCopy = {
  pageTitle: 'Create your ERP',
  stepLabel: (current, total) => `Step ${current} of ${total}`,
  back: 'Back',
  next: 'Continue',
  skip: 'Skip for now',
  cancel: 'Back to account',
  leaveConfirm: 'Your answers are saved in this browser tab. Leave anyway?',
  optional: 'Optional',
  steps: {
    company: {
      title: 'What is your company called?',
      explainer:
        'This names your ERP workspace. You can always rename it later — nothing here is final.',
      label: 'Company name',
      placeholder: 'e.g. Balkan Furniture Ltd',
      required: 'Please enter your company name.',
    },
    business: {
      title: 'What does your company make or do?',
      explainer:
        'Pick the closest match. This is how ERP Generator knows which processes matter for a company like yours.',
      industryOptions: [
        { key: 'furniture', label: 'Furniture manufacturing' },
        { key: 'food_beverage', label: 'Food & beverage' },
        { key: 'metal', label: 'Metal fabrication' },
        { key: 'plastics', label: 'Plastics & rubber' },
        { key: 'textiles', label: 'Textiles & apparel' },
        { key: 'machinery', label: 'Machinery & equipment' },
        { key: 'electronics', label: 'Electronics' },
        { key: 'print_packaging', label: 'Printing & packaging' },
        { key: 'construction_materials', label: 'Construction materials' },
        { key: 'chemicals', label: 'Chemicals' },
        { key: INDUSTRY_OTHER_KEY, label: 'Something else…' },
      ],
      industryRequired: 'Please pick what your company makes or does.',
      otherLabel: 'Tell us in your own words',
      otherPlaceholder: 'e.g. Custom marine equipment',
      otherRequired: 'Please describe what your company makes or does.',
      profileHeading: 'A bit about the company',
      profileExplainer: 'Three quick taps. This shapes how much structure your ERP starts with.',
      sizeLabel: 'How many people work there?',
      sizeOptions: [
        { key: 'micro', label: '1–10 people' },
        { key: 'small', label: '11–50 people' },
        { key: 'medium', label: '51–200 people' },
        { key: 'large', label: '200+ people' },
      ],
      ownershipLabel: 'Who owns it?',
      ownershipOptions: [
        { key: 'family', label: 'Family-owned' },
        { key: 'partners', label: 'Partner-owned' },
        { key: 'group', label: 'Part of a group' },
      ],
      sitesLabel: 'How many sites?',
      sitesOptions: [
        { key: 'single', label: 'Single site' },
        { key: 'multi', label: 'Multiple sites' },
      ],
      profileRequired: 'Please answer all three — each is a single tap.',
    },
    goal: {
      title: 'Where does the company lose the most time or money today?',
      explainer:
        'Pick everything that sounds familiar. This tells ERP Generator what to fix first — nothing here is locked in.',
      painsLabel: 'Pick everything that sounds familiar',
      painOptions: [
        { key: 'order_status', label: 'Chasing order status by phone' },
        { key: 'true_cost', label: 'Nobody knows the real cost of a product' },
        { key: 'inventory_spreadsheets', label: 'Inventory and purchasing live in spreadsheets' },
        { key: 'slow_quoting', label: 'Quoting a customer takes days' },
        { key: 'paper_floor', label: 'The shop floor still runs on paper' },
        { key: 'month_end', label: 'Surprises at month-end' },
      ],
      notesLabel: 'Anything in your own words?',
      notesPlaceholder:
        'e.g. I want to see the real cost of every order, and stop chasing production status over the phone.',
      required: 'Please pick at least one, or describe it in your own words.',
      composedPainsHeading: 'Where we lose the most time or money today:',
      composedNotesHeading: 'In the owner’s words:',
    },
    people: {
      title: 'Who knows your operations best?',
      explainer:
        'ERP Generator interviews the people who run things day to day — a short online chat, in their own time. Add the leaders you trust; you can skip this and add them later.',
      nameLabel: 'Name',
      emailLabel: 'Email',
      roleLabel: 'Role',
      namePlaceholder: 'e.g. Maria Petrova',
      emailPlaceholder: 'maria@company.com',
      rolePlaceholder: 'e.g. Head of Production',
      addPerson: 'Add another person',
      removePerson: 'Remove',
      emailInvalid: 'Please enter a valid email address.',
      emptyHint: 'No one added yet — that’s fine, you can invite people after launch too.',
    },
    connect: {
      title: 'Your documents do the talking',
      explainer:
        'After launch, you can connect the places where your company already keeps its knowledge. ERP Generator reads the evidence so your team answers fewer questions.',
      detail:
        'Nothing is required now. Connecting happens with one click from your account page, whenever you’re ready.',
      sources: ['Google Drive', 'SharePoint', 'Notion', 'Slack', 'Microsoft Teams'],
    },
    review: {
      title: 'Ready to launch?',
      explainer:
        'Check your answers. Launching creates your ERP project — ERP Generator will guide you step by step from there, and nothing goes live without your review.',
      companyHeading: 'Company',
      businessHeading: 'Business',
      goalHeading: 'Goal',
      peopleHeading: 'People to interview',
      peopleNone: 'No one added yet — you can invite people after launch.',
      edit: 'Edit',
      launch: 'Create my ERP project',
      launching: 'Creating your project…',
      submitError: 'We couldn’t create the project right now. Please try again in a moment.',
    },
  },
}

const bg: WizardCopy = {
  pageTitle: 'Създайте вашата ERP система',
  stepLabel: (current, total) => `Стъпка ${current} от ${total}`,
  back: 'Назад',
  next: 'Продължи',
  skip: 'Пропусни засега',
  cancel: 'Обратно към акаунта',
  leaveConfirm: 'Отговорите ви са запазени в този раздел на браузъра. Да напуснем ли въпреки това?',
  optional: 'По желание',
  steps: {
    company: {
      title: 'Как се казва вашата компания?',
      explainer:
        'Това дава име на вашето ERP работно пространство. Винаги можете да го промените по-късно — нищо тук не е окончателно.',
      label: 'Име на компанията',
      placeholder: 'напр. Балкан Мебел ЕООД',
      required: 'Моля, въведете името на компанията.',
    },
    business: {
      title: 'С какво се занимава вашата компания?',
      explainer:
        'Изберете най-близкото. Така ERP Generator разбира кои процеси са важни за компания като вашата.',
      industryOptions: [
        { key: 'furniture', label: 'Производство на мебели' },
        { key: 'food_beverage', label: 'Храни и напитки' },
        { key: 'metal', label: 'Металообработка' },
        { key: 'plastics', label: 'Пластмаси и каучук' },
        { key: 'textiles', label: 'Текстил и облекло' },
        { key: 'machinery', label: 'Машини и оборудване' },
        { key: 'electronics', label: 'Електроника' },
        { key: 'print_packaging', label: 'Печат и опаковки' },
        { key: 'construction_materials', label: 'Строителни материали' },
        { key: 'chemicals', label: 'Химическа промишленост' },
        { key: INDUSTRY_OTHER_KEY, label: 'Нещо друго…' },
      ],
      industryRequired: 'Моля, изберете с какво се занимава компанията ви.',
      otherLabel: 'Опишете със свои думи',
      otherPlaceholder: 'напр. Оборудване за яхти по поръчка',
      otherRequired: 'Моля, опишете с какво се занимава компанията ви.',
      profileHeading: 'Малко за компанията',
      profileExplainer:
        'Три бързи избора. Това определя с колко структура стартира вашата ERP система.',
      sizeLabel: 'Колко души работят в нея?',
      sizeOptions: [
        { key: 'micro', label: '1–10 души' },
        { key: 'small', label: '11–50 души' },
        { key: 'medium', label: '51–200 души' },
        { key: 'large', label: 'Над 200 души' },
      ],
      ownershipLabel: 'Кой е собственик?',
      ownershipOptions: [
        { key: 'family', label: 'Семейна фирма' },
        { key: 'partners', label: 'Съдружници' },
        { key: 'group', label: 'Част от група' },
      ],
      sitesLabel: 'Колко обекта има?',
      sitesOptions: [
        { key: 'single', label: 'Един обект' },
        { key: 'multi', label: 'Няколко обекта' },
      ],
      profileRequired: 'Моля, отговорете и на трите — всеки е един клик.',
    },
    goal: {
      title: 'Къде компанията губи най-много време или пари днес?',
      explainer:
        'Изберете всичко, което ви звучи познато. Това казва на ERP Generator какво да поправи първо — нищо не е окончателно.',
      painsLabel: 'Изберете всичко, което ви звучи познато',
      painOptions: [
        { key: 'order_status', label: 'Издирване на статуса на поръчките по телефона' },
        { key: 'true_cost', label: 'Никой не знае реалната себестойност на продукт' },
        { key: 'inventory_spreadsheets', label: 'Складът и доставките се водят в таблици' },
        { key: 'slow_quoting', label: 'Изготвянето на оферта отнема дни' },
        { key: 'paper_floor', label: 'Производството още работи на хартия' },
        { key: 'month_end', label: 'Изненади в края на месеца' },
      ],
      notesLabel: 'Нещо със свои думи?',
      notesPlaceholder:
        'напр. Искам да виждам реалната себестойност на всяка поръчка и да спра да издирвам статуса на производството по телефона.',
      required: 'Моля, изберете поне едно или опишете със свои думи.',
      composedPainsHeading: 'Къде губим най-много време или пари днес:',
      composedNotesHeading: 'Със думите на собственика:',
    },
    people: {
      title: 'Кой познава операциите ви най-добре?',
      explainer:
        'ERP Generator интервюира хората, които движат нещата ежедневно — кратък онлайн чат, в удобно за тях време. Добавете ръководителите, на които имате доверие; можете да пропуснете и да ги добавите по-късно.',
      nameLabel: 'Име',
      emailLabel: 'Имейл',
      roleLabel: 'Роля',
      namePlaceholder: 'напр. Мария Петрова',
      emailPlaceholder: 'maria@company.com',
      rolePlaceholder: 'напр. Ръководител производство',
      addPerson: 'Добави още човек',
      removePerson: 'Премахни',
      emailInvalid: 'Моля, въведете валиден имейл адрес.',
      emptyHint: 'Още няма добавени — това е наред, можете да поканите хора и след старта.',
    },
    connect: {
      title: 'Вашите документи говорят вместо вас',
      explainer:
        'След старта можете да свържете местата, където компанията ви вече съхранява знанията си. ERP Generator чете доказателствата, за да отговаря екипът ви на по-малко въпроси.',
      detail:
        'Сега не се изисква нищо. Свързването става с един клик от страницата на акаунта ви, когато сте готови.',
      sources: ['Google Drive', 'SharePoint', 'Notion', 'Slack', 'Microsoft Teams'],
    },
    review: {
      title: 'Готови ли сте за старт?',
      explainer:
        'Прегледайте отговорите си. Стартът създава вашия ERP проект — ERP Generator ще ви води стъпка по стъпка, и нищо не влиза в действие без вашия преглед.',
      companyHeading: 'Компания',
      businessHeading: 'Бизнес',
      goalHeading: 'Цел',
      peopleHeading: 'Хора за интервю',
      peopleNone: 'Още няма добавени — можете да поканите хора след старта.',
      edit: 'Редактирай',
      launch: 'Създай моя ERP проект',
      launching: 'Създаване на проекта…',
      submitError: 'В момента не можем да създадем проекта. Моля, опитайте отново след малко.',
    },
  },
}

export function getWizardCopy(locale: Locale): WizardCopy {
  return locale === 'bg' ? bg : en
}
