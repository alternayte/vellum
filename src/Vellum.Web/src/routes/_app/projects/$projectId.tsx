import { createFileRoute } from '@tanstack/react-router'
import { TopBar } from '@/components/shell/top-bar'
import { Navigator } from '@/components/shell/navigator'
import { DetailPanel } from '@/components/shell/detail-panel'
import { LensBar } from '@/components/shell/lens-bar'
import { CanvasView } from '@/components/canvas/canvas-view'
import { useCanvasStore } from '@/stores/canvas-store'

const MOCK_ELEMENTS = [
  { id: '1', kind: 'actor', name: 'Customer', description: 'End user', technology: null, status: 'current', parentId: null, tags: ['external'] },
  { id: '2', kind: 'system', name: 'Orders', description: 'Order lifecycle', technology: 'dotnet', status: 'current', parentId: null, tags: ['core'] },
  { id: '3', kind: 'system', name: 'Payments', description: 'Payment processing', technology: 'go', status: 'planned', parentId: null, tags: ['core'] },
  { id: '4', kind: 'app', name: 'Orders API', description: 'REST API for orders', technology: 'ASP.NET Core', status: 'current', parentId: '2', tags: [] },
  { id: '5', kind: 'store', name: 'Orders DB', description: 'PostgreSQL', technology: 'PostgreSQL', status: 'current', parentId: '2', tags: [] },
]

const MOCK_RELATIONSHIPS = [
  { id: 'r1', fromId: '1', toId: '2', label: 'Places orders', technology: 'HTTPS' },
  { id: 'r2', fromId: '2', toId: '3', label: 'Requests payment', technology: 'gRPC' },
]

const MOCK_VIEWS = [
  { id: 'v1', name: 'Context View' },
  { id: 'v2', name: 'Orders System' },
]

export const Route = createFileRoute('/_app/projects/$projectId')({
  component: ProjectWorkspace,
})

function ProjectWorkspace() {
  return (
    <div className="flex h-screen flex-col">
      <TopBar />
      <div className="flex flex-1 overflow-hidden">
        <Navigator elements={MOCK_ELEMENTS} views={MOCK_VIEWS} />
        <main className="flex-1">
          <CanvasView
            elements={MOCK_ELEMENTS.map((e) => ({ ...e, ownerId: null }))}
            relationships={MOCK_RELATIONSHIPS}
            positions={[
              { elementId: '1', x: 0, y: 200 },
              { elementId: '2', x: 400, y: 100 },
              { elementId: '3', x: 400, y: 300 },
              { elementId: '4', x: 800, y: 50 },
              { elementId: '5', x: 800, y: 200 },
            ]}
            onNodeDoubleClick={(elementId) => {
              const el = MOCK_ELEMENTS.find((e) => e.id === elementId)
              if (el) {
                const children = MOCK_ELEMENTS.filter((e) => e.parentId === elementId)
                if (children.length > 0) {
                  useCanvasStore.getState().drillInto({
                    elementId: el.id,
                    name: el.name,
                    kind: el.kind,
                  })
                }
              }
            }}
          />
        </main>
        <DetailPanel
          elements={MOCK_ELEMENTS}
          relationships={MOCK_RELATIONSHIPS}
        />
      </div>
      <LensBar />
    </div>
  )
}
