/**
 * Diagram exports. The server writes SVG and Mermaid of a view and of a scenario, and
 * Structurizr DSL of the model; the browser rasterises the server's SVG for PNG. The server
 * names no file, so the name is made here from the view, scenario or project name.
 */

export type ViewExportFormat = 'svg' | 'png' | 'mermaid'

/** The largest side of a PNG, in pixels. Browsers refuse larger canvases. */
const MAX_PNG_SIDE = 8192

function fileName(name: string, fallback: string, extension: string): string {
  return `${name.trim().replace(/[^\w.-]+/g, '-').replace(/^-+|-+$/g, '').toLowerCase() || fallback}.${extension}`
}

function save(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.click()
  // The download reads the URL after this task, so it is released later.
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

async function fetchText(url: string): Promise<string> {
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(
      response.status === 403 ? 'You do not have permission to do this.'
        : response.status === 404 ? 'The diagram was not found. It may have been deleted.'
          : 'The export did not complete.',
    )
  }
  return response.text()
}

/** Draws an SVG document on a canvas at twice its size (less for a large one) and gives the PNG. */
async function rasterise(svg: string): Promise<Blob> {
  const doc = new DOMParser().parseFromString(svg, 'image/svg+xml')
  const root = doc.documentElement
  const width = Number.parseFloat(root.getAttribute('width') ?? '')
  const height = Number.parseFloat(root.getAttribute('height') ?? '')
  if (!(width > 0) || !(height > 0)) throw new Error('The picture has no size.')
  const scale = Math.min(2, MAX_PNG_SIDE / width, MAX_PNG_SIDE / height)

  const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }))
  try {
    const image = new Image()
    image.decoding = 'async'
    image.src = url
    await image.decode()
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(width * scale))
    canvas.height = Math.max(1, Math.round(height * scale))
    const context = canvas.getContext('2d')
    if (!context) throw new Error('This browser cannot draw the picture.')
    context.drawImage(image, 0, 0, canvas.width, canvas.height)
    return await new Promise<Blob>((resolve, reject) =>
      canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('This browser cannot make a PNG.'))), 'image/png'))
  } finally {
    URL.revokeObjectURL(url)
  }
}

/** The query that picks a named revision; empty for the main model. */
const revisionQuery = (revisionId: string | null | undefined) => (revisionId ? `?revisionId=${encodeURIComponent(revisionId)}` : '')

/** Downloads one view as SVG, PNG or Mermaid, of the main model or of the named revision `revisionId`. */
export async function downloadView(
  projectId: string, viewId: string, viewName: string, format: ViewExportFormat, revisionId?: string | null,
): Promise<void> {
  const base = `/api/projects/${projectId}/views/${viewId}/export`
  const query = revisionQuery(revisionId)
  if (format === 'mermaid') {
    const text = await fetchText(`${base}/mermaid${query}`)
    save(new Blob([text], { type: 'text/plain' }), fileName(viewName, 'diagram', 'mmd'))
    return
  }
  const svg = await fetchText(`${base}/svg${query}`)
  if (format === 'svg') {
    save(new Blob([svg], { type: 'image/svg+xml' }), fileName(viewName, 'diagram', 'svg'))
    return
  }
  save(await rasterise(svg), fileName(viewName, 'diagram', 'png'))
}

export type ScenarioExportFormat = 'svg' | 'mermaid'

/**
 * Downloads one scenario as an SVG or Mermaid sequence diagram. The steps are the scenario
 * now; the names come from the named revision `revisionId`, else the draft `branchId`, else main.
 */
export async function downloadScenario(
  projectId: string, scenarioId: string, scenarioName: string, format: ScenarioExportFormat,
  model: { revisionId?: string | null; branchId?: string | null } = {},
): Promise<void> {
  const query = model.revisionId
    ? revisionQuery(model.revisionId)
    : model.branchId ? `?branchId=${encodeURIComponent(model.branchId)}` : ''
  const text = await fetchText(`/api/projects/${projectId}/scenarios/${scenarioId}/export/${format}${query}`)
  if (format === 'mermaid') save(new Blob([text], { type: 'text/plain' }), fileName(scenarioName, 'scenario', 'mmd'))
  else save(new Blob([text], { type: 'image/svg+xml' }), fileName(scenarioName, 'scenario', 'svg'))
}

/** Downloads the model of main, or of the named revision `revisionId`, as a Structurizr DSL workspace. */
export async function downloadStructurizr(projectId: string, projectName: string, revisionId?: string | null): Promise<void> {
  const text = await fetchText(`/api/projects/${projectId}/export/structurizr${revisionQuery(revisionId)}`)
  save(new Blob([text], { type: 'text/plain' }), fileName(projectName, 'workspace', 'dsl'))
}
