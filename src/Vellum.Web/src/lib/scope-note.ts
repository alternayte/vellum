import type { Flyout } from '@/stores/shell-store'
import { selectionName, type ModelSelection } from '@/stores/draft-store'

/** Panels whose content belongs to the project or reads main only, whatever model the workbench shows. */
const NOT_BRANCHED: Partial<Record<Flyout, string>> = {
  files: 'Views and documents belong to the project, so every model shares them. The model document is the document of main.',
  create: 'A diagram and a document belong to the project, so every model shares them.',
  import: 'An import from GitHub reads main and its proposals change main only.',
  promises: 'Promises belong to the project. A check that you run here plays on main.',
  build: 'The build export and Build Ready read main only.',
}

/** Panels that follow a draft or a variant, with the part of them that does not. */
const BRANCHED: Partial<Record<Flyout, (model: string) => string>> = {
  scenarios: (model) => `Scenarios belong to the project. A run plays on ${model}.`,
  review: (model) => `The findings are of ${model}. Decisions and open questions belong to the project.`,
  stress: (model) => `Profiles belong to the project. A run uses ${model}.`,
  costs: (model) => `This panel opens on the cost sheet of ${model}. Its Branch list selects the sheet of another model.`,
}

/** What a panel shows when the workbench shows another model than main. Null when the panel follows the model with nothing to say. */
export function scopeNote(flyout: Flyout | null, selection: ModelSelection): string | null {
  if (!flyout || selection.kind === 'main') return null
  const model = selectionName(selection)
  if (NOT_BRANCHED[flyout]) return `This panel does not follow ${model}. ${NOT_BRANCHED[flyout]}`
  if (selection.kind === 'revision') {
    if (flyout === 'review') return `The findings are of ${model}. Decisions and open questions belong to the project. The assistant reads main.`
    if (flyout === 'contracts') return `The contracts are of ${model}. A revision is read-only.`
    return flyout === 'catalog'
      ? `The catalog lists ${model}. A revision is read-only, so nothing can be added to it.`
      : `This panel shows main, not ${model}. A revision is read-only: the canvas and Compare show it.`
  }
  if (flyout === 'comments') {
    return selection.kind === 'draft'
      ? `The threads are of ${model}. A merge moves its open threads to main.`
      : `Threads belong to main and to drafts. This panel shows the threads of main and takes no new one on ${model}.`
  }
  return BRANCHED[flyout]?.(model) ?? null
}
