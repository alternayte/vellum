import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api } from '@/lib/settings-api'
import { branchesApi } from '@/lib/branches-api'
import { shownInline } from '@/lib/mutation-errors'
import { SectionHeading } from '@/components/settings/fields'
import { TypedDeleteDialog } from '@/components/shell/typed-delete-dialog'

interface WorkspaceDto {
  id: string
  name: string
}

interface ProjectDto {
  id: string
  name: string
}

/** The name of the workspace, and its delete in a danger area. Both are for an owner. */
export function WorkspaceGeneral({ workspaceId }: { workspaceId: string }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const workspaces = useQuery({ queryKey: ['workspaces'], queryFn: () => api<WorkspaceDto[]>('GET', '/api/workspaces') })
  const members = useQuery({ queryKey: ['workspaces', workspaceId, 'members'], queryFn: () => branchesApi.members(workspaceId) })
  const projects = useQuery({
    queryKey: ['workspaces', workspaceId, 'projects'],
    queryFn: () => api<ProjectDto[]>('GET', `/api/workspaces/${workspaceId}/projects`),
  })
  const workspace = workspaces.data?.find((w) => w.id === workspaceId)
  const isOwner = members.data?.yourRole === 'Owner'

  const [name, setName] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const shown = name ?? workspace?.name ?? ''
  const changed = name !== null && name.trim() !== '' && name.trim() !== workspace?.name

  const rename = useMutation({
    mutationFn: (next: string) => api('PATCH', `/api/workspaces/${workspaceId}`, { name: next }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workspaces'], exact: true })
      setName(null)
    },
    onError: shownInline,
  })
  const remove = useMutation({
    mutationFn: () => api('DELETE', `/api/workspaces/${workspaceId}`),
    onSuccess: () => {
      queryClient.removeQueries({ queryKey: ['workspaces', workspaceId] })
      queryClient.invalidateQueries({ queryKey: ['workspaces'], exact: true })
      navigate({ to: '/' })
    },
    onError: shownInline,
  })

  if (!workspace) {
    return <p className="text-sm text-muted-foreground">{workspaces.isError ? workspaces.error.message : 'Loading…'}</p>
  }

  const projectList = projects.data ?? []
  return (
    <>
      <SectionHeading title="General" description="The name of the workspace shows in the workspace list of every member." />
      <form
        className="flex items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          if (changed) rename.mutate(shown.trim())
        }}
      >
        <label className="block min-w-0 flex-1 text-sm font-medium">
          Name
          <Input className="mt-1 font-normal" value={shown} disabled={!isOwner} onChange={(e) => setName(e.target.value)} />
        </label>
        {isOwner && (
          <Button type="submit" disabled={!changed || rename.isPending}>
            {rename.isPending ? 'Saving…' : 'Rename'}
          </Button>
        )}
      </form>
      {rename.isError && <p role="alert" className="mt-2 text-xs text-destructive">{rename.error.message}</p>}
      {!isOwner && members.data && <p className="mt-2 text-xs text-muted-foreground">Only an owner renames or deletes the workspace.</p>}

      {isOwner && (
        <section aria-labelledby="danger-area" className="mt-10 rounded-lg border border-destructive/50 p-4">
          <h3 id="danger-area" className="text-sm font-medium text-destructive">Danger area</h3>
          <div className="mt-2 flex flex-wrap items-center gap-3">
            <p className="min-w-0 flex-1 text-xs text-muted-foreground">
              Deletes the workspace, its {projectList.length === 1 ? 'project' : `${projectList.length} projects`} and all their data. This cannot be undone.
            </p>
            <Button variant="destructive" size="sm" className="gap-1.5" onClick={() => { remove.reset(); setDeleting(true) }}>
              <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> Delete workspace…
            </Button>
          </div>
        </section>
      )}

      {deleting && (
        <TypedDeleteDialog
          noun="workspace"
          name={workspace.name}
          items={[
            projectList.length === 0
              ? 'No projects'
              : projectList.length === 1
                ? `The project ${projectList[0].name}, with its model, diagrams, documents, promises, cost data, reviews, stress runs, load tests, imports and AI proposals`
                : `${projectList.length} projects (${projectList.map((p) => p.name).join(', ')}), with their models, diagrams, documents, promises, cost data, reviews, stress runs, load tests, imports and AI proposals`,
            'Members and open invitations',
            'Settings, AI providers and stored credentials',
            'The price book',
            'Agent tokens',
            'AI usage records',
            'Cached repository files and scan charges',
          ]}
          note="The audit log keeps its entries, and the instance audit log records the delete."
          pending={remove.isPending}
          error={remove.error?.message}
          onConfirm={() => remove.mutate()}
          onClose={() => setDeleting(false)}
        />
      )}
    </>
  )
}
