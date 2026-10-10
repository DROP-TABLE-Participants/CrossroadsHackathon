'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import {
  CaretDown,
  CaretRight,
  CheckCircle,
  Database,
  FileText,
} from '@phosphor-icons/react'

import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { Spinner } from '@/components/ui/spinner'
import type { AccountLabels } from '@/components/account/labels'
import type {
  ConnectorResourceSummary,
  ConnectorScopeInput,
  GenerationInputSourceSummary,
} from '@/components/account/types'

type ConnectorScopeDialogProps = {
  labels: AccountLabels
  source: GenerationInputSourceSummary | null
  resources: ConnectorResourceSummary[]
  loading: boolean
  saving: boolean
  onOpenChange: (open: boolean) => void
  onLoadResources: () => Promise<void>
  onSave: (scope: ConnectorScopeInput) => Promise<boolean>
}

type ResourceTree = {
  roots: ConnectorResourceSummary[]
  byId: Map<string, ConnectorResourceSummary>
  childrenByParent: Map<string, ConnectorResourceSummary[]>
}

type ResourceRow = {
  resource: ConnectorResourceSummary
  depth: number
  hasChildren: boolean
}

function buildResourceTree(resources: ConnectorResourceSummary[]): ResourceTree {
  const selectable = resources.filter((resource) => resource.kind !== 'database_row')
  const byId = new Map(selectable.map((resource) => [resource.id, resource]))
  const roots: ConnectorResourceSummary[] = []
  const childrenByParent = new Map<string, ConnectorResourceSummary[]>()

  for (const resource of selectable) {
    if (resource.parentId && byId.has(resource.parentId)) {
      const children = childrenByParent.get(resource.parentId) || []
      children.push(resource)
      childrenByParent.set(resource.parentId, children)
    } else {
      roots.push(resource)
    }
  }

  return { roots, byId, childrenByParent }
}

function ancestorIds(resourceId: string, tree: ResourceTree) {
  const ancestors: string[] = []
  let current = tree.byId.get(resourceId)
  const visited = new Set<string>()
  while (current?.parentId && tree.byId.has(current.parentId) && !visited.has(current.parentId)) {
    visited.add(current.parentId)
    ancestors.push(current.parentId)
    current = tree.byId.get(current.parentId)
  }
  return ancestors
}

function descendantIds(resourceId: string, tree: ResourceTree) {
  const descendants: string[] = []
  const visit = (parentId: string) => {
    for (const child of tree.childrenByParent.get(parentId) || []) {
      descendants.push(child.id)
      visit(child.id)
    }
  }
  visit(resourceId)
  return descendants
}

export function ConnectorScopeDialog({
  labels,
  source,
  resources,
  loading,
  saving,
  onOpenChange,
  onLoadResources,
  onSave,
}: ConnectorScopeDialogProps) {
  const [mode, setMode] = useState<'all_accessible' | 'selected_roots'>(
    source?.scopeMode === 'selected_roots' ? 'selected_roots' : 'all_accessible',
  )
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<string[]>(source?.includedRootIds || [])
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const initialLoadStarted = useRef(false)
  const tree = useMemo(() => buildResourceTree(resources), [resources])

  useEffect(() => {
    if (
      !source ||
      source.scopeMode !== 'selected_roots' ||
      resources.length ||
      loading ||
      initialLoadStarted.current
    ) return
    initialLoadStarted.current = true
    void onLoadResources()
  }, [loading, onLoadResources, resources.length, source])

  useEffect(() => {
    if (!tree.roots.length) return
    setExpanded((current) => {
      const next = new Set(current)
      for (const root of tree.roots) {
        if (tree.childrenByParent.has(root.id)) next.add(root.id)
      }
      return next
    })
  }, [tree])

  const matchingIds = useMemo(() => {
    const normalized = query.trim().toLocaleLowerCase()
    if (!normalized) return null
    const matches = new Set<string>()
    for (const resource of tree.byId.values()) {
      if (!resource.title.toLocaleLowerCase().includes(normalized)) continue
      matches.add(resource.id)
      for (const ancestor of ancestorIds(resource.id, tree)) matches.add(ancestor)
    }
    return matches
  }, [query, tree])

  const rows = useMemo(() => {
    const result: ResourceRow[] = []
    const visit = (items: ConnectorResourceSummary[], depth: number) => {
      for (const resource of items) {
        if (matchingIds && !matchingIds.has(resource.id)) continue
        const children = tree.childrenByParent.get(resource.id) || []
        result.push({ resource, depth, hasChildren: children.length > 0 })
        if (matchingIds || expanded.has(resource.id)) visit(children, depth + 1)
      }
    }
    visit(tree.roots, 0)
    return result
  }, [expanded, matchingIds, tree])

  const selectedSet = useMemo(() => new Set(selected), [selected])
  const toggleSelection = (resourceId: string, checked: boolean) => {
    if (!checked) {
      setSelected((current) => current.filter((id) => id !== resourceId))
      return
    }
    const descendants = new Set(descendantIds(resourceId, tree))
    setSelected((current) => [
      ...current.filter((id) => id !== resourceId && !descendants.has(id)),
      resourceId,
    ])
  }

  const chooseMode = (value: string) => {
    const nextMode = value as 'all_accessible' | 'selected_roots'
    setMode(nextMode)
    if (nextMode === 'selected_roots' && !resources.length && !loading)
      void onLoadResources()
  }

  const canSave = mode === 'all_accessible' || selected.length > 0

  return (
    <Dialog open={Boolean(source)} onOpenChange={onOpenChange}>
      <DialogContent className="grid max-h-[calc(100dvh-2rem)] grid-rows-[auto_auto_minmax(0,1fr)_auto] gap-0 overflow-hidden p-0 sm:max-w-2xl">
        <DialogHeader className="px-4 pt-4 pb-4 pr-10 text-left">
          <DialogTitle>{labels.connectorContentTitle}</DialogTitle>
          <DialogDescription>{labels.connectorContentDescription}</DialogDescription>
        </DialogHeader>

        <div className="border-y bg-muted/20 px-4 py-4">
          <RadioGroup
            value={mode}
            onValueChange={chooseMode}
            className="grid gap-2 sm:grid-cols-2"
          >
            <Label
              htmlFor="scope-all-accessible"
              className="items-start gap-3 rounded-lg border bg-background p-3 whitespace-normal has-[[data-state=checked]]:border-primary"
            >
              <RadioGroupItem id="scope-all-accessible" value="all_accessible" className="mt-0.5" />
              <span>
                <strong className="block text-sm">{labels.allAccessibleContent}</strong>
                <span className="block text-xs font-normal text-muted-foreground">
                  {labels.allAccessibleContentDescription}
                </span>
              </span>
            </Label>
            <Label
              htmlFor="scope-selected-roots"
              className="items-start gap-3 rounded-lg border bg-background p-3 whitespace-normal has-[[data-state=checked]]:border-primary"
            >
              <RadioGroupItem id="scope-selected-roots" value="selected_roots" className="mt-0.5" />
              <span>
                <strong className="block text-sm">{labels.specificContentAreas}</strong>
                <span className="block text-xs font-normal text-muted-foreground">
                  {labels.specificContentAreasDescription}
                </span>
              </span>
            </Label>
          </RadioGroup>
        </div>

        {mode === 'all_accessible' ? (
          <div className="flex min-h-0 items-start justify-center overflow-y-auto px-4 py-4">
            <div className="flex max-w-lg gap-3 rounded-lg border bg-muted/20 p-4">
              <CheckCircle className="mt-0.5 size-5 shrink-0 text-primary" weight="fill" aria-hidden="true" />
              <div className="flex flex-col gap-1">
                <strong className="text-sm">{labels.everythingAccessible}</strong>
                <span className="text-sm text-muted-foreground">{labels.uncheckedContentExcluded}</span>
              </div>
            </div>
          </div>
        ) : (
          <div className="flex min-h-0 min-w-0 flex-col overflow-y-auto">
            <div className="flex flex-col gap-3 border-b px-4 py-3">
              <div className="flex flex-col gap-1">
                <strong className="text-sm">{labels.selectedPages}: {selected.length}</strong>
                <span className="text-xs text-muted-foreground">
                  {labels.includeDescendantsDescription}
                </span>
              </div>
              <div className="flex flex-col gap-2 sm:flex-row">
                <Input
                  className="flex-1"
                  type="search"
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  placeholder={labels.searchPages}
                  aria-label={labels.searchPages}
                />
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={loading || tree.roots.length === 0}
                  onClick={() => setSelected(tree.roots.map((resource) => resource.id))}
                >
                  {labels.selectAllPages}
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant="ghost"
                  disabled={loading || selected.length === 0}
                  onClick={() => setSelected([])}
                >
                  {labels.clearPageSelection}
                </Button>
              </div>
            </div>

            <div data-slot="connector-resource-list" className="min-h-0 overflow-y-auto overscroll-contain">
              {loading && resources.length === 0 ? (
                <div className="flex min-h-40 items-center justify-center gap-2 text-sm text-muted-foreground">
                  <Spinner aria-hidden="true" />
                  {labels.checkingSource}
                </div>
              ) : rows.length ? (
                <div className="divide-y">
                  {rows.map(({ resource, depth, hasChildren }) => {
                    const id = `connector-resource-${resource.id}`
                    const ancestors = ancestorIds(resource.id, tree)
                    const includedThroughParent = ancestors.some((ancestor) => selectedSet.has(ancestor))
                    const hasSelectedChild = descendantIds(resource.id, tree)
                      .some((descendant) => selectedSet.has(descendant))
                    const checked = includedThroughParent || selectedSet.has(resource.id)
                      ? true
                      : hasSelectedChild
                        ? 'indeterminate'
                        : false
                    const isExpanded = expanded.has(resource.id)
                    return (
                      <div
                        className="flex min-h-14 min-w-0 items-center gap-2 py-2 pr-4"
                        style={{ paddingLeft: `${16 + Math.min(depth, 4) * 16}px` }}
                        key={resource.id}
                      >
                        <Button
                          type="button"
                          size="icon-xs"
                          variant="ghost"
                          className={hasChildren ? '' : 'invisible'}
                          aria-label={resource.title}
                          aria-expanded={hasChildren ? isExpanded : undefined}
                          onClick={() => setExpanded((current) => {
                            const next = new Set(current)
                            if (next.has(resource.id)) next.delete(resource.id)
                            else next.add(resource.id)
                            return next
                          })}
                        >
                          {isExpanded ? <CaretDown /> : <CaretRight />}
                        </Button>
                        <Checkbox
                          id={id}
                          checked={checked}
                          disabled={includedThroughParent}
                          onCheckedChange={(value) => toggleSelection(resource.id, value === true)}
                        />
                        <Label htmlFor={id} className="min-w-0 flex-1 items-center gap-2 whitespace-normal">
                          {resource.kind === 'database'
                            ? <Database className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                            : <FileText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />}
                          <span className="min-w-0">
                            <strong className="block truncate">{resource.title}</strong>
                            <span className="block text-xs font-normal text-muted-foreground">
                              {resource.kind.replace(/_/g, ' ')}
                              {resource.kind === 'database'
                                ? ` · ${labels.includesDatabaseRows}`
                                : ` · ${labels.includesDescendants}`}
                              {includedThroughParent ? ` · ${labels.includedThroughParent}` : ''}
                            </span>
                          </span>
                        </Label>
                      </div>
                    )
                  })}
                </div>
              ) : (
                <div className="flex min-h-40 flex-col items-center justify-center gap-3 p-4 text-center">
                  <p className="m-0 text-sm text-muted-foreground">{labels.noConnectorResources}</p>
                  {!query ? (
                    <Button type="button" size="sm" variant="outline" onClick={onLoadResources}>
                      {labels.retryConnectorResources}
                    </Button>
                  ) : null}
                </div>
              )}
            </div>
          </div>
        )}

        <DialogFooter className="border-t px-4 py-4">
          <DialogClose asChild>
            <Button type="button" variant="outline">{labels.cancel}</Button>
          </DialogClose>
          <Button
            type="button"
            disabled={loading || saving || !canSave}
            onClick={async () => {
              const saved = await onSave({
                mode,
                includedRootIds: mode === 'selected_roots' ? selected : [],
                excludedIds: [],
                includeDescendants: true,
                includeNewContent: true,
              })
              if (saved) onOpenChange(false)
            }}
          >
            {saving ? <Spinner data-icon="inline-start" aria-hidden="true" /> : null}
            {labels.saveAndSync}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
