import { useShellStore } from '@/stores/shell-store'
import { StrataBreadcrumb } from './strata-breadcrumb'
import { Button } from '@/components/ui/button'
import { BranchSwitcher } from '@/components/branch/branch-switcher'
import type { ProjectDetail } from '@/lib/branches-api'
import { useAuth } from '@/hooks/use-auth'
import { useNavigate } from '@tanstack/react-router'
import { Menu } from 'lucide-react'
import { ProjectMenu } from './project-menu'
import { ShareButton } from '@/components/share/share-dialog'

interface TopBarProps {
  projectId?: string
  projectName?: string
  /** The project with the role of the signed-in person. With it the bar shows the model switcher. */
  project?: ProjectDetail
  activeDoc?: { id: string; title: string; spaceName?: string | null } | null
}

export function TopBar({ projectId, projectName, project, activeDoc }: TopBarProps) {
  const { toggleCommandPalette } = useShellStore()
  const { logout, user } = useAuth()
  const navigate = useNavigate()

  const handleLogout = async () => {
    await logout()
    navigate({ to: '/login' })
  }

  const initials = user?.displayName
    ? user.displayName
        .split(' ')
        .map((w: string) => w[0])
        .join('')
        .toUpperCase()
        .slice(0, 2)
    : '?'

  return (
    <header className="flex h-12 items-center gap-2 border-b border-border bg-card px-3 sm:gap-3">
      <button
        className="flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground"
        onClick={() => navigate({ to: '/' })}
        aria-label="Home"
      >
        <Menu className="h-4 w-4" />
      </button>

      {projectName && (
        <span className="flex min-w-0 items-center gap-0.5 max-sm:hidden">
          <span className="min-w-0 truncate text-sm font-medium">{projectName}</span>
          {project && (
            <ProjectMenu project={project} role={project.yourRole} onDeleted={() => navigate({ to: '/' })} />
          )}
        </span>
      )}

      {projectId && project && (
        <BranchSwitcher projectId={projectId} workspaceId={project.workspaceId} role={project.yourRole} />
      )}
      {project?.yourRole === 'Viewer' && (
        <span className="shrink-0 rounded-full border border-border px-2 py-0.5 text-[11px] text-muted-foreground" title="Your role in this workspace is Viewer. You can read the project and you cannot change it.">
          Viewer<span className="max-sm:hidden"> · read-only</span>
        </span>
      )}

      {/* The breadcrumb takes the room that is left. A long path scrolls in its own box and pushes nothing off the bar. */}
      <div className="min-w-0 flex-1 overflow-x-auto whitespace-nowrap sm:mx-2">
        <StrataBreadcrumb activeDoc={activeDoc} />
      </div>

      {projectId && project && <ShareButton projectId={projectId} workspaceId={project.workspaceId} role={project.yourRole} />}

      <Button
        variant="outline"
        size="sm"
        className="gap-2 text-xs text-muted-foreground focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-primary"
        onClick={toggleCommandPalette}
      >
        <span>Search</span>
        <kbd className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-[10px] max-sm:hidden">
          ⌘K
        </kbd>
      </Button>

      <button
        className="flex h-7 w-7 items-center justify-center rounded-full bg-primary text-[10px] font-medium text-primary-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        onClick={handleLogout}
        aria-label={`Log out (signed in as ${initials})`}
        title="Log out"
      >
        {initials}
      </button>
    </header>
  )
}
