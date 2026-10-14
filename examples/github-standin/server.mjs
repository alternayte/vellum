// A stand-in for the GitHub REST API that serves directories of this machine as the refs of
// one repository. With it you can try a Vellum import, and a comparison of two refs, with
// no GitHub account and no network.
//
//   node examples/github-standin/server.mjs
//
// With no arguments it serves the fixture repositories of the Vellum tests as `acme/orders`:
//   ref `main`           tests/Vellum.Tests/Fixtures/Repos/orders
//   ref `remove-outbox`  tests/Vellum.Tests/Fixtures/Repos/orders-outbox-removed
//
// and one open pull request:
//   #1  "Remove the outbox"  head `remove-outbox`, base `main`
//
// Other content:
//   node examples/github-standin/server.mjs --repo my/shop --ref main=../shop --ref next=../shop-next --port 5299
//   ... --pull 4=main:next   (pull request #4 from ref `next` into ref `main`; the title names the head ref)
//
// It implements only the calls that a Vellum scan makes: get a repository, resolve a ref to
// a commit, read the tree of a commit, read a blob, get a pull request, the rate limit and
// the user. It is
// read-only, it needs no token and it accepts any token. A commit SHA is a hash of the file
// names and contents of the directory, so the same files always give the same SHA. It is
// not a git commit ID.

import { createHash } from 'node:crypto'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import http from 'node:http'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const fixtures = join(repoRoot, 'tests/Vellum.Tests/Fixtures/Repos')

const args = process.argv.slice(2)
const option = (name) => args.flatMap((a, i) => (a === name ? [args[i + 1]] : []))
const port = Number(option('--port')[0] ?? 5299)
const host = option('--host')[0] ?? '127.0.0.1'
const fullName = option('--repo')[0] ?? 'acme/orders'
const refArgs = option('--ref')
const refDirs =
  refArgs.length > 0
    ? refArgs.map((r) => {
        const at = r.indexOf('=')
        if (at < 1) throw new Error(`--ref takes name=directory, not "${r}"`)
        return [r.slice(0, at), resolve(r.slice(at + 1))]
      })
    : [
        ['main', join(fixtures, 'orders')],
        ['remove-outbox', join(fixtures, 'orders-outbox-removed')],
      ]

const sha1 = (...parts) => {
  const hash = createHash('sha1')
  for (const part of parts) hash.update(part)
  return hash.digest('hex')
}

function filesIn(dir, prefix = '') {
  const found = []
  for (const name of readdirSync(dir).sort()) {
    if (name === '.git') continue
    const full = join(dir, name)
    const path = prefix ? `${prefix}/${name}` : name
    if (statSync(full).isDirectory()) found.push(...filesIn(full, path))
    else found.push({ path, content: readFileSync(full) })
  }
  return found
}

/** Reads a directory as one commit: blobs with the SHA that git gives them, and the tree. */
function commitOf(dir) {
  const files = filesIn(dir)
  const blobs = new Map()
  const tree = []
  const folders = new Set()
  const commit = createHash('sha1')
  for (const file of files) {
    const sha = sha1(`blob ${file.content.length}\0`, file.content)
    blobs.set(sha, file.content)
    commit.update(`${file.path}\0${sha}\n`)
    const parts = file.path.split('/')
    for (let i = 1; i < parts.length; i++) folders.add(parts.slice(0, i).join('/'))
    tree.push({ path: file.path, mode: '100644', type: 'blob', sha, size: file.content.length })
  }
  for (const folder of folders) tree.push({ path: folder, mode: '040000', type: 'tree', sha: sha1('tree ', folder) })
  tree.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))
  return { sha: commit.digest('hex'), tree, blobs, files: files.length }
}

const refs = new Map()
const commits = new Map()
for (const [name, dir] of refDirs) {
  const commit = commitOf(dir)
  refs.set(name, commit.sha)
  commits.set(commit.sha, commit)
  console.log(`${fullName}@${name}  ${commit.sha}  ${commit.files} files  ${dir}`)
}
const defaultBranch = refDirs[0][0]
const [owner, name] = fullName.toLowerCase().split('/')

// Pull requests by number. Each names a base ref and a head ref of this repository.
const pullArgs = option('--pull')
const pullSpecs =
  pullArgs.length > 0
    ? pullArgs.map((p) => {
        const match = /^(\d+)=([^:]+):(.+)$/.exec(p)
        if (!match) throw new Error(`--pull takes number=base:head, not "${p}"`)
        return { number: Number(match[1]), base: match[2], head: match[3], title: `Merge ${match[3]} into ${match[2]}` }
      })
    : refArgs.length > 0
      ? []
      : [{ number: 1, base: 'main', head: 'remove-outbox', title: 'Remove the outbox' }]
const pulls = new Map()
for (const spec of pullSpecs) {
  for (const ref of [spec.base, spec.head]) if (!refs.has(ref)) throw new Error(`--pull names the ref "${ref}", which no --ref gives`)
  const side = (ref) => ({ ref, sha: refs.get(ref), repo: { full_name: fullName } })
  pulls.set(spec.number, {
    number: spec.number,
    title: spec.title,
    state: 'open',
    merged: false,
    draft: false,
    base: side(spec.base),
    head: side(spec.head),
  })
  console.log(`${fullName}#${spec.number}  ${spec.head} -> ${spec.base}  "${spec.title}"`)
}

function send(response, status, body) {
  const reset = Math.floor(Date.now() / 1000) + 3600
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'x-ratelimit-limit': '5000',
    'x-ratelimit-remaining': '4999',
    'x-ratelimit-reset': String(reset),
  })
  response.end(JSON.stringify(body))
}

const notFound = (response) => send(response, 404, { message: 'Not Found', documentation_url: 'https://docs.github.com/rest' })

http
  .createServer((request, response) => {
    const url = new URL(request.url, 'http://standin')
    const parts = url.pathname.split('/').filter(Boolean).map(decodeURIComponent)
    console.log(`${request.method} ${request.url}`)
    if (request.method !== 'GET') return send(response, 405, { message: 'The stand-in is read-only.' })

    if (url.pathname === '/rate_limit') {
      const core = { limit: 5000, remaining: 4999, reset: Math.floor(Date.now() / 1000) + 3600, used: 1 }
      return send(response, 200, { resources: { core }, rate: core })
    }
    if (url.pathname === '/user') return send(response, 200, { login: 'standin', id: 1 })

    if (parts[0] !== 'repos' || parts[1]?.toLowerCase() !== owner || parts[2]?.toLowerCase() !== name) return notFound(response)
    const rest = parts.slice(3)

    if (rest.length === 0) {
      return send(response, 200, { id: 1, full_name: fullName, private: false, default_branch: defaultBranch, archived: false, size: 1 })
    }
    if (rest.length === 1 && rest[0] === 'commits') {
      const wanted = url.searchParams.get('sha') ?? defaultBranch
      const sha = refs.get(wanted) ?? (commits.has(wanted) ? wanted : null)
      // GitHub answers 404 for a ref that does not exist.
      return sha ? send(response, 200, [{ sha }]) : notFound(response)
    }
    if (rest.length === 2 && rest[0] === 'pulls') {
      const pull = /^\d+$/.test(rest[1]) ? pulls.get(Number(rest[1])) : undefined
      return pull ? send(response, 200, pull) : notFound(response)
    }
    if (rest.length === 3 && rest[0] === 'git' && rest[1] === 'trees') {
      const commit = commits.get(rest[2])
      return commit ? send(response, 200, { sha: commit.sha, truncated: false, tree: commit.tree }) : notFound(response)
    }
    if (rest.length === 3 && rest[0] === 'git' && rest[1] === 'blobs') {
      for (const commit of commits.values()) {
        const content = commit.blobs.get(rest[2])
        if (content) {
          // GitHub breaks the base64 text into lines of 60 characters.
          const text = content.toString('base64').replace(/(.{60})/g, '$1\n')
          return send(response, 200, { sha: rest[2], size: content.length, encoding: 'base64', content: text })
        }
      }
      return notFound(response)
    }
    return notFound(response)
  })
  .listen(port, host, () => console.log(`GitHub stand-in for ${fullName} on http://${host}:${port}`))
