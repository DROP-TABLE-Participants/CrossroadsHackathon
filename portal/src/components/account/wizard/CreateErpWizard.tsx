'use client'

import { useEffect, useMemo, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { useDemoBooking } from '@/components/booking/DemoBookingProvider'
import { getLabels } from '@/components/account/labels'
import { useGenerationProjects } from '@/components/account/hooks/useGenerationProjects'
import { ConnectorsPanel } from '@/components/account/projects/ConnectorsPanel'
import type { GenerationProjectSummary } from '@/components/account/types'
import type { Locale } from '@/i18n/config'

type Draft = { company: string; description: string; answers: string[]; choices: string[]; prompt: string }
const empty: Draft = { company: '', description: '', answers: ['', '', ''], choices: ['quality_approval'], prompt: '' }

export function CreateErpWizard({ locale }: { locale: Locale }) {
  const bg = locale === 'bg'
  const router = useRouter()
  const { customer, openLogin } = useDemoBooking()
  const labels = useMemo(() => getLabels(locale), [locale])
  const api = useGenerationProjects({ labels, openLogin, customer })
  const [draft, setDraft] = useState<Draft>(empty)
  const [template, setTemplate] = useState(false)
  const [step, setStep] = useState(0)
  const [project, setProject] = useState<GenerationProjectSummary | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [loaded, setLoaded] = useState(false)
  const questions = bg ? ['Какво произвеждате или продавате и на кого?', 'Как протича поръчката от получаване до доставка?', 'Какви материали, проверки и записи трябва да проследявате?'] : ['What do you make or sell, and to whom?', 'How does an order move from request to delivery?', 'Which materials, checks, and records must you track?']
  const choices = [
    ['tasting_notes', bg ? 'Дегустационни бележки за партидите' : 'Add batch tasting notes'],
    ['cocoa_origin', bg ? 'Произход на какаото и година на реколтата' : 'Track cocoa origin and harvest year'],
    ['quality_approval', bg ? 'Одобрение на качеството преди изпращане' : 'Require quality approval before shipment'],
  ]
  const key = 'erp-generator-create-draft'
  useEffect(() => {
    const fromTemplate = new URLSearchParams(window.location.search).has('template')
    setTemplate(fromTemplate)
    try {
      const saved = window.localStorage.getItem(key + (fromTemplate ? '-dandelion' : ''))
      if (saved) setDraft({ ...empty, ...JSON.parse(saved) })
      else if (fromTemplate) setDraft({ ...empty, company: 'Dandelion Chocolate' })
    } catch { /* A damaged draft can be safely replaced. */ }
    setLoaded(true)
  }, [])
  useEffect(() => { if (loaded) window.localStorage.setItem(key + (template ? '-dandelion' : ''), JSON.stringify(draft)) }, [draft, loaded, template])
  const edit = (values: Partial<Draft>) => setDraft(value => ({ ...value, ...values }))
  async function save() {
    if (project) return project
    const route = template ? '/api/customers/generation-projects/create-from-template' : '/api/customers/generation-projects'
    const body = template ? { templateVersion: 'dandelion-chocolate.v1', choices: draft.choices, prompt: draft.prompt } : {
      companyName: draft.company, goal: 'Create a practical ERP from the supplied business description and interview evidence. Ask explicit questions where essential information is missing.', industry: 'Business operations', companyType: 'Company', discoveryMode: 'hybrid',
      description: draft.description,
      interviewAnswers: questions.flatMap((question, i) => draft.answers[i]?.trim() ? [{ question, answer: draft.answers[i] }] : []),
    }
    const response = await fetch(route, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
    const payload = await response.json()
    if (!response.ok) throw new Error(payload.message || (bg ? 'Неуспешно запазване. Опитайте отново.' : 'Could not save your project. Please try again.'))
    const created = payload.project as GenerationProjectSummary
    setProject(created); api.setProjects([created])
    return created
  }
  async function next(connect = false) {
    setBusy(true); setError('')
    try { if (step === 1) await save(); if (!connect) setStep(v => v + 1) }
    catch (e) { setError(e instanceof Error ? e.message : 'Please try again') }
    finally { setBusy(false) }
  }
  async function build() {
    setBusy(true); setError('')
    try {
      const created = await save()
      const response = await fetch(`/api/customers/generation-projects/${created.id}/start-generation`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ deployToRuntime: true }) })
      const payload = await response.json()
      if (!response.ok) throw new Error(payload.message || (bg ? 'Генерирането не започна. Доказателствата са запазени.' : 'Generation could not start. Your evidence is saved.'))
      window.localStorage.removeItem(key + (template ? '-dandelion' : ''))
      router.push(`/${locale}/account?created=${created.id}`)
    } catch (e) { setError(e instanceof Error ? e.message : 'Please try again') }
    finally { setBusy(false) }
  }
  const hasEvidence = template || !!draft.description.trim() || draft.answers.some(v => v.trim())
  const liveProject = api.projects.find(v => v.id === project?.id) ?? project
  return <section className="mx-auto w-full max-w-3xl space-y-5">
    <ol className="grid grid-cols-3 gap-2 text-sm">{(bg ? ['Опишете бизнеса', 'Добавете контекст', 'Преглед и създаване'] : ['Describe your business', 'Add context', 'Review and build']).map((label, i) => <li key={label} className={`rounded-lg border p-3 ${i === step ? 'border-primary bg-primary/5 font-medium' : 'text-muted-foreground'}`}>{i + 1}. {label}</li>)}</ol>
    <Card className="space-y-5 p-6">
      <h1 className="text-2xl font-semibold">{template ? (bg ? 'Персонализирайте Dandelion Chocolate' : 'Customize Dandelion Chocolate') : (bg ? 'Създайте вашето ERP' : 'Create your ERP')}</h1>
      {step === 0 && <>
        <label className="grid gap-2 text-sm">{bg ? 'Име на компанията' : 'Company name'}<Input value={draft.company} maxLength={160} disabled={!!project || template} onChange={e => edit({ company: e.target.value })} /></label>
        {template ? <>
          <p className="text-sm text-muted-foreground">{bg ? 'Новото ERP се генерира отделно. Подготвеното демо остава достъпно.' : 'A new ERP is generated separately. Your prepared demo stays available.'}</p>
          {choices.map(([value, label]) => <label key={value} className="flex items-center gap-3 text-sm"><input type="checkbox" disabled={!!project} checked={draft.choices.includes(value)} onChange={e => edit({ choices: e.target.checked ? [...draft.choices, value] : draft.choices.filter(v => v !== value) })} />{label}</label>)}
          <label className="grid gap-2 text-sm">{bg ? 'Други промени (по избор)' : 'Other changes (optional)'}<Textarea value={draft.prompt} maxLength={4000} disabled={!!project} onChange={e => edit({ prompt: e.target.value })} /></label>
        </> : <>
          <label className="grid gap-2 text-sm">{bg ? 'Опишете дейността и процесите си' : 'Describe your company and its processes'}<Textarea rows={6} value={draft.description} maxLength={50000} disabled={!!project} onChange={e => edit({ description: e.target.value })} /></label>
        </>}
      </>}
      {step === 1 && <>
        {!template && questions.map((question, i) => <label key={question} className="grid gap-2 text-sm">{question}<Textarea rows={3} maxLength={4000} disabled={!!project} value={draft.answers[i]} onChange={e => edit({ answers: draft.answers.map((v, n) => n === i ? e.target.value : v) })} /></label>)}
        <div className="border-t pt-4"><h2 className="mb-2 font-medium">Notion ({bg ? 'по избор' : 'optional'})</h2><p className="mb-3 text-sm text-muted-foreground">{bg ? 'Свържете Notion и изберете страници, които описват бизнеса ви.' : 'Connect Notion and select the pages that describe your business.'}</p>{liveProject ? <ConnectorsPanel labels={labels} locale={locale} project={liveProject} projectsApi={api} /> : <Button variant="outline" disabled={busy || !hasEvidence} onClick={() => void next(true)}>{bg ? 'Запази контекста и свържи Notion' : 'Save context and connect Notion'}</Button>}</div>
      </>}
      {step === 2 && <>
        <h2 className="font-medium">{bg ? 'Спецификация' : 'Specification preview'}</h2>
        <p>{draft.company}</p>
        {template ? <><p className="text-sm">{bg ? 'Производство на шоколад: продукти, съставки и опаковки, рецепти, производствени партиди, качество, клиенти и поръчки на едро.' : 'Chocolate manufacturing: products, ingredients and packaging, recipes, production batches, quality, customers and wholesale orders.'}</p><ul className="list-disc space-y-1 pl-5 text-sm">{choices.filter(([v]) => draft.choices.includes(v)).map(([v, label]) => <li key={v}>{label}</li>)}</ul><p className="whitespace-pre-wrap text-sm">{draft.prompt}</p></> : <><p className="whitespace-pre-wrap text-sm">{draft.description}</p>{questions.map((q, i) => draft.answers[i]?.trim() && <div key={q} className="text-sm"><p className="font-medium">{q}</p><p className="whitespace-pre-wrap">{draft.answers[i]}</p></div>)}</>}
        <p className="text-sm text-muted-foreground">{bg ? 'Ще получите въпроси, ако липсва важна информация. ERP се отваря след компилиране и успешно изпълнение на проверките.' : 'You will receive questions if essential information is missing. Your ERP opens after compilation and successful business checks.'}</p>
      </>}
      {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
      <div className="flex flex-wrap justify-between gap-3"><Button variant="ghost" disabled={busy || step === 0} onClick={() => setStep(v => v - 1)}>{bg ? 'Назад' : 'Back'}</Button>{step < 2 ? <Button disabled={busy || !draft.company.trim() || (step === 0 && !template && !draft.description.trim()) || (step === 1 && !hasEvidence)} onClick={() => void next()}>{busy ? (bg ? 'Запазване…' : 'Saving…') : (bg ? 'Продължи' : 'Continue')}</Button> : <Button disabled={busy} onClick={() => void build()}>{busy ? (bg ? 'Започване…' : 'Starting…') : (bg ? 'Създай ERP' : 'Build ERP')}</Button>}</div>
    </Card>
  </section>
}
