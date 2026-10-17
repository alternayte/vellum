/**
 * The rules of `.gitignore` files, as git reads them, for a folder that the browser read. A
 * rule file applies to the paths under its own folder; a deeper file wins over a shallower
 * one, and within a file the last matching line wins. A path under an ignored folder stays
 * ignored: git does not look inside that folder, so no rule can bring a file back.
 */

interface Rule {
  /** The folder of the rule file, with a trailing slash, or '' at the root. */
  base: string
  pattern: RegExp
  negate: boolean
  directoryOnly: boolean
}

/** One rule file: its path relative to the chosen folder, such as `.gitignore` or `web/.gitignore`, and its text. */
export interface IgnoreFile {
  path: string
  text: string
}

/** True when git would leave the path out. `directory` says whether the path names a folder. */
export type IgnoreTest = (path: string, directory?: boolean) => boolean

export function createIgnoreTest(files: IgnoreFile[]): IgnoreTest {
  const rules = files
    .map((file) => ({ file, depth: file.path.split('/').length }))
    .sort((a, b) => a.depth - b.depth)
    .flatMap(({ file }) => parseRules(file.text, baseOf(file.path)))

  const matches = (path: string, directory: boolean): boolean => {
    let ignored = false
    for (const rule of rules) {
      if (rule.directoryOnly && !directory) continue
      if (rule.base && !path.startsWith(rule.base)) continue
      if (rule.pattern.test(path.slice(rule.base.length))) ignored = !rule.negate
    }
    return ignored
  }

  return (path, directory = false) => {
    const segments = path.split('/')
    for (let i = 1; i < segments.length; i++) {
      if (matches(segments.slice(0, i).join('/'), true)) return true
    }
    return matches(path, directory)
  }
}

function baseOf(path: string): string {
  const slash = path.lastIndexOf('/')
  return slash < 0 ? '' : path.slice(0, slash + 1)
}

function parseRules(text: string, base: string): Rule[] {
  const rules: Rule[] = []
  for (const raw of text.split(/\r?\n/)) {
    let line = trimTrailingSpaces(raw)
    if (line === '' || line.startsWith('#')) continue
    let negate = false
    if (line.startsWith('!')) {
      negate = true
      line = line.slice(1)
    } else if (line.startsWith('\\!') || line.startsWith('\\#')) {
      line = line.slice(1)
    }
    let directoryOnly = false
    if (line.endsWith('/') && !line.endsWith('\\/')) {
      directoryOnly = true
      line = line.slice(0, -1)
    }
    if (line === '') continue
    // A slash at the start or in the middle ties the pattern to the folder of the rule file.
    const anchored = line.includes('/')
    if (line.startsWith('/')) line = line.slice(1)
    const body = toExpression(line)
    if (body === null) continue
    rules.push({
      base,
      pattern: new RegExp(`^${anchored ? '' : '(?:.*/)?'}${body}$`),
      negate,
      directoryOnly,
    })
  }
  return rules
}

/** Trailing spaces are dropped unless a backslash escapes them. */
function trimTrailingSpaces(line: string): string {
  let end = line.length
  while (end > 0 && line[end - 1] === ' ' && line[end - 2] !== '\\') end--
  return line.slice(0, end)
}

/** The pattern as a regular expression body. Null for a pattern git cannot use, such as an unclosed `[`. */
function toExpression(pattern: string): string | null {
  let out = ''
  for (let i = 0; i < pattern.length; i++) {
    const c = pattern[i]
    if (c === '*') {
      if (pattern[i + 1] === '*') {
        const atStart = i === 0 || pattern[i - 1] === '/'
        const atEnd = i + 2 === pattern.length
        if (atStart && pattern[i + 2] === '/') {
          // `**/` is any number of folders, none included.
          out += '(?:.*/)?'
          i += 2
          continue
        }
        if (atStart && atEnd) {
          // A trailing `/**` is everything inside.
          out += '.*'
          i += 1
          continue
        }
        // Any other `**` is an ordinary `*`.
        while (pattern[i + 1] === '*') i++
      }
      out += '[^/]*'
    } else if (c === '?') {
      out += '[^/]'
    } else if (c === '[') {
      const close = pattern.indexOf(']', i + 2)
      if (close < 0) return null
      let set = pattern.slice(i + 1, close)
      if (set.startsWith('!')) set = '^' + set.slice(1)
      out += `[${set.replace(/\\/g, '\\\\')}]`
      i = close
    } else if (c === '\\' && i + 1 < pattern.length) {
      out += escape(pattern[++i])
    } else {
      out += escape(c)
    }
  }
  return out
}

function escape(c: string): string {
  return c.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')
}
