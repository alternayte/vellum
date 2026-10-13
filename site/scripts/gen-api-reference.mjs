// Writes the HTTP API reference pages from the OpenAPI document that the API build produces
// (src/Vellum.Web/openapi.json, refreshed by `just openapi`). The pages are build output:
// they are not in the repository, so they cannot differ from the API.
//
//   node scripts/gen-api-reference.mjs
//
// Output: src/content/docs/reference/http-api/index.md, one page for each tag, types.md.

import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const source = join(here, '../../src/Vellum.Web/openapi.json')
const outDir = join(here, '../src/content/docs/reference/http-api')
const base = '/reference/http-api'

const doc = JSON.parse(readFileSync(source, 'utf8'))
const schemas = doc.components?.schemas ?? {}
const methods = ['get', 'post', 'put', 'patch', 'delete']

/** The anchor that Starlight gives a heading (github-slugger, for the characters that occur here). */
const slug = (text) =>
  text
    .toLowerCase()
    .replace(/[^a-z0-9 _-]/g, '')
    .replace(/ /g, '-')

const tagSlug = (tag) => slug(tag)

// A schema name is a heading on the schemas page. Two names that differ only in case would get the same anchor.
const schemaAnchors = new Map()
for (const name of Object.keys(schemas)) {
  const anchor = slug(name)
  if ([...schemaAnchors.values()].includes(anchor)) {
    throw new Error(`Two schemas have the anchor "${anchor}". Give each schema page heading a unique name.`)
  }
  schemaAnchors.set(name, anchor)
}

const cell = (text) => String(text).replace(/\|/g, '\\|').replace(/\n/g, ' ')

const refName = (ref) => ref.split('/').pop()

/** A short type for a table cell, with a link for each named schema. */
function typeOf(schema) {
  if (!schema || Object.keys(schema).length === 0) return 'any'
  if (schema.$ref) {
    const name = refName(schema.$ref)
    return `[${name}](${base}/types/#${schemaAnchors.get(name)})`
  }
  for (const key of ['oneOf', 'anyOf']) {
    if (schema[key]) return schema[key].map(typeOf).join(' or ')
  }
  if (schema.allOf) return schema.allOf.map(typeOf).join(' and ')
  if (schema.enum) return schema.enum.map((v) => `\`${JSON.stringify(v)}\``).join(' or ')
  if (schema.const !== undefined) return `\`${JSON.stringify(schema.const)}\``
  const types = Array.isArray(schema.type) ? schema.type : [schema.type ?? 'any']
  const nullable = types.includes('null')
  const named = types
    .filter((t) => t !== 'null')
    .map((t) => {
      if (t === 'array') return `array of ${typeOf(schema.items)}`
      if (t === 'object') {
        if (schema.additionalProperties && typeof schema.additionalProperties === 'object') {
          return `map of string to ${typeOf(schema.additionalProperties)}`
        }
        return 'object'
      }
      return schema.format ? `${t} (${schema.format})` : t
    })
  const text = named.length > 0 ? named.join(' or ') : 'null'
  return nullable && named.length > 0 ? `${text} or null` : text
}

function limitsOf(schema) {
  const parts = []
  for (const key of ['minimum', 'maximum', 'minLength', 'maxLength', 'minItems', 'maxItems', 'pattern', 'default']) {
    if (schema?.[key] !== undefined) parts.push(`${key} \`${JSON.stringify(schema[key])}\``)
  }
  return parts.join(', ')
}

function fieldTable(schema) {
  const required = new Set(schema.required ?? [])
  const rows = Object.entries(schema.properties ?? {}).map(
    ([name, property]) =>
      `| \`${name}\` | ${cell(typeOf(property))} | ${required.has(name) ? 'yes' : 'no'} | ${cell(
        [property.description, limitsOf(property)].filter(Boolean).join(' '),
      )} |`,
  )
  if (rows.length === 0) return ''
  return ['| Field | Type | Required | Notes |', '|---|---|---|---|', ...rows].join('\n')
}

function bodyOf(content) {
  if (!content) return null
  const [mediaType, media] = Object.entries(content)[0]
  return { mediaType, schema: media.schema }
}

function operationSection(path, method, operation) {
  const lines = [`## ${method.toUpperCase()} ${path}`, '']
  if (operation.summary) lines.push(operation.summary, '')
  if (operation.description) lines.push(operation.description, '')

  const parameters = operation.parameters ?? []
  if (parameters.length > 0) {
    lines.push('| Parameter | In | Type | Required |', '|---|---|---|---|')
    for (const p of parameters) {
      lines.push(`| \`${p.name}\` | ${p.in} | ${cell(typeOf(p.schema))} | ${p.required ? 'yes' : 'no'} |`)
    }
    lines.push('')
  }

  const body = bodyOf(operation.requestBody?.content)
  if (body) {
    lines.push(`Request body (\`${body.mediaType}\`): ${typeOf(body.schema)}`, '')
    const target = body.schema?.$ref ? schemas[refName(body.schema.$ref)] : body.schema
    const table = target ? fieldTable(target) : ''
    if (table) lines.push(table, '')
  }

  lines.push('| Status | Meaning | Body |', '|---|---|---|')
  for (const [status, response] of Object.entries(operation.responses ?? {})) {
    const answer = bodyOf(response.content)
    lines.push(`| \`${status}\` | ${cell(response.description ?? '')} | ${answer ? cell(typeOf(answer.schema)) : 'none'} |`)
  }
  lines.push('')
  return lines.join('\n')
}

// Group the operations by their first tag, in the order of the document.
const tagOrder = (doc.tags ?? []).map((t) => t.name)
const byTag = new Map(tagOrder.map((t) => [t, []]))
let operationCount = 0
for (const [path, item] of Object.entries(doc.paths)) {
  for (const method of methods) {
    const operation = item[method]
    if (!operation) continue
    const tag = operation.tags?.[0] ?? 'Other'
    if (!byTag.has(tag)) byTag.set(tag, [])
    byTag.get(tag).push({ path, method, operation })
    operationCount++
  }
}

const tagSlugs = new Set()
for (const tag of byTag.keys()) {
  const s = tagSlug(tag)
  if (tagSlugs.has(s) || s === 'types' || s === 'index') throw new Error(`The tag "${tag}" has no unique page name.`)
  tagSlugs.add(s)
}

const frontmatter = (title, description, order) =>
  ['---', `title: ${JSON.stringify(title)}`, `description: ${JSON.stringify(description)}`, 'sidebar:', `  order: ${order}`, 'editUrl: false', '---', ''].join('\n')

const generatedNote =
  'This page is generated from the OpenAPI document of the API (`src/Vellum.Web/openapi.json`) when the documentation is built.'

rmSync(outDir, { recursive: true, force: true })
mkdirSync(outDir, { recursive: true })

// Index
{
  const lines = [
    frontmatter(
      'HTTP API',
      'Every endpoint of the Vellum HTTP API, by area, with its parameters, request body and answers.',
      0,
    ).replace('sidebar:\n', 'sidebar:\n  label: Overview\n'),
  ]
  lines.push(
    `${generatedNote} It lists ${operationCount} operations on ${Object.keys(doc.paths).length} paths and ${Object.keys(schemas).length} schemas. The document is OpenAPI ${doc.openapi}.`,
    '',
    '[Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in, how to send a version with a write and how to read an error.',
    '',
    'These paths are not in the OpenAPI document: `/health`, `/mcp`, `/llms.txt`, `/llms-full.txt` and `/skills/vellum/SKILL.md`. [Bootstrap environment](/reference/bootstrap-environment/#health) describes `/health`. [MCP tools](/reference/mcp-tools/) describes `/mcp`.',
    '',
    '## Areas',
    '',
    '| Area | Operations | Paths |',
    '|---|---|---|',
  )
  for (const [tag, operations] of byTag) {
    if (operations.length === 0) continue
    const paths = [...new Set(operations.map((o) => `\`${o.path}\``))]
    lines.push(`| [${tag}](${base}/${tagSlug(tag)}/) | ${operations.length} | ${cell(paths.join('<br>'))} |`)
  }
  lines.push('', `[Types](${base}/types/) lists each request and answer type with its fields.`, '')
  writeFileSync(join(outDir, 'index.md'), lines.join('\n'))
}

// One page for each tag
let order = 1
for (const [tag, operations] of byTag) {
  if (operations.length === 0) continue
  const lines = [
    frontmatter(`${tag} endpoints`, `The ${tag} endpoints of the Vellum HTTP API.`, order++).replace(
      'sidebar:\n',
      `sidebar:\n  label: ${JSON.stringify(tag)}\n`,
    ),
    generatedNote,
    '',
  ]
  for (const { path, method, operation } of operations) lines.push(operationSection(path, method, operation))
  writeFileSync(join(outDir, `${tagSlug(tag)}.md`), lines.join('\n'))
}

// Schemas
{
  const lines = [
    frontmatter('Types', 'Every request and answer type of the Vellum HTTP API, with its fields.', 1000),
    generatedNote,
    '',
  ]
  for (const [name, schema] of Object.entries(schemas)) {
    lines.push(`## ${name}`, '')
    if (schema.description) lines.push(schema.description, '')
    const table = fieldTable(schema)
    if (table) lines.push(table, '')
    else lines.push(`Type: ${typeOf(schema)}`, '')
    if (schema.additionalProperties && typeof schema.additionalProperties === 'object' && table) {
      lines.push(`Other fields: ${typeOf(schema.additionalProperties)}`, '')
    }
  }
  writeFileSync(join(outDir, 'types.md'), lines.join('\n'))
}

console.log(`http-api: ${operationCount} operations, ${byTag.size} areas, ${Object.keys(schemas).length} schemas`)
