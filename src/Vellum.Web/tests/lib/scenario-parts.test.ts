import { describe, it, expect } from 'vitest'
import { scenarioSteps, stepEnds } from '../../src/lib/scenario-parts'

describe('the parts a scenario names', () => {
  // A fault of a check is chosen from these, and "Show on canvas" rings them: a step that is missed is a part nobody can fault or see.
  it('finds nested steps, with the elements and the contracts each names', () => {
    const steps = scenarioSteps([
      {
        type: 'call', id: 'place', from: 'customer', operation: 'post-orders',
        then: [{ type: 'write', id: 'save', by: 'api', store: 'db' }],
        onError: [{ type: 'branch', id: 'check', whenStep: 'save', is: 'Failed', then: [], else: [{ type: 'write', id: 'undo', by: 'api', store: 'db' }] }],
      },
      { type: 'parallel', id: 'fan', branches: [[{ type: 'publish', id: 'publish', from: 'api', channel: 'events', message: 'created', handlers: { billing: [{ type: 'call', id: 'charge', from: 'worker', operation: 'pay' }] } }]] },
    ])
    expect(steps.map((s) => s.id)).toEqual(['place', 'save', 'check', 'undo', 'fan', 'publish', 'charge'])
    expect(steps.find((s) => s.id === 'place')).toMatchObject({ elementIds: ['customer'], contractIds: ['post-orders'], messageId: null })
    expect(steps.find((s) => s.id === 'publish')).toMatchObject({ elementIds: ['api'], contractIds: ['events', 'billing'], messageId: 'created' })
  })
})

describe('the ends of a step', () => {
  // The impact view draws a step from where it starts to what it reaches. A wrong end draws the step against the wrong service.
  it('starts in the caller, publisher or writer and reaches the owner of the operation, the consumers or the store', () => {
    const [call, write, publish] = scenarioSteps([
      { type: 'call', id: 'place', from: 'customer', operation: 'post-orders' },
      { type: 'write', id: 'save', by: 'api', store: 'db' },
      { type: 'publish', id: 'publish', from: 'api', channel: 'events', message: 'created', handlers: { 'billing-sub': [] } },
    ])
    const owners = { 'post-orders': 'api', 'billing-sub': 'billing' }
    expect(stepEnds(call, owners)).toEqual({ fromId: 'customer', toIds: ['api'] })
    expect(stepEnds(write, owners)).toEqual({ fromId: 'api', toIds: ['db'] })
    expect(stepEnds(publish, owners)).toEqual({ fromId: 'api', toIds: ['billing'] })
  })
})
