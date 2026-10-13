import { useShellStore } from '@/stores/shell-store'
import { useDraftStore } from '@/stores/draft-store'
import { StrataBreadcrumb } from './strata-breadcrumb'
import { Button } from '@/components/ui/button'
import { DraftPill } from '@/components/draft/draft-pill'
import { useAuth } from '@/hooks/use-auth'
import { useNavigate } from '@tanstack/react-router'
import { Menu } from 'lucide-react'

interface TopBarProps {
  projectId?: string
  projectName?: string
  activeDoc?: { id: string; title: string; spaceName?: string | null } | null
}

export function TopBar({ projectId, projectName, activeDoc }: TopBarProps) {
  const { toggleCommandPalette } = useShellStore()
  const { activeDraftId } = useDraftStore()
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
    <header className="flex h-12 items-center gap-3 border-b border-border bg-card px-3">
      <button
        className="flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground"
        onClick={() => navigate({ to: '/' })}
        aria-label="Home"
      >
        <Menu className="h-4 w-4" />
      </button>

      {projectName && (
        <span className="text-sm font-medium">{projectName}</span>
      )}

      <div className="mx-2">
        <StrataBreadcrumb activeDoc={activeDoc} />
      </div>

      {activeDraftId && projectId && (
        <DraftPill projectId={projectId} />
      )}

      <div className="flex-1" />

      <Button
        variant="outline"
        size="sm"
        className="gap-2 text-xs text-muted-foreground"
        onClick={toggleCommandPalette}
      >
        <span>Search</span>
        <kbd className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-[10px]">
          ⌘K
        </kbd>
      </Button>

      <button
        className="flex h-7 w-7 items-center justify-center rounded-full bg-primary text-[10px] font-medium text-primary-foreground"
        onClick={handleLogout}
        aria-label={`Log out (signed in as ${initials})`}
        title="Log out"
      >
        {initials}
      </button>
    </header>
  )
}
