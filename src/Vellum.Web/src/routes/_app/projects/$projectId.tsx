import { createFileRoute } from '@tanstack/react-router'
import { TopBar } from '@/components/shell/top-bar'
import { Navigator } from '@/components/shell/navigator'
import { DetailPanel } from '@/components/shell/detail-panel'
import { LensBar } from '@/components/shell/lens-bar'

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
        <main className="flex flex-1 items-center justify-center text-muted-foreground">
          Canvas (Task 4)
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
