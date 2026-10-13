// Fails when a page names an HTTP path or a method that the API does not have.
//
//   node scripts/check-api-paths.mjs
//
// It reads every Markdown page under src/content/docs (not the generated HTTP API pages) and
// README.md, finds each `/api/...` path, and looks it up in src/Vellum.Web/openapi.json.
// A path written as `METHOD /api/...` must also have that method.

import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repo = join(here, '../..')
const docsDir = join(here, '../src/content/docs')
const generatedDir = join(docsDir, 'reference/http-api')

const openapi = JSON.parse(readFileSync(join(repo, 'src/Vellum.Web/openapi.json'), 'utf8'))
const templates = Object.entries(openapi.paths).map(([path, item]) => ({
  path,
  segments: path.split('/').filter(Boolean),
  methods: new Set(Object.keys(item).map((m) => m.toUpperCase())),
}))

function pagesIn(dir) {
  const found = []
  for (const name of readdirSync(dir)) {
    const full = join(dir, name)
    if (full === generatedDir) continue
    if (statSync(full).isDirectory()) found.push(...pagesIn(full))
    else if (/\.mdx?$/.test(name)) found.push(full)
  }
  return found
}

const isParameter = (segment) => /^\{.*\}$/.test(segment)
// What a page writes in place of a value: {id}, <id>, $ID, ${ID}, or "…".
const isPlaceholder = (segment) => /^(\{.*\}|<.*>|\$\{?\w+\}?|…|\.\.\.)$/.test(segment)

/** The templates that the written path matches. `open` means the page ended the path with "..." or "…". */
function matches(segments, open) {
  return templates.filter((t) => {
    if (open ? t.segments.length < segments.length : t.segments.length !== segments.length) return false
    return segments.every((segment, i) => {
      const expected = t.segments[i]
      if (isParameter(expected)) return true // a placeholder or an example value
      return !isPlaceholder(segment) && segment === expected
    })
  })
}

const pathPattern = /(?:\b(GET|POST|PUT|PATCH|DELETE)\s+)?(?<![\w/.-])(\/api\/[A-Za-z0-9_\-{}<>$:/.…]*)/g

const problems = []
let checked = 0

for (const file of [...pagesIn(docsDir), join(repo, 'README.md')]) {
  const lines = readFileSync(file, 'utf8').split('\n')
  lines.forEach((line, index) => {
    // The address of this instance is not part of the path. Any other URL is another service.
    const text = line
      .replace(/https?:\/\/(localhost|127\.0\.0\.1|\$\{?\w+\}?|<[a-z-]+>|vellum\.example\.com)(:\d+)?(?=\/)/g, ' ')
      .replace(/https?:\/\/[^\s)`'"]+/g, ' ')
    for (const match of text.matchAll(pathPattern)) {
      const method = match[1]
      let written = match[2].replace(/[.,:;]+$/, '')
      const open = /(\.\.\.|…)$/.test(written) || written.endsWith('/')
      written = written.replace(/(\/?(\.\.\.|…))$/, '').replace(/\/$/, '')
      // A route constraint of ASP.NET, as in {promiseId:guid}, is not part of the template.
      written = written.replace(/\{(\w+):[^}]+\}/g, '{$1}')
      const segments = written.split('/').filter(Boolean)
      const found = matches(segments, open)
      checked++
      const where = `${relative(repo, file)}:${index + 1}`
      if (found.length === 0) {
        problems.push(`${where}  ${written}  is not a path of the API`)
      } else if (method && !open && !found.some((t) => t.methods.has(method))) {
        const has = [...new Set(found.flatMap((t) => [...t.methods]))].join(', ')
        problems.push(`${where}  ${method} ${written}  the path has ${has} only`)
      }
    }
  })
}

if (problems.length > 0) {
  console.error(problems.join('\n'))
  console.error(`\ncheck-api-paths: ${problems.length} of ${checked} paths are not in openapi.json`)
  process.exit(1)
}
console.log(`check-api-paths: ${checked} paths, all in openapi.json`)
