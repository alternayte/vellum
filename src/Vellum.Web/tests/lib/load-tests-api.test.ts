import { describe, expect, it } from 'vitest'
import { lineOfMember } from '../../src/lib/load-tests-api'

/** A result file with two operations, as `JSON.stringify(result, null, 2)` writes it. */
const file = JSON.stringify({
  scriptHash: 'abc',
  note: 'a [bracket], a "quote" and a } in text',
  window: { warmUpMs: 1000, elapsedMs: 4000 },
  operations: [
    { operationId: 'first', requests: 10, latencyMs: { count: 10, p95: 12 } },
    { operationId: 'second', requests: 20, latencyMs: { count: 20, p95: 30 } },
  ],
  metrics: { vellum_op0_duration: { 'p(95)': 12 } },
}, null, 2)

const lineWith = (text: string, nth = 1) => {
  const lines = file.split('\n')
  let seen = 0
  return lines.findIndex((line) => line.includes(text) && ++seen === nth) + 1
}

describe('lineOfMember', () => {
  it('finds a member by the path that an import error names', () => {
    expect(lineOfMember(file, 'scriptHash')).toBe(lineWith('"scriptHash"'))
    expect(lineOfMember(file, 'window.elapsedMs')).toBe(lineWith('"elapsedMs"'))
    expect(lineOfMember(file, 'metrics.vellum_op0_duration.p(95)')).toBe(lineWith('"p(95)"'))
  })

  it('goes to the element that the index names, not to the first one', () => {
    expect(lineOfMember(file, 'operations[0].latencyMs.p95')).toBe(lineWith('"p95"', 1))
    expect(lineOfMember(file, 'operations[1].latencyMs.p95')).toBe(lineWith('"p95"', 2))
    expect(lineOfMember(file, 'operations[1].requests')).toBe(lineWith('"requests"', 2))
    expect(lineOfMember(file, 'operations[1]')).toBe(lineWith('"operationId": "second"') - 1)
  })

  it('gives the last member it found, and nothing for a member the text does not have', () => {
    expect(lineOfMember(file, 'window.missing')).toBe(lineWith('"window"'))
    expect(lineOfMember(file, 'operations[7].requests')).toBe(lineWith('"operations"'))
    expect(lineOfMember(file, 'absent')).toBeNull()
    expect(lineOfMember('not json', 'scriptHash')).toBeNull()
  })
})
