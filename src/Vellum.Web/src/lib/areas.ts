/** A tool panel that opens next to the sidebar. */
export type Flyout =
  | 'catalog' | 'contracts' | 'create' | 'files'
  | 'scenarios'
  | 'review' | 'promises' | 'stress' | 'costs'
  | 'build' | 'share'
  | 'import'
  | 'assistant'

/** A sidebar area: one job, with its tools as tabs of one panel. */
export type Area = 'model' | 'flows' | 'check' | 'ship' | 'sources'

export interface AreaTool {
  flyout: Flyout
  label: string
}

export interface AreaDefinition {
  id: Area
  label: string
  tools: AreaTool[]
  /** The area has nothing to work on before the model holds an element, so it is hidden until then. */
  needsModel: boolean
}

export const AREAS: AreaDefinition[] = [
  {
    id: 'model',
    label: 'Model',
    needsModel: false,
    tools: [
      { flyout: 'catalog', label: 'Catalog' },
      { flyout: 'contracts', label: 'Contracts' },
      { flyout: 'create', label: 'Create' },
      { flyout: 'files', label: 'Files' },
    ],
  },
  { id: 'flows', label: 'Flows', needsModel: false, tools: [{ flyout: 'scenarios', label: 'Scenarios' }] },
  {
    id: 'check',
    label: 'Check',
    needsModel: true,
    tools: [
      { flyout: 'review', label: 'Review' },
      { flyout: 'promises', label: 'Promises' },
      { flyout: 'stress', label: 'Stress' },
      { flyout: 'costs', label: 'Costs' },
    ],
  },
  {
    id: 'ship',
    label: 'Ship',
    needsModel: true,
    tools: [
      { flyout: 'build', label: 'Build' },
      { flyout: 'share', label: 'Share links' },
    ],
  },
  { id: 'sources', label: 'Sources', needsModel: false, tools: [{ flyout: 'import', label: 'Sources' }] },
]

/** The area whose panel holds the tool. Null for the assistant, which opens from the canvas and the command palette. */
export function areaOf(flyout: Flyout): Area | null {
  return AREAS.find((a) => a.tools.some((t) => t.flyout === flyout))?.id ?? null
}

export interface AreaAccess {
  /** True when the model that the workbench shows holds at least one element. */
  hasElements: boolean
  /** True when the person can manage share links: an Editor or an Owner. */
  canShare: boolean
}

/** The areas and tools that the person sees now. */
export function visibleAreas({ hasElements, canShare }: AreaAccess): AreaDefinition[] {
  return AREAS
    .filter((a) => hasElements || !a.needsModel)
    .map((a) => ({ ...a, tools: a.tools.filter((t) => t.flyout !== 'share' || canShare) }))
}

/** True when the tool is in an area that the person sees now. The assistant is always available. */
export function isToolVisible(flyout: Flyout, access: AreaAccess): boolean {
  if (flyout === 'assistant') return true
  return visibleAreas(access).some((a) => a.tools.some((t) => t.flyout === flyout))
}
