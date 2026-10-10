'use client'

import Link from 'next/link'
import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import type { Locale } from '@/i18n/config'

type Demo = { id: string; status: string; failureMessage?: string }
function openForm(action: string) {
  const form = document.createElement('form')
  form.method = 'POST'
  form.action = action
  document.body.appendChild(form)
  form.submit()
}

export function DandelionDemoCard({ locale }: { locale: Locale }) {
  const [demos, setDemos] = useState<Demo[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const bg = locale === 'bg'
  const load = useCallback(async () => {
    const response = await fetch('/api/customers/demos', { cache: 'no-store' })
    if (!response.ok) throw new Error(bg ? 'Демото не е достъпно. Опитайте отново.' : 'The demo is unavailable. Please try again.')
    setDemos((await response.json()).demos)
  }, [bg])
  const current = demos.find(v => v.status === 'assigned')
  const preparing = demos.some(v => v.status === 'queued' || v.status === 'preparing')
  useEffect(() => { void load().catch(e => setError(e.message)) }, [load])
  useEffect(() => {
    if (!preparing) return
    const timer = setInterval(() => { void load().catch(e => setError(e.message)) }, 2000)
    return () => clearInterval(timer)
  }, [preparing, load])
  useEffect(() => {
    const host = new URLSearchParams(window.location.search).get('openHost')
    if (host) openForm('/api/customers/runtime/open?host=' + encodeURIComponent(host))
  }, [])
  async function prepare(reset = false) {
    setBusy(true); setError('')
    try {
      const response = await fetch(reset && current ? `/api/customers/demos/${current.id}/reset` : '/api/customers/demos', { method: 'POST' })
      if (!response.ok) throw new Error(bg ? 'Подготовката не успя. Опитайте отново.' : 'Preparation failed. Please try again.')
      const demo: Demo = await response.json()
      await load()
      if (!reset && demo.status === 'assigned') openForm(`/api/customers/demos/${demo.id}/open`)
    } catch (e) { setError(e instanceof Error ? e.message : 'Please try again') }
    finally { setBusy(false) }
  }
  return <Card className="overflow-hidden border-primary/20 p-6 sm:p-8">
    <div className="mb-4 text-xs font-medium uppercase tracking-wider text-primary">{bg ? 'Подготвено демо · Симулирани операции' : 'Curated demo · Simulated operations'}</div>
    <h1 className="text-3xl font-semibold tracking-tight">Dandelion Chocolate</h1>
    <p className="mt-3 max-w-2xl text-muted-foreground">{bg ? 'Производство на шоколад в Сан Франциско. Изпълнете поръчка за 600 шоколада: 200 налични → произведете 400 → одобрете качеството → изпратете 600.' : 'San Francisco chocolate manufacturing. Fulfil an order for 600 bars: 200 available → produce 400 → release quality → ship 600.'}</p>
    <div className="my-5 grid grid-cols-2 gap-3 sm:grid-cols-4">{[bg ? '3 шоколада' : '3 chocolate bars', bg ? 'Партиди и рецепти' : 'Lots & recipes', bg ? 'Контрол на качеството' : 'Quality checks', bg ? 'Поръчки на едро' : 'Wholesale orders'].map(v => <div key={v} className="rounded-lg bg-muted p-3 text-sm">{v}</div>)}</div>
    <div className="flex flex-wrap gap-3">
      {current ? <form action={`/api/customers/demos/${current.id}/open`} method="post"><Button type="submit">{bg ? 'Отвори демо ERP' : 'Open demo ERP'}</Button></form> : <Button onClick={() => void prepare()} disabled={busy || preparing}>{preparing ? (bg ? 'Подготвяме вашето демо…' : 'Preparing your demo…') : (bg ? 'Отвори демо ERP' : 'Open demo ERP')}</Button>}
      <Button asChild variant="outline"><Link href={`/${locale}/account/create?template=dandelion-chocolate`}>{bg ? 'Персонализирай и генерирай' : 'Customize and generate'}</Link></Button>
      {current && <Button variant="ghost" disabled={busy || preparing} onClick={() => void prepare(true)}>{bg ? 'Ново демо' : 'Reset demo'}</Button>}
    </div>
    {preparing && current && <p className="mt-3 text-sm">{bg ? 'Подготвяме ново копие. Текущото демо остава достъпно.' : 'Preparing a fresh copy. Your current demo remains available.'}</p>}
    {(error || demos.find(v => v.status === 'failed')?.failureMessage) && <p role="alert" className="mt-3 text-sm text-destructive">{error || (bg ? 'Подготовката не успя. Опитайте отново.' : 'Preparation failed. Please try again.')} <Button variant="link" onClick={() => void prepare()}>{bg ? 'Опитай отново' : 'Try again'}</Button></p>}
    <p className="mt-5 text-xs text-muted-foreground">{bg ? 'Данните за компанията са базирани на публична информация. Всички оперативни записи са симулирани.' : 'Company information is based on public sources. All operational records are simulated.'}</p>
  </Card>
}
