import { createFileRoute } from '@tanstack/react-router'
import { TopBar } from '@/components/shell/top-bar'
import { Navigator } from '@/components/shell/navigator'
import { DetailPanel } from '@/components/shell/detail-panel'
import { LensBar } from '@/components/shell/lens-bar'
import { CanvasView } from '@/components/canvas/canvas-view'
import { useElements, useUpdateElement } from '@/hooks/use-elements'
import { useRelationships, useUpdateRelationship } from '@/hooks/use-relationships'
import { useViews, useView, useSaveLayout } from '@/hooks/use-views'
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
  const { activeViewId } = useShellStore()
  const { data: activeView } = useView(projectId, activeViewId)
  const updateElement = useUpdateElement(projectId)
  const updateRelationship = useUpdateRelationship(projectId)
  const { saveLayout } = useSaveLayout(projectId, activeViewId)

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

  return (
    <div className="flex h-screen flex-col">
      <TopBar />
      <div className="flex flex-1 overflow-hidden">
        <Navigator elements={elements ?? []} views={views ?? []} />
        <main className="flex-1">
          <CanvasView
            elements={(elements ?? []).map((e) => ({ ...e, ownerId: e.ownerId ?? null }))}
            relationships={relationships ?? []}
            positions={positions}
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
        </main>
        <DetailPanel
          elements={elements ?? []}
          relationships={relationships ?? []}
          onUpdateElement={(id, fields) => updateElement.mutate({ id, ...fields })}
          onUpdateRelationship={(id, fields) => updateRelationship.mutate({ id, ...fields })}
        />
      </div>
      <LensBar
        onTidy={async () => {
          if (!elements || !relationships) return
          const newPositions = await computeLayout(elements, relationships)
          saveLayout(newPositions)
        }}
      />
    </div>
  )
}
