import { createFileRoute } from '@tanstack/react-router'

export const Route = createFileRoute('/_app/projects/$projectId')({
  component: ProjectWorkspace,
})

function ProjectWorkspace() {
  const { projectId } = Route.useParams()
  return (
    <div className="flex flex-1 flex-col">
      <div className="flex items-center border-b border-border bg-card px-4 py-2">
        <span className="font-display text-sm font-medium">Project: {projectId}</span>
      </div>
      <div className="flex flex-1 items-center justify-center text-muted-foreground">
        Canvas wired in Task 4
      </div>
    </div>
  )
}
