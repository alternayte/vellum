// Checks the code blocks of the documentation pages.
//
//   node scripts/check-snippets.mjs                    the offline checks (CI runs this)
//   node scripts/check-snippets.mjs --live <page>      also runs the `live` blocks of one page
//
// Offline, for every page under src/content/docs:
//   - each `sh` block must parse (`bash -n`);
//   - each `json` block must be valid JSON;
//   - a `sh` block in a tutorial or a runbook must have a marker on the line before its fence:
//       <!-- snippet: run -->     the check runs the block now, in an empty directory, with no network use expected;
//       <!-- snippet: live -->    the block needs a running Vellum or Docker; the check parses it only;
//       <!-- snippet: syntax -->  the block has a value that the reader supplies; the check parses it only.
//     A block with no marker fails, so that nobody adds a command to those pages without a decision.
//
// With --live, the `live` blocks of the named page (for example runbooks/backup-and-restore) run
// in page order as one bash script (`set -euo pipefail`) with the environment of the caller.
// The script runs in the root of the repository, or in the directory that --cwd names.
// Use a throwaway Compose project:
//
//   COMPOSE_PROJECT_NAME=vellum-docs VELLUM_PORT=18080 VELLUM_POSTGRES_PORT=15432 \
//     node scripts/check-snippets.mjs --live runbooks/backup-and-restore
//
// scripts/verify-live.sh runs every page that has live blocks, in order, on such a project.

import { spawnSync } from 'node:child_process'
import { mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repo = join(here, '../..')
const docsDir = join(here, '../src/content/docs')
const generatedDir = join(docsDir, 'reference/http-api')
const markedSections = ['tutorials/', 'runbooks/']

const liveIndex = process.argv.indexOf('--live')
const livePage = liveIndex >= 0 ? process.argv[liveIndex + 1] : null
const cwdIndex = process.argv.indexOf('--cwd')
const liveCwd = cwdIndex >= 0 ? process.argv[cwdIndex + 1] : repo
if (liveIndex >= 0 && !livePage) {
  console.error('--live needs a page, for example: --live runbooks/backup-and-restore')
  process.exit(2)
}

function pagesIn(dir) {
  const found = []
  for (const name of readdirSync(dir)) {
    const full = join(dir, name)
    if (full === generatedDir) continue
    if (statSync(full).isDirectory()) found.push(...pagesIn(full))
    else if (/\.mdx?$/.test(name)) found.push(full)
  }
  return found.sort()
}

/** The fenced blocks of a page: language, text, first line number and the marker before the fence. */
function blocksOf(text) {
  const lines = text.split('\n')
  const blocks = []
  let open = null
  lines.forEach((line, index) => {
    const fence = line.match(/^(\s*)(`{3,})\s*([A-Za-z0-9_-]*)/)
    if (!open) {
      if (!fence) return
      let before = index - 1
      while (before >= 0 && lines[before].trim() === '') before--
      const marker = before >= 0 ? lines[before].match(/^\s*<!--\s*snippet:\s*([a-z]+)\s*-->\s*$/) : null
      open = { indent: fence[1].length, ticks: fence[2].length, language: fence[3].toLowerCase(), line: index + 2, marker: marker?.[1] ?? null, body: [] }
      return
    }
    if (fence && fence[2].length >= open.ticks && fence[3] === '' && line.trim() === fence[2]) {
      blocks.push({ ...open, text: open.body.join('\n') + '\n' })
      open = null
      return
    }
    // A block inside a list item is indented with the item. Remove that indent.
    open.body.push(line.startsWith(' '.repeat(open.indent)) ? line.slice(open.indent) : line.trimStart())
  })
  return blocks
}

const shell = new Set(['sh', 'bash', 'shell'])
const problems = []
const counts = { pages: 0, shell: 0, json: 0, run: 0, live: 0 }
const liveBlocks = []

function bash(args, input, cwd) {
  return spawnSync('bash', args, { input, cwd, encoding: 'utf8', env: process.env })
}

/**
 * Runs a script from a file, with an empty standard input. A script that bash reads from its
 * standard input loses its remaining lines to the first command that reads standard input,
 * such as `docker compose exec -T`.
 */
function runScript(text, cwd, stdio) {
  const dir = mkdtempSync(join(tmpdir(), 'vellum-snippet-script-'))
  const file = join(dir, 'snippet.sh')
  writeFileSync(file, text)
  const result = spawnSync('bash', ['-euo', 'pipefail', file], { cwd, encoding: 'utf8', env: process.env, stdio })
  rmSync(dir, { recursive: true, force: true })
  return result
}

for (const file of pagesIn(docsDir)) {
  const page = relative(docsDir, file).replace(/\.mdx?$/, '')
  const needsMarker = markedSections.some((section) => page.startsWith(section))
  counts.pages++
  for (const block of blocksOf(readFileSync(file, 'utf8'))) {
    const where = `${relative(repo, file)}:${block.line}`
    if (block.language === 'json') {
      counts.json++
      try {
        JSON.parse(block.text)
      } catch (error) {
        problems.push(`${where}  the json block is not valid JSON: ${error.message}`)
      }
      continue
    }
    if (!shell.has(block.language)) continue
    counts.shell++

    // A value that the reader supplies is written as <name>. Outside quotes bash reads that as a redirect.
    const parsed = bash(['-n'], block.text.replace(/<[a-z][a-z0-9-]*>/g, 'VALUE'))
    if (parsed.status !== 0) {
      problems.push(`${where}  the sh block does not parse: ${parsed.stderr.trim().split('\n')[0]}`)
      continue
    }
    if (block.marker && !['run', 'live', 'syntax'].includes(block.marker)) {
      problems.push(`${where}  unknown marker "snippet: ${block.marker}" (use run, live or syntax)`)
      continue
    }
    if (needsMarker && !block.marker) {
      problems.push(`${where}  the sh block has no <!-- snippet: run|live|syntax --> marker before it`)
      continue
    }
    if (block.marker === 'run') {
      counts.run++
      const dir = mkdtempSync(join(tmpdir(), 'vellum-snippet-'))
      const result = runScript(block.text, dir, ['ignore', 'pipe', 'pipe'])
      rmSync(dir, { recursive: true, force: true })
      if (result.status !== 0) {
        problems.push(`${where}  the block marked "run" failed (exit ${result.status}): ${result.stderr.trim().split('\n').slice(-1)[0]}`)
      }
    }
    if (block.marker === 'live') {
      counts.live++
      if (page === livePage) liveBlocks.push(block)
    }
  }
}

if (problems.length > 0) {
  console.error(problems.join('\n'))
  console.error(`\ncheck-snippets: ${problems.length} problems`)
  process.exit(1)
}
console.log(
  `check-snippets: ${counts.pages} pages, ${counts.shell} sh blocks parse, ${counts.json} json blocks are valid, ` +
    `${counts.run} blocks ran, ${counts.live} live blocks parsed only`,
)

if (livePage) {
  if (liveBlocks.length === 0) {
    console.error(`check-snippets: the page ${livePage} has no block marked "live"`)
    process.exit(1)
  }
  const script = liveBlocks.map((b) => `echo '--- ${livePage}:${b.line}'\n${b.text}`).join('\n')
  const result = runScript(script, liveCwd, ['ignore', 'inherit', 'inherit'])
  if (result.status !== 0) {
    console.error(`check-snippets: the live blocks of ${livePage} failed (exit ${result.status})`)
    process.exit(1)
  }
  console.log(`check-snippets: ${liveBlocks.length} live blocks of ${livePage} ran`)
}
