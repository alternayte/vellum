import { useShellStore } from '@/stores/shell-store'
import { StrataBreadcrumb } from './strata-breadcrumb'
import { Button } from '@/components/ui/button'

export function TopBar() {
  const { toggleCommandPalette } = useShellStore()

  return (
    <header className="flex h-12 items-center gap-3 border-b border-border bg-card px-4">
      <div className="flex items-center gap-2">
        <span className="font-display text-sm font-bold text-primary">Vellum</span>
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
        <StrataBreadcrumb />
      </div>

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
    </header>
  )
}
