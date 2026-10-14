import { describe, expect, it } from 'vitest'
import { allStepIds, finalBefore, newStep, nextStepId, readBody, renameStep, type CallStep, type ScenarioBody } from '../../src/lib/scenario-document'

describe('the scenario document of the step editor', () => {
  // The server refuses a step without its lists ("malformed-scenario"); a hand-written or
  // AI-written document can leave them out, and a save from the editor must not.
  it('reads a partial document into complete steps and keeps every value', () => {
    const { body, unknown } = readBody({
      steps: [
        { type: 'call', id: 'place', from: 'web', operation: 'post-orders', request: { sku: 'a' } },
        { type: 'publish', id: 'announce', from: 'api', channel: 'events', message: 'created', routingKey: 'order.created', handlers: { billing: [{ type: 'write', id: 'w', by: 'b', store: 'db', dataset: 'x' }] } },
      ],
      faults: [{ target: 'post-orders', kind: 'Timeout', failFirst: null }],
      assertions: [{ type: 'event-count', id: 'dlq', kind: 'DeadLettered', target: null, expected: 1 }],
    })
    expect(unknown).toEqual([])
    expect(body.steps[0]).toEqual({ type: 'call', id: 'place', from: 'web', operation: 'post-orders', request: { sku: 'a' }, response: null, then: [], onError: [] })
    expect(body.steps[1]).toMatchObject({ headers: {}, payload: null, routingKey: 'order.created', handlers: { billing: [{ type: 'write', change: null }] } })
    expect(body.faults).toEqual([{ target: 'post-orders', kind: 'Timeout', failFirst: null }])
    expect(body.assertions[0]).toEqual({ type: 'event-count', id: 'dlq', kind: 'DeadLettered', target: null, expected: 1 })
  })

  it('names a step type it does not know instead of dropping it silently', () => {
    const { unknown } = readBody({ steps: [{ type: 'consume', id: 'c' }], faults: [], assertions: [{ type: 'latency', id: 'l' }] })
    expect(unknown).toEqual(['step c of type "consume"', 'assertion l of type "latency"'])
  })

  // The branch picker offers only these; the engine rejects any other with "unknown-step".
  it('offers a branch only the steps whose outcome is final, as the engine counts them', () => {
    const steps = readBody({
      steps: [
        { type: 'call', id: 'place', then: [{ type: 'call', id: 'charge', onError: [{ type: 'write', id: 'undo' }] }] },
        { type: 'publish', id: 'announce', handlers: { sub: [{ type: 'write', id: 'consumer-write' }] } },
        { type: 'branch', id: 'check', whenStep: 'charge', is: 'Failed' },
      ],
    }).body.steps
    expect(finalBefore(steps, [])).toEqual([
      [],
      ['place', 'charge', 'undo'],
      ['place', 'charge', 'undo', 'announce'],
    ])
  })

  it('renames a step where branches and expected results name it', () => {
    const body: ScenarioBody = {
      steps: [
        { ...(newStep('call', 'call-1') as CallStep), then: [newStep('write', 'write-1')] },
        { type: 'branch', id: 'branch-1', whenStep: 'write-1', is: 'Failed', then: [], else: [] },
      ],
      faults: [],
      assertions: [{ type: 'outcome', id: 'expect-1', step: 'write-1', expected: 'Succeeded' }],
    }
    const renamed = renameStep(body, 'write-1', 'save-order')
    expect(allStepIds(renamed.steps)).toEqual(['call-1', 'save-order', 'branch-1'])
    expect(renamed.steps[1]).toMatchObject({ whenStep: 'save-order' })
    expect(renamed.assertions[0]).toMatchObject({ step: 'save-order' })
    expect(nextStepId('call', allStepIds(renamed.steps))).toBe('call-2')
  })
})
