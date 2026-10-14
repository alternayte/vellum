import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Pencil, Text, Trash2 } from 'lucide-react'
import { ActionMenu, type ActionMenuItem } from '@/components/ui/action-menu'
import { api } from '@/lib/settings-api'
import { shownInline } from '@/lib/mutation-errors'
import { TextPromptDialog } from './text-prompt-dialog'
import { TypedDeleteDialog } from './typed-delete-dialog'

export interface ProjectSummary {
  id: string
  workspaceId: string
  name: string
  description: string | null
}

/** What a project delete removes, one line for each module (DeleteProject on the server). */
const PROJECT_DATA = [
  'The model of main, its revisions, variants and drafts, with their comments',
  'Diagrams and their layouts',
  'Documents and spaces',
  'Schemas, contracts and scenarios',
  'Promises, their runs and the impact gate',
  'Cost sheets, saved estimates and price pins',
  'Review decisions, questions, dispositions, snapshots and critiques',
  'Stress profiles and runs',
  'Load-test targets, scripts and results',
  'Imports: scans, snapshots, proposals and links to source',
  'AI proposals',
  'Agent tokens restricted to this project',
]

type Open = 'rename' | 'description' | 'delete' | null

interface ProjectMenuProps {
  project: ProjectSummary
  /** The role of the signed-in person in the workspace. Rename needs Editor; delete needs Owner. */
  role: string | undefined
  onDeleted: () => void
  className?: string
}

/** The "⋯" menu of a project: Rename, Edit description and, for an owner, Delete project. */
export function ProjectMenu({ project, role, onDeleted, className }: ProjectMenuProps) {
  const queryClient = useQueryClient()
  const [open, setOpen] = useState<Open>(null)

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['workspaces', project.workspaceId, 'projects'] })
    queryClient.invalidateQueries({ queryKey: ['projects', project.id, 'detail'] })
  }
  const update = useMutation({
    mutationFn: (body: { name?: string; description?: string }) =>
      api('PATCH', `/api/projects/${project.id}`, { name: body.name ?? null, description: body.description ?? null }),
    onSuccess: () => { refresh(); setOpen(null) },
    onError: shownInline,
  })
  const remove = useMutation({
    mutationFn: () => api('DELETE', `/api/projects/${project.id}`),
    onSuccess: () => {
      queryClient.removeQueries({ queryKey: ['projects', project.id] })
      queryClient.invalidateQueries({ queryKey: ['workspaces', project.workspaceId, 'projects'] })
      setOpen(null)
      onDeleted()
    },
    onError: shownInline,
  })

  const canEdit = role === 'Owner' || role === 'Editor'
  const items: ActionMenuItem[] = []
  if (canEdit) {
    items.push({ label: 'Rename', icon: <Pencil className="h-3.5 w-3.5" aria-hidden="true" />, onSelect: () => { update.reset(); setOpen('rename') } })
    items.push({ label: 'Edit description', icon: <Text className="h-3.5 w-3.5" aria-hidden="true" />, onSelect: () => { update.reset(); setOpen('description') } })
  }
  if (role === 'Owner') {
    items.push({ label: 'Delete project…', icon: <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />, destructive: true, onSelect: () => { remove.reset(); setOpen('delete') } })
  }

  return (
    <>
      <ActionMenu label={`Actions for project ${project.name}`} items={items} className={className} />
      {open === 'rename' && (
        <TextPromptDialog
          title="Rename project"
          label="Name"
          initial={project.name}
          submitLabel="Rename"
          pending={update.isPending}
          error={update.error?.message}
          onSubmit={(name) => update.mutate({ name })}
          onClose={() => setOpen(null)}
        />
      )}
      {open === 'description' && (
        <TextPromptDialog
          title="Edit description"
          label="Description"
          initial={project.description ?? ''}
          submitLabel="Save"
          multiline
          pending={update.isPending}
          error={update.error?.message}
          onSubmit={(description) => update.mutate({ description })}
          onClose={() => setOpen(null)}
        />
      )}
      {open === 'delete' && (
        <TypedDeleteDialog
          noun="project"
          name={project.name}
          items={PROJECT_DATA}
          note="The event streams of the project are deleted too. The audit log of the workspace keeps its entries."
          pending={remove.isPending}
          error={remove.error?.message}
          onConfirm={() => remove.mutate()}
          onClose={() => setOpen(null)}
        />
      )}
    </>
  )
}
