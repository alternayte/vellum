import { Command } from 'cmdk'
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog'
import { useShellStore } from '@/stores/shell-store'
import { useCanvasStore } from '@/stores/canvas-store'
import { kindColor } from '@/lib/kind-colors'
import { factStatusColor, factStatusLabel } from '@/lib/fact-status'
import { visibleAreas, type AreaAccess } from '@/lib/areas'

interface Element {
  id: string
  kind: string
  name: string
  technology?: string | null
  factStatus?: string | null
}

interface CommandPaletteProps {
  elements: Element[]
  /** Which sidebar areas the person sees. The palette opens the same tools. */
  access: AreaAccess
  onAddElement?: () => void
  onTidy?: () => void
  onZoomToFit?: () => void
}

const itemClass = 'flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted'

export function CommandPalette({ elements, access, onAddElement, onTidy, onZoomToFit }: CommandPaletteProps) {
  const { commandPaletteOpen, closeCommandPalette, selectElement, openFlyout, openComments } = useShellStore()
  const { toggleLens, drillTo } = useCanvasStore()

  // The dialog primitive holds the focus inside the palette, gives it back to the control
  // that had it, and closes on Escape as the top layer only.
  return (
    <Dialog open={commandPaletteOpen} onOpenChange={(open) => { if (!open) closeCommandPalette() }}>
      <DialogContent
        showCloseButton={false}
        aria-describedby={undefined}
        className="top-[20vh] block w-full max-w-lg translate-y-0 gap-0 border-0 bg-transparent p-0 ring-0 sm:max-w-lg"
      >
        <DialogTitle className="sr-only">Command palette</DialogTitle>
      <Command
        label="Command palette"
        className="relative w-full max-w-lg rounded-lg border border-border bg-card shadow-2xl"
      >
        <Command.Input
          placeholder="Search elements, actions..."
          className="w-full border-b border-border bg-transparent px-4 py-3 text-sm outline-none placeholder:text-muted-foreground"
          aria-label="Search elements and actions"
          data-autofocus
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
                keywords={[
                  el.kind,
                  el.technology ?? '',
                  factStatusLabel(el.factStatus ?? 'declared'),
                ]}
                onSelect={() => {
                  selectElement(el.id)
                  closeCommandPalette()
                }}
                className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
              >
                <span
                  aria-hidden="true"
                  className="h-2 w-2 rounded-full"
                  style={{ backgroundColor: kindColor(el.kind) }}
                />
                {el.name}
                {el.factStatus && el.factStatus !== 'declared' && (
                  <span
                    className="rounded-full border px-1.5 text-[10px] leading-4"
                    style={{ borderColor: factStatusColor(el.factStatus), color: factStatusColor(el.factStatus) }}
                  >
                    {factStatusLabel(el.factStatus)}
                  </span>
                )}
                <span className="ml-auto font-mono text-[10px] text-muted-foreground">
                  {el.kind}
                </span>
              </Command.Item>
            ))}
          </Command.Group>

          <Command.Separator className="my-1 h-px bg-border" />

          {/* The assistant and the comments are actions, not sidebar areas; the palette reaches every tool. */}
          <Command.Group heading="Open" className="text-xs text-muted-foreground">
            <Command.Item
              value="Ask the assistant"
              keywords={['assistant', 'ai', 'propose', 'explain']}
              onSelect={() => { openFlyout('assistant'); closeCommandPalette() }}
              className={itemClass}
            >
              Ask the assistant
            </Command.Item>
            <Command.Item
              value="Comments"
              keywords={['threads', 'inbox']}
              onSelect={() => { closeCommandPalette(); openComments() }}
              className={itemClass}
            >
              Comments
            </Command.Item>
            {visibleAreas(access).flatMap((area) => area.tools.map((tool) => (
              <Command.Item
                key={tool.flyout}
                value={`${area.label}: ${tool.label}`}
                onSelect={() => { openFlyout(tool.flyout); closeCommandPalette() }}
                className={itemClass}
              >
                {tool.label}
                <span className="ml-auto text-[10px] text-muted-foreground">{area.label}</span>
              </Command.Item>
            )))}
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
              onSelect={() => { toggleLens('messages'); closeCommandPalette() }}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
            >
              Toggle messages lens
              <kbd className="ml-auto rounded border border-border bg-muted px-1 font-mono text-[10px]">3</kbd>
            </Command.Item>
            <Command.Item
              onSelect={() => { toggleLens('facts'); closeCommandPalette() }}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm aria-selected:bg-muted"
            >
              Toggle truth labels
              <kbd className="ml-auto rounded border border-border bg-muted px-1 font-mono text-[10px]">4</kbd>
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
      </DialogContent>
    </Dialog>
  )
}
