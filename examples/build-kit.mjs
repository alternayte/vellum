// The part that every examples/<name>/build.mjs shares: sign in to a running Vellum, create the
// project of the example, and after the build play the scenarios, run the stress profile and
// write the files of the example (model.json, scenarios/*.json and stress-profile.json).
//
//   VELLUM_URL=http://localhost:8080 VELLUM_EMAIL=you@example.com VELLUM_PASSWORD=... node build.mjs
//
// On an instance with no account, give VELLUM_SETUP_CODE in place of an existing account:
// the script then completes first-run setup with VELLUM_EMAIL and VELLUM_PASSWORD.
//
// Use a throwaway instance. A schema ID and a scenario ID belong to one project of an
// instance, so a build fails on an instance that already holds its example.

import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

const base = (process.env.VELLUM_URL ?? 'http://localhost:8080').replace(/\/$/, '')

let cookie = ''

export async function call(method, path, body) {
  const response = await fetch(base + path, {
    method,
    headers: { ...(body === undefined ? {} : { 'content-type': 'application/json' }), ...(cookie ? { cookie } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const set = response.headers.getSetCookie?.() ?? []
  if (set.length > 0) cookie = set.map((c) => c.split(';')[0]).join('; ')
  const text = await response.text()
  if (!response.ok) throw new Error(`${method} ${path} -> ${response.status} ${text}`)
  return text ? JSON.parse(text) : null
}

/** The text of a JSON Schema of an object with exactly these properties. */
export const schema = (properties, required) => JSON.stringify({ type: 'object', required, properties, additionalProperties: false })

/**
 * Signs in (after first-run setup when VELLUM_SETUP_CODE is set) and creates the project of
 * the example in the first workspace. Returns a function that posts to a path of the project.
 */
export async function startProject({ id, name, description }) {
  const email = process.env.VELLUM_EMAIL
  const password = process.env.VELLUM_PASSWORD
  const setupCode = process.env.VELLUM_SETUP_CODE
  if (!email || !password) {
    console.error('Set VELLUM_EMAIL and VELLUM_PASSWORD.')
    process.exit(2)
  }
  if (setupCode) {
    // A second build on the same instance finds the setup done.
    const setup = await call('GET', '/api/setup/status')
    if (setup.needsSetup) {
      await call('POST', '/api/setup', { email, password, displayName: 'Example builder', workspaceName: 'Examples', setupToken: setupCode })
    }
  }
  await call('POST', '/api/auth/login', { email, password })

  const workspaces = await call('GET', '/api/workspaces')
  const workspace = (workspaces.items ?? workspaces)[0]
  if (!workspace) throw new Error('The account has no workspace.')

  await call('POST', `/api/workspaces/${workspace.id}/projects`, { id, name, description })
  const path = `/api/projects/${id}`
  return { path, post: (to, body) => call('POST', `${path}/${to}`, body) }
}

/**
 * Saves and plays each scenario, runs the stress profile, and writes the files of the example.
 * A scenario that cannot play or whose expectations do not hold writes nothing, and so does a
 * stress run that is refused.
 */
export async function finishProject(here, path, scenarios, profile) {
  mkdirSync(join(here, 'scenarios'), { recursive: true })
  for (const [name, document] of Object.entries(scenarios)) {
    await call('POST', `${path}/scenarios`, document)
    const run = await call('POST', `${path}/scenarios/${document.id}/run`)
    if (run.issues.length > 0) throw new Error(`${name} cannot play: ${JSON.stringify(run.issues)}`)
    const failed = run.assertions.filter((a) => !a.passed)
    if (failed.length > 0) throw new Error(`${name}: ${JSON.stringify(failed)}`)
    console.log(`${name}: ${run.events.length} events, ${run.assertions.length} expectations hold, hash ${run.hash}`)
    writeFileSync(join(here, 'scenarios', `${name}.json`), JSON.stringify(document, null, 2) + '\n')
  }

  if (profile) {
    const saved = await call('POST', `${path}/stress/profiles`, profile)
    const stored = await call('POST', `${path}/stress/profiles/${saved.id}/runs`)
    const flow = stored.result.flow
    console.log(
      `stress-profile.json (${profile.name}): ${flow.arrivals} arrivals, ${flow.counts.completed} completed, ` +
        `p95 ${flow.latency.p95Ms.toFixed(1)} ms, bottleneck ${stored.result.bottleneck?.name ?? 'none'}, hash ${stored.run.hash}`,
    )
    writeFileSync(join(here, 'stress-profile.json'), JSON.stringify(profile, null, 2) + '\n')
  }

  const model = await call('GET', `${path}/export`)
  writeFileSync(join(here, 'model.json'), JSON.stringify(model, null, 2) + '\n')
  console.log(
    `model.json: ${model.elements.length} elements, ${model.relationships.length} relationships, ${model.schemas.length} schemas, ` +
      `${model.operations.length} operations, ${model.channels.length} channels, ${model.bindings.length} bindings, ` +
      `${model.subscriptions.length} subscriptions, ${model.messages.length} messages`,
  )
}
