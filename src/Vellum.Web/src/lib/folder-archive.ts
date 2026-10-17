import { deflateSync, zip, type AsyncZippable } from 'fflate'
import { createIgnoreTest, type IgnoreFile } from '@/lib/gitignore'

/*
 * A folder that the person chose in the browser, packed as a .zip for an archive Source.
 * The browser reads a copy of each file and never writes to the folder. Only files that a
 * scan reads go into the archive: what .gitignore leaves out, what the server's path filter
 * refuses (secrets, binaries, dependency folders) and what no extractor reads stay on the
 * machine. The rules below mirror PathFilter.cs and AdrExtractor.Directory on the server.
 */

const EXCLUDED_DIRECTORIES = new Set(['node_modules', 'vendor', 'bin', 'obj', 'dist', '.git'])
const SECRET_DIRECTORIES = new Set(['.ssh', '.aws', '.gnupg', '.azure', '.kube'])
const SECRET_SUFFIXES = ['.pem', '.key', '.pfx', '.p12', '.jks', '.keystore', '.kdbx', '.tfvars', '.tfvars.json', '.tfstate']
const SECRET_PREFIXES = ['.env', 'id_rsa', 'id_dsa', 'id_ecdsa', 'id_ed25519', 'secrets.']
const SECRET_NAMES = new Set([
  '.npmrc', '.pypirc', '.netrc', '.htpasswd', '.dockercfg', 'kubeconfig',
  'credentials', 'credentials.json', 'credentials.yaml', 'credentials.yml', 'credentials.xml',
])
const LOCK_NAMES = new Set(['package-lock.json', 'packages.lock.json', 'pnpm-lock.yaml', 'npm-shrinkwrap.json', 'go.sum', 'bun.lockb'])
const RELEVANT_EXTENSIONS = new Set(['.cs', '.csproj', '.go', '.json', '.yaml', '.yml', '.dsl'])

function extension(name: string): string {
  const dot = name.lastIndexOf('.')
  return dot <= 0 ? '' : name.slice(dot).toLowerCase()
}

function isSecretName(name: string): boolean {
  const lower = name.toLowerCase()
  if (SECRET_NAMES.has(lower)) return true
  if (SECRET_PREFIXES.some((p) => lower.startsWith(p))) return true
  if (SECRET_SUFFIXES.some((s) => lower.endsWith(s))) return true
  const ext = extension(name)
  return (ext === '.yaml' || ext === '.yml' || ext === '.json') && lower.includes('secret')
}

/** Why the server's path filter refuses the path, or null when a scan may read it. */
export function pathSkipReason(path: string): string | null {
  const segments = path.split('/')
  for (const segment of segments.slice(0, -1)) {
    const lower = segment.toLowerCase()
    if (EXCLUDED_DIRECTORIES.has(lower)) return 'excluded-directory'
    if (SECRET_DIRECTORIES.has(lower)) return 'secret-file'
  }
  const name = segments[segments.length - 1]
  const lower = name.toLowerCase()
  if (isSecretName(name)) return 'secret-file'
  if (LOCK_NAMES.has(lower) || lower.endsWith('.lock')) return 'lock-file'
  if (lower.includes('.min.') || lower.endsWith('.map')) return 'minified'
  return null
}

function adrDirectory(directory: string): boolean {
  const endsWith = (tail: string) => directory === tail || directory.endsWith('/' + tail)
  return endsWith('docs/adr') || endsWith('doc/adr') || endsWith('docs/decisions')
}

/** True when an extractor reads the file: code and manifests, Dockerfiles, and ADRs in their folders. */
export function isRelevantPath(path: string): boolean {
  const slash = path.lastIndexOf('/')
  const name = path.slice(slash + 1)
  const lower = name.toLowerCase()
  if (RELEVANT_EXTENSIONS.has(extension(name)) || name === 'go.mod') return true
  if (lower === 'dockerfile' || lower.startsWith('dockerfile.') || lower.endsWith('.dockerfile')) return true
  return slash > 0 && extension(name) === '.md' && adrDirectory(path.slice(0, slash))
}

export interface FolderFile {
  /** The path inside the chosen folder, such as `src/Orders/Program.cs`. */
  path: string
  file: File
}

/** A file of an `<input webkitdirectory>` list: its path starts with the chosen folder. */
export interface PickedFile {
  webkitRelativePath: string
  name: string
  size: number
}

/**
 * Chooses the files of a folder that go to the server. `readText` reads a `.gitignore`
 * (root, nested, and `.git/info/exclude`); the other files are not read here.
 */
export async function selectFolderFiles<T extends PickedFile>(
  picked: T[],
  readText: (file: T) => Promise<string>,
): Promise<{ name: string; files: { path: string; file: T }[]; total: number; ignored: number; bytes: number }> {
  const entries = picked.map((file) => {
    const relative = file.webkitRelativePath || file.name
    const slash = relative.indexOf('/')
    return { top: slash < 0 ? '' : relative.slice(0, slash), path: slash < 0 ? relative : relative.slice(slash + 1), file }
  })
  const name = entries[0]?.top ?? ''
  const ruleFiles: IgnoreFile[] = []
  for (const entry of entries) {
    const last = entry.path.slice(entry.path.lastIndexOf('/') + 1)
    if (last === '.gitignore' && !entry.path.split('/').includes('.git')) {
      ruleFiles.push({ path: entry.path, text: await readText(entry.file) })
    } else if (entry.path === '.git/info/exclude') {
      ruleFiles.push({ path: '.gitignore', text: await readText(entry.file) })
    }
  }
  const ignoredByGit = createIgnoreTest(ruleFiles)
  let ignored = 0
  const files: { path: string; file: T }[] = []
  for (const entry of entries) {
    if (pathSkipReason(entry.path) !== null || !isRelevantPath(entry.path)) continue
    if (ignoredByGit(entry.path)) {
      ignored++
      continue
    }
    files.push({ path: entry.path, file: entry.file })
  }
  files.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))
  return { name, files, total: entries.length, ignored, bytes: files.reduce((sum, f) => sum + f.file.size, 0) }
}

/** The server refuses an entry that expands more than this many times its compressed size above 64 KiB. */
const MAX_RATIO = 100
const RATIO_FLOOR = 64 * 1024

/**
 * Packs the files as a .zip. A large file that compresses more than the server's bomb check
 * allows is stored without compression, so a log-like JSON file does not make the upload fail.
 */
export async function zipFolder(name: string, files: FolderFile[], onRead?: (done: number, total: number) => void): Promise<File> {
  const entries: AsyncZippable = {}
  let done = 0
  for (const { path, file } of files) {
    const data = new Uint8Array(await file.arrayBuffer())
    const store = data.length > RATIO_FLOOR && deflateSync(data, { level: 6 }).length * MAX_RATIO < data.length
    entries[path] = [data, { level: store ? 0 : 6 }]
    onRead?.(++done, files.length)
  }
  const bytes = await new Promise<Uint8Array>((resolve, reject) => zip(entries, (error, out) => (error ? reject(error) : resolve(out))))
  return new File([bytes as BlobPart], `${name || 'folder'}.zip`, { type: 'application/zip' })
}
