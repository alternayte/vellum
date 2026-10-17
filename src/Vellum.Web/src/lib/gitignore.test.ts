import { describe, expect, it } from 'vitest'
import { createIgnoreTest } from './gitignore'

const root = (text: string) => createIgnoreTest([{ path: '.gitignore', text }])

describe('createIgnoreTest', () => {
  it('matches a name at any depth and a folder with everything in it', () => {
    const ignored = root('node_modules\n*.log\n# a comment\n\n')
    expect(ignored('node_modules/react/index.js')).toBe(true)
    expect(ignored('web/node_modules/react/index.js')).toBe(true)
    expect(ignored('logs/today.log')).toBe(true)
    expect(ignored('src/app.ts')).toBe(false)
    expect(ignored('# a comment')).toBe(false)
  })

  it('ties a pattern with a slash to the folder of its file', () => {
    const ignored = root('/build\ndocs/generated\n')
    expect(ignored('build/out.js')).toBe(true)
    expect(ignored('src/build/keep.go')).toBe(false)
    expect(ignored('docs/generated/a.json')).toBe(true)
    expect(ignored('web/docs/generated/a.json')).toBe(false)
  })

  it('applies a trailing slash to folders only', () => {
    const ignored = root('out/\n')
    expect(ignored('out/a.json')).toBe(true)
    expect(ignored('out')).toBe(false)
    expect(ignored('out', true)).toBe(true)
  })

  it('reads ** as any number of folders', () => {
    const ignored = root('**/fixtures\nsrc/**/gen/*.cs\nlib/**\n')
    expect(ignored('a/b/fixtures/x.json')).toBe(true)
    expect(ignored('src/gen/a.cs')).toBe(true)
    expect(ignored('src/x/y/gen/a.cs')).toBe(true)
    expect(ignored('src/x/gen/sub/a.cs')).toBe(false)
    expect(ignored('lib/deep/file.go')).toBe(true)
    expect(ignored('lib')).toBe(false)
  })

  it('brings a file back with ! unless its folder is ignored', () => {
    const ignored = root('*.json\n!package.json\nsecret/\n!secret/keep.json\n')
    expect(ignored('a.json')).toBe(true)
    expect(ignored('web/package.json')).toBe(false)
    expect(ignored('secret/keep.json')).toBe(true)
  })

  it('lets a nested file override the root file for its own folder only', () => {
    const ignored = createIgnoreTest([
      { path: 'web/.gitignore', text: '!dist\nlocal.yaml\n' },
      { path: '.gitignore', text: 'dist\n' },
    ])
    expect(ignored('dist/a.js')).toBe(true)
    expect(ignored('web/dist/a.js')).toBe(false)
    expect(ignored('web/local.yaml')).toBe(true)
    expect(ignored('local.yaml')).toBe(false)
  })

  it('reads ?, character classes and escapes', () => {
    const ignored = root('file?.txt\n[Tt]emp/\n\\#notes\n\\!bang\nspace\\ \n')
    expect(ignored('file1.txt')).toBe(true)
    expect(ignored('file10.txt')).toBe(false)
    expect(ignored('Temp/a.go')).toBe(true)
    expect(ignored('temp/a.go')).toBe(true)
    expect(ignored('#notes')).toBe(true)
    expect(ignored('!bang')).toBe(true)
    expect(ignored('space ')).toBe(true)
  })
})
