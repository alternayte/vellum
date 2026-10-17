import { api } from '@/lib/settings-api'

interface ImportResult {
  refused: number
  items: { change: string; name: string | null; error: string | null }[]
}

/**
 * Imports the Orders example (examples/orders) into a project, as `just seed` and
 * examples/orders/import.sh do: the model document through the import endpoint, then its
 * scenarios. The files are bundled with the app, so the sample works with no network access.
 * A scenario ID is unique across projects, so each scenario gets a new one here.
 */
export async function openSample(projectId: string): Promise<void> {
  const [model, happy, inventoryDown] = await Promise.all([
    import('@examples/orders/model.json'),
    import('@examples/orders/scenarios/place-an-order.json'),
    import('@examples/orders/scenarios/place-an-order-inventory-down.json'),
  ])
  const result = await api<ImportResult>('POST', `/api/projects/${projectId}/import`, model.default)
  if (result.refused > 0) {
    const first = result.items.find((i) => i.change === 'refused')
    throw new Error(`The sample did not import: ${first?.name ?? 'an entry'} was refused (${first?.error ?? 'no reason given'}).`)
  }
  for (const scenario of [happy.default, inventoryDown.default]) {
    await api('POST', `/api/projects/${projectId}/scenarios`, { ...scenario, id: crypto.randomUUID() })
  }
}
