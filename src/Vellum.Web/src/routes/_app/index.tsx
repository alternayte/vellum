import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiWorkspaces,
  getApiWorkspacesByWorkspaceIdProjects,
  postApiWorkspaces,
  postApiWorkspacesByWorkspaceIdProjects,
} from '@/api/generated'
import '@/api/client'
import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from '@/components/ui/dialog'
import { useAuth } from '@/hooks/use-auth'
import { MyInvitations } from '@/components/settings/my-invitations'
import { pendingInvitation } from '@/lib/invitation-link'
import { branchesApi } from '@/lib/branches-api'
import { ProjectMenu } from '@/components/shell/project-menu'
import { InboxButton } from '@/components/comments/inbox-button'

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
  const queryClient = useQueryClient()
  const { logout } = useAuth()
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<string | null>(null)
  const [showCreateWorkspace, setShowCreateWorkspace] = useState(false)
  const [showCreateProject, setShowCreateProject] = useState(false)
  const [workspaceName, setWorkspaceName] = useState('')
  const [projectName, setProjectName] = useState('')

  // A sign-in that started at an invitation link comes back here: go on to the link.
  useEffect(() => {
    const invitationId = pendingInvitation()
    if (invitationId) navigate({ to: '/invitations/$invitationId', params: { invitationId } })
  }, [navigate])

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

  // The role of the signed-in person decides which project actions the cards offer.
  const { data: members } = useQuery({
    queryKey: ['workspaces', selectedWorkspaceId, 'members'],
    queryFn: () => branchesApi.members(selectedWorkspaceId!),
    enabled: !!selectedWorkspaceId,
  })

  const createWorkspace = useMutation({
    mutationFn: async (name: string) => {
      const id = crypto.randomUUID()
      await postApiWorkspaces({ body: { id, name } })
      return id
    },
    onSuccess: (id) => {
      queryClient.invalidateQueries({ queryKey: ['workspaces'] })
      setSelectedWorkspaceId(id)
      setShowCreateWorkspace(false)
      setWorkspaceName('')
    },
  })

  const createProject = useMutation({
    mutationFn: async (name: string) => {
      if (!selectedWorkspaceId) return
      const id = crypto.randomUUID()
      await postApiWorkspacesByWorkspaceIdProjects({
        path: { workspaceId: selectedWorkspaceId },
        body: { id, name, description: null },
      })
      return id
    },
    onSuccess: (id) => {
      queryClient.invalidateQueries({ queryKey: ['workspaces', selectedWorkspaceId, 'projects'] })
      setShowCreateProject(false)
      setProjectName('')
      if (id) navigate({ to: '/projects/$projectId', params: { projectId: id } })
    },
  })

  const activeWorkspace = workspaces?.find((w) => w.id === selectedWorkspaceId)

  return (
    <div className="flex min-h-screen flex-col bg-background">
      <header className="flex h-12 items-center justify-between border-b border-border bg-card px-4">
        <span className="font-display text-sm font-bold text-primary">Vellum</span>
        <span className="flex items-center gap-1">
        <InboxButton />
        <Button
          variant="ghost"
          size="sm"
          className="text-xs text-muted-foreground"
          onClick={async () => {
            await logout()
            navigate({ to: '/login' })
          }}
        >
          Log out
        </Button>
        </span>
      </header>

      <div className="flex flex-1 items-center justify-center">
        <div className="w-full max-w-md rounded-lg border border-border bg-card p-8">
          <MyInvitations />
          <h1 className="mb-6 font-display text-2xl font-bold">Workspaces</h1>

          {workspacesLoading ? (
            <p className="text-sm text-muted-foreground">Loading workspaces…</p>
          ) : !workspaces?.length ? (
            <div className="space-y-3">
              <p className="text-sm text-muted-foreground">
                No workspaces found. Create one to get started.
              </p>
              <Button onClick={() => setShowCreateWorkspace(true)}>Create Workspace</Button>
            </div>
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
              <Button
                variant="outline"
                size="sm"
                className="mt-2 w-full"
                onClick={() => setShowCreateWorkspace(true)}
              >
                + New Workspace
              </Button>
            </div>
          )}

          {selectedWorkspaceId && (
            <div className="mt-6">
              <div className="mb-3 flex items-center justify-between">
                <h2 className="text-sm font-medium uppercase tracking-wider text-muted-foreground">
                  Projects in {activeWorkspace?.name}
                </h2>
                <div className="flex items-center gap-2">
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-7 text-xs"
                    onClick={() => navigate({ to: '/workspaces/$workspaceId/settings', params: { workspaceId: selectedWorkspaceId } })}
                  >
                    Settings
                  </Button>
                  <Button
                    variant="outline"
                    size="sm"
                    className="h-7 text-xs"
                    onClick={() => setShowCreateProject(true)}
                  >
                    + New Project
                  </Button>
                </div>
              </div>
              {projectsLoading ? (
                <p className="text-sm text-muted-foreground">Loading projects…</p>
              ) : !projects?.length ? (
                <p className="text-sm text-muted-foreground">No projects in this workspace yet.</p>
              ) : (
                <div className="space-y-2">
                  {projects.map((project) => (
                    <div key={project.id} className="flex items-center gap-1 rounded-lg border border-border bg-background pr-1.5 hover:bg-muted">
                      <button
                        type="button"
                        className="min-w-0 flex-1 rounded-lg px-3 py-2 text-left focus-visible:outline-2 focus-visible:outline-primary"
                        onClick={() =>
                          navigate({ to: '/projects/$projectId', params: { projectId: project.id } })
                        }
                      >
                        <span className="block truncate text-sm font-medium">{project.name}</span>
                        {project.description && (
                          <span className="block truncate text-xs text-muted-foreground">{project.description}</span>
                        )}
                      </button>
                      <ProjectMenu
                        project={{ ...project, workspaceId: selectedWorkspaceId }}
                        role={members?.yourRole}
                        onDeleted={() => {}}
                      />
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      <Dialog open={showCreateWorkspace} onOpenChange={setShowCreateWorkspace}>
        <DialogContent aria-describedby={undefined}>
          <DialogHeader>
            <DialogTitle>Create Workspace</DialogTitle>
          </DialogHeader>
          <form
            onSubmit={(e) => {
              e.preventDefault()
              if (workspaceName.trim()) createWorkspace.mutate(workspaceName.trim())
            }}
          >
            <Input
              placeholder="Workspace name"
              value={workspaceName}
              onChange={(e) => setWorkspaceName(e.target.value)}
              data-autofocus
            />
            <DialogFooter className="mt-4">
              <Button type="submit" disabled={!workspaceName.trim() || createWorkspace.isPending}>
                {createWorkspace.isPending ? 'Creating…' : 'Create'}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={showCreateProject} onOpenChange={setShowCreateProject}>
        <DialogContent aria-describedby={undefined}>
          <DialogHeader>
            <DialogTitle>Create Project</DialogTitle>
          </DialogHeader>
          <form
            onSubmit={(e) => {
              e.preventDefault()
              if (projectName.trim()) createProject.mutate(projectName.trim())
            }}
          >
            <Input
              placeholder="Project name"
              value={projectName}
              onChange={(e) => setProjectName(e.target.value)}
              data-autofocus
            />
            <DialogFooter className="mt-4">
              <Button type="submit" disabled={!projectName.trim() || createProject.isPending}>
                {createProject.isPending ? 'Creating…' : 'Create'}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  )
}
