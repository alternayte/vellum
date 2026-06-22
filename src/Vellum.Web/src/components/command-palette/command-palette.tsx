import { Command } from 'cmdk'
import { useShellStore } from '@/stores/shell-store'
import { useCanvasStore } from '@/stores/canvas-store'
import { kindColor } from '@/lib/kind-colors'

interface Element {
  id: string
  kind: string
  name: string
}

interface CommandPaletteProps {
  elements: Element[]
  onAddElement?: () => void
  onTidy?: () => void
  onZoomToFit?: () => void
}

export function CommandPalette({ elements, onAddElement, onTidy, onZoomToFit }: CommandPaletteProps) {
  const { commandPaletteOpen, closeCommandPalette, selectElement } = useShellStore()
  const { toggleLens, drillTo } = useCanvasStore()

  if (!commandPaletteOpen) return null

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center pt-[20vh]">
      <div className="fixed inset-0 bg-background/80" onClick={closeCommandPalette} />
      <Command
        className="relative w-full max-w-lg rounded-lg border border-border bg-card shadow-2xl"
        onKeyDown={(e) => {
          if (e.key === 'Escape') closeCommandPalette()
        }}
      >
        <Command.Input
          placeholder="Search elements, actions..."
          className="w-full border-b border-border bg-transparent px-4 py-3 text-sm outline-none placeholder:text-muted-foreground"
          autoFocus
        />
        <Command.List className="max-h-80 overflow-y-auto p-2">
          <Command.Empty className="py-4 text-center text-sm text-muted-foreground">
            No results found.
          </Command.Empty>

          <Command.Group heading="Elements" className="text-xs text-muted-foreground">
            {elements.map((el) => (
              <Command.Item
                key={el.id}
                value={el.name}
                onSelect={() => {
                  selectElement(el.id)
                  closeCommandPalette()
                }}
                className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
              >
                <span
                  className="h-2 w-2 rounded-full"
                  style={{ backgroundColor: kindColor(el.kind) }}
                />
                {el.name}
                <span className="ml-auto font-mono text-[10px] text-muted-foreground">
                  {el.kind}
                </span>
              </Command.Item>
            ))}
          </Command.Group>

          <Command.Separator className="my-1 h-px bg-border" />

          <Command.Group heading="Actions" className="text-xs text-muted-foreground">
            <Command.Item
              onSelect={() => { onAddElement?.(); closeCommandPalette() }}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
            >
              Add element
              <kbd className="ml-auto rounded border border-border bg-muted px-1 font-mono text-[10px]">N</kbd>
            </Command.Item>
            <Command.Item
              onSelect={() => { toggleLens('status'); closeCommandPalette() }}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
            >
              Toggle status lens
              <kbd className="ml-auto rounded border border-border bg-muted px-1 font-mono text-[10px]">1</kbd>
            </Command.Item>
            <Command.Item
              onSelect={() => { toggleLens('metadata'); closeCommandPalette() }}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
            >
              Toggle metadata lens
              <kbd className="ml-auto rounded border border-border bg-muted px-1 font-mono text-[10px]">2</kbd>
            </Command.Item>
            <Command.Item
              onSelect={() => { onTidy?.(); closeCommandPalette() }}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
            >
              Tidy layout
              <kbd className="ml-auto rounded border border-border bg-muted px-1 font-mono text-[10px]">T</kbd>
            </Command.Item>
            <Command.Item
              onSelect={() => { onZoomToFit?.(); closeCommandPalette() }}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
            >
              Zoom to fit
              <kbd className="ml-auto rounded border border-border bg-muted px-1 font-mono text-[10px]">⌘0</kbd>
            </Command.Item>
            <Command.Item
              onSelect={() => { drillTo(-1); closeCommandPalette() }}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
            >
              Go to top level
            </Command.Item>
          </Command.Group>
        </Command.List>
      </Command>
    </div>
  )
}
