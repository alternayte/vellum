import { useShellStore } from '@/stores/shell-store'
import { useDraftStore } from '@/stores/draft-store'
import { StrataBreadcrumb } from './strata-breadcrumb'
import { Button } from '@/components/ui/button'
import { DraftPill } from '@/components/draft/draft-pill'
import { useAuth } from '@/hooks/use-auth'
import { useNavigate } from '@tanstack/react-router'

interface TopBarProps {
  projectId?: string
  activeDoc?: { id: string; title: string; spaceName?: string | null } | null
}

export function TopBar({ projectId, activeDoc }: TopBarProps = {}) {
  const { toggleCommandPalette } = useShellStore()
  const { activeDraftId } = useDraftStore()
  const { logout } = useAuth()
  const navigate = useNavigate()

  const handleLogout = async () => {
    await logout()
    navigate({ to: '/login' })
  }

  return (
    <header className="flex h-12 items-center gap-3 border-b border-border bg-card px-4">
      <div className="flex items-center gap-2">
        <button
          className="font-display text-sm font-bold text-primary hover:text-primary/80"
          onClick={() => navigate({ to: '/' })}
        >
          Vellum
        </button>
        <span className="text-muted-foreground">·</span>
        <button className="text-sm text-muted-foreground hover:text-foreground">
          Workspace ▾
        </button>
        <span className="text-muted-foreground">/</span>
        <button className="text-sm text-muted-foreground hover:text-foreground">
          Project ▾
        </button>
      </div>

      <div className="mx-4">
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

      <Button
        variant="ghost"
        size="sm"
        className="text-xs text-muted-foreground"
        onClick={handleLogout}
      >
        Log out
      </Button>
    </header>
  )
}
