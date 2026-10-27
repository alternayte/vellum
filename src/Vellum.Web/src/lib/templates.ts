import { api } from '@/lib/settings-api'

/**
 * A design that an empty project can start from. Its files are examples/<id> in the
 * repository: model.json, scenarios/*.json and stress-profile.json. They are bundled with
 * the app, so a template opens with no network access.
 */
export interface Template {
  id: string
  name: string
  description: string
  /** The elements of its model.json; a test holds the two equal. */
  elements: number
}

export const TEMPLATES: Template[] = [
  { id: 'orders', name: 'Orders', description: 'A small shop: an Orders API reserves stock, publishes OrderCreated and charges through a payment provider.', elements: 8 },
  { id: 'url-shortener', name: 'URL shortener', description: 'Short links: the Links API redirects each visitor and sends every click to Analytics.', elements: 8 },
  { id: 'chat-service', name: 'Chat service', description: 'Group chat: a gateway takes messages, a service stores them, and a notifier pushes them to phones.', elements: 8 },
  { id: 'payments', name: 'Payments', description: 'Card payments: a risk check, an authorisation at the card network, and a ledger entry for each capture.', elements: 9 },
]

// Each file is a chunk of its own, loaded when its template opens.
const models = import.meta.glob<unknown>('../../../../examples/*/model.json', { import: 'default' })
const scenarioFiles = import.meta.glob<{ id: string }>('../../../../examples/*/scenarios/*.json', { import: 'default' })
const profiles = import.meta.glob<{ scenarioId: string }>('../../../../examples/*/stress-profile.json', { import: 'default' })

/** The files of a template, with its scenarios in the order of their file names. */
export async function templateFiles(id: string) {
  const dir = `../../../../examples/${id}/`
  const model = models[`${dir}model.json`]
  const profile = profiles[`${dir}stress-profile.json`]
  if (!model || !profile) throw new Error(`The template ${id} is not bundled with this app.`)
  const scenarioPaths = Object.keys(scenarioFiles).filter((path) => path.startsWith(`${dir}scenarios/`)).sort()
  return {
    model: await model(),
    scenarios: await Promise.all(scenarioPaths.map((path) => scenarioFiles[path]())),
    profile: await profile(),
  }
}

interface ImportResult {
  refused: number
  items: { change: string; name: string | null; error: string | null }[]
}

/**
 * Opens a template in a project, as examples/orders/import.sh does: the model document through
 * the import endpoint, then its scenarios, then its stress profile, so a Stress run works at
 * once. A scenario ID is unique across projects, so each scenario gets a new one here, and the
 * profile names the new ID of its scenario.
 */
export async function openTemplate(projectId: string, id: string): Promise<void> {
  const { model, scenarios, profile } = await templateFiles(id)
  const result = await api<ImportResult>('POST', `/api/projects/${projectId}/import`, model)
  if (result.refused > 0) {
    const first = result.items.find((i) => i.change === 'refused')
    throw new Error(`The template did not import: ${first?.name ?? 'an entry'} was refused (${first?.error ?? 'no reason given'}).`)
  }
  const newIds = new Map<string, string>()
  for (const scenario of scenarios) {
    const newId = crypto.randomUUID()
    newIds.set(scenario.id, newId)
    await api('POST', `/api/projects/${projectId}/scenarios`, { ...scenario, id: newId })
  }
  const scenarioId = newIds.get(profile.scenarioId)
  if (!scenarioId) throw new Error(`The stress profile of the template ${id} names a scenario that the template does not hold.`)
  await api('POST', `/api/projects/${projectId}/stress/profiles`, { ...profile, scenarioId })
}
