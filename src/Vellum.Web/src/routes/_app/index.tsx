import { createFileRoute } from '@tanstack/react-router'

export const Route = createFileRoute('/_app/')({
  component: WorkspacePicker,
})

function WorkspacePicker() {
  return (
    <div className="flex min-h-screen items-center justify-center">
      <div className="rounded-lg border border-border bg-card p-8">
        <h1 className="mb-2 font-display text-2xl font-bold">Workspaces</h1>
        <p className="text-sm text-muted-foreground">Workspace/project picker wired in Task 6</p>
      </div>
    </div>
  )
}
