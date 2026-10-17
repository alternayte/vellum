export interface Insets {
  top: number
  right: number
  bottom: number
  left: number
}

/**
 * Panels that open over the canvas without making it smaller: the left flyout and the
 * detail sheet. The trace dock and the status bar sit below the canvas and shrink it already.
 */
const OCCLUDERS = '#left-toolbar-flyout, [data-slot="sheet-content"][data-state="open"]'

/** Panels that lie over the bottom of the canvas, such as Type it. Each covers it from its top edge down. */
const BOTTOM_OCCLUDERS = '[data-canvas-occluder="bottom"]'

/** The canvas keeps at least this much width free; a panel that leaves less is ignored. */
const MIN_FREE_WIDTH = 240
/** The canvas keeps at least this much height free; a bottom panel that leaves less is ignored. */
const MIN_FREE_HEIGHT = 160

/** How far panels that lie over the canvas reach into it from each side, in screen pixels. */
export function canvasOcclusion(pane: DOMRect, root: ParentNode = document): Insets {
  const insets: Insets = { top: 0, right: 0, bottom: 0, left: 0 }
  for (const el of root.querySelectorAll<HTMLElement>(OCCLUDERS)) {
    const r = el.getBoundingClientRect()
    if (r.width === 0 || r.right <= pane.left || r.left >= pane.right || r.bottom <= pane.top || r.top >= pane.bottom) continue
    const centre = r.left + r.width / 2
    if (centre < pane.left + pane.width / 2) insets.left = Math.max(insets.left, r.right - pane.left)
    else insets.right = Math.max(insets.right, pane.right - r.left)
  }
  if (pane.width - insets.left - insets.right < MIN_FREE_WIDTH) {
    insets.left = 0
    insets.right = 0
  }
  for (const el of root.querySelectorAll<HTMLElement>(BOTTOM_OCCLUDERS)) {
    const r = el.getBoundingClientRect()
    if (r.width === 0 || r.right <= pane.left || r.left >= pane.right || r.bottom <= pane.top || r.top >= pane.bottom) continue
    insets.bottom = Math.max(insets.bottom, pane.bottom - r.top)
  }
  if (pane.height - insets.top - insets.bottom < MIN_FREE_HEIGHT) insets.bottom = 0
  return insets
}

/** The part of the canvas that no panel covers, in screen pixels. Null when no canvas is shown. */
export function visibleCanvasRect(root: ParentNode = document): DOMRect | null {
  const pane = root.querySelector<HTMLElement>('.react-flow')
  if (!pane) return null
  const r = pane.getBoundingClientRect()
  const i = canvasOcclusion(r, root)
  return new DOMRect(r.left + i.left, r.top + i.top, Math.max(0, r.width - i.left - i.right), Math.max(0, r.height - i.top - i.bottom))
}
