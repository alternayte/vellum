import { useCallback, useEffect, useRef } from 'react'
import { createFileRoute } from '@tanstack/react-router'
import { TopBar } from '@/components/shell/top-bar'
import { Navigator } from '@/components/shell/navigator'
import { DetailPanel } from '@/components/shell/detail-panel'
import { LensBar } from '@/components/shell/lens-bar'
import { CanvasView } from '@/components/canvas/canvas-view'
import { DocEditor } from '@/components/docs/doc-editor'
import { CommandPalette } from '@/components/command-palette/command-palette'
import { useElements, useUpdateElement } from '@/hooks/use-elements'
import { useRelationships, useUpdateRelationship } from '@/hooks/use-relationships'
import { useViews, useView, useSaveLayout } from '@/hooks/use-views'
import { useDocs } from '@/hooks/use-docs'
import { useSpaces } from '@/hooks/use-spaces'
import { useShellStore } from '@/stores/shell-store'
import { useCanvasStore } from '@/stores/canvas-store'
import { computeLayout } from '@/lib/layout'
import { Skeleton } from '@/components/ui/skeleton'

export const Route = createFileRoute('/_app/projects/$projectId')({
  component: ProjectWorkspace,
})

function ProjectWorkspace() {
  const { projectId } = Route.useParams()
  const { data: elements, isLoading: elementsLoading } = useElements(projectId)
  const { data: relationships, isLoading: relsLoading } = useRelationships(projectId)
  const { data: views } = useViews(projectId)
  const { data: docs } = useDocs(projectId)
  const { data: spaces } = useSpaces(projectId)
  const { activeViewId, activeDocId } = useShellStore()
  const { data: activeView } = useView(projectId, activeViewId)
  const updateElement = useUpdateElement(projectId)
  const updateRelationship = useUpdateRelationship(projectId)
  const { saveLayout } = useSaveLayout(projectId, activeViewId)

  const fitViewRef = useRef<(() => void) | null>(null)
  const handleFitViewReady = useCallback((fn: () => void) => {
    fitViewRef.current = fn
  }, [])

  const handleTidy = async () => {
    if (!elements || !relationships) return
    const newPositions = await computeLayout(elements, relationships)
    saveLayout(newPositions)
  }

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const meta = e.metaKey || e.ctrlKey
      const target = e.target as HTMLElement | null
      const inInput = target?.closest?.('input, textarea, select, [contenteditable="true"]')

      if (meta && e.key === 'k') {
        e.preventDefault()
        useShellStore.getState().toggleCommandPalette()
      }
      if (e.key === '1' && !inInput) {
        useCanvasStore.getState().toggleLens('status')
      }
      if (e.key === '2' && !inInput) {
        useCanvasStore.getState().toggleLens('metadata')
      }
      if (e.key === 't' && !inInput) {
        handleTidy()
      }
      if (e.key === 'Escape') {
        useShellStore.getState().closeCommandPalette()
        useShellStore.getState().selectElement(null)
      }
    }

    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [elements, relationships])

  if (elementsLoading || relsLoading) {
    return (
      <div className="flex h-screen flex-col">
        <TopBar />
        <div className="flex flex-1 items-center justify-center">
          <Skeleton className="h-8 w-48" />
        </div>
      </div>
    )
  }

  const positions = activeView?.positions ?? []

  const activeDocData = activeDocId ? (docs ?? []).find((d) => d.id === activeDocId) : null
  const activeDocSpaceName = activeDocData?.spaceId
    ? (spaces ?? []).find((s) => s.id === activeDocData.spaceId)?.name ?? null
    : null
  const activeDocForBreadcrumb = activeDocData
    ? { id: activeDocData.id, title: activeDocData.title, spaceName: activeDocSpaceName }
    : null

  return (
    <div className="flex h-screen flex-col">
      <TopBar activeDoc={activeDocForBreadcrumb} />
      <div className="flex flex-1 overflow-hidden">
        <Navigator
          elements={elements ?? []}
          views={views ?? []}
          spaces={spaces ?? []}
          docs={(docs ?? []).map((d) => ({ id: d.id, title: d.title, spaceId: d.spaceId }))}
        />
        <main className="flex-1">
          {activeDocId ? (
            <DocEditor projectId={projectId} docId={activeDocId} />
          ) : (
            <CanvasView
              elements={(elements ?? []).map((e) => ({ ...e, ownerId: e.ownerId ?? null }))}
              relationships={relationships ?? []}
              positions={positions}
              onFitViewReady={handleFitViewReady}
              onNodeDoubleClick={(elementId) => {
                const el = elements?.find((e) => e.id === elementId)
                if (!el) return
                const children = elements?.filter((e) => e.parentId === elementId)
                if (children && children.length > 0) {
                  useCanvasStore.getState().drillInto({
                    elementId: el.id,
                    name: el.name,
                    kind: el.kind,
                  })
                }
              }}
            />
          )}
        </main>
        <DetailPanel
          elements={elements ?? []}
          relationships={relationships ?? []}
          onUpdateElement={(id, fields) => updateElement.mutate({ id, ...fields })}
          onUpdateRelationship={(id, fields) => updateRelationship.mutate({ id, ...fields })}
        />
      </div>
      <LensBar onTidy={handleTidy} />
      <CommandPalette
        elements={elements ?? []}
        onTidy={handleTidy}
        onZoomToFit={() => fitViewRef.current?.()}
      />
    </div>
  )
}
