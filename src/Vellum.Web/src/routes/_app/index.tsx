import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { getApiWorkspaces, getApiWorkspacesByWorkspaceIdProjects } from '@/api/generated'
import '@/api/client'
import { useState } from 'react'
import { Button } from '@/components/ui/button'

interface Workspace {
  id: string
  name: string
}

interface Project {
  id: string
  name: string
  description: string | null
}

export const Route = createFileRoute('/_app/')({
  component: WorkspacePicker,
})

function WorkspacePicker() {
  const navigate = useNavigate()
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<string | null>(null)

  const { data: workspaces, isLoading: workspacesLoading } = useQuery({
    queryKey: ['workspaces'],
    queryFn: async () => {
      const result = await getApiWorkspaces()
      return result.data as Workspace[]
    },
  })

  const { data: projects, isLoading: projectsLoading } = useQuery({
    queryKey: ['workspaces', selectedWorkspaceId, 'projects'],
    queryFn: async () => {
      if (!selectedWorkspaceId) return []
      const result = await getApiWorkspacesByWorkspaceIdProjects({
        path: { workspaceId: selectedWorkspaceId },
      })
      return result.data as Project[]
    },
    enabled: !!selectedWorkspaceId,
  })

  const activeWorkspace = workspaces?.find((w) => w.id === selectedWorkspaceId)

  return (
    <div className="flex min-h-screen items-center justify-center bg-background">
      <div className="w-full max-w-md rounded-lg border border-border bg-card p-8">
        <h1 className="mb-6 font-display text-2xl font-bold">Workspaces</h1>

        {workspacesLoading ? (
          <p className="text-sm text-muted-foreground">Loading workspaces…</p>
        ) : !workspaces?.length ? (
          <p className="text-sm text-muted-foreground">
            No workspaces found. Create one to get started.
          </p>
        ) : (
          <div className="space-y-2">
            {workspaces.map((workspace) => (
              <button
                key={workspace.id}
                onClick={() => setSelectedWorkspaceId(workspace.id)}
                className={`w-full rounded-md border px-4 py-3 text-left text-sm transition-colors ${
                  selectedWorkspaceId === workspace.id
                    ? 'border-primary bg-primary/10 text-foreground'
                    : 'border-border bg-background hover:bg-muted'
                }`}
              >
                {workspace.name}
              </button>
            ))}
          </div>
        )}

        {selectedWorkspaceId && (
          <div className="mt-6">
            <h2 className="mb-3 text-sm font-medium text-muted-foreground uppercase tracking-wider">
              Projects in {activeWorkspace?.name}
            </h2>
            {projectsLoading ? (
              <p className="text-sm text-muted-foreground">Loading projects…</p>
            ) : !projects?.length ? (
              <p className="text-sm text-muted-foreground">No projects in this workspace yet.</p>
            ) : (
              <div className="space-y-2">
                {projects.map((project) => (
                  <Button
                    key={project.id}
                    variant="outline"
                    className="w-full justify-start"
                    onClick={() => navigate({ to: '/projects/$projectId', params: { projectId: project.id } })}
                  >
                    {project.name}
                  </Button>
                ))}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  )
}
