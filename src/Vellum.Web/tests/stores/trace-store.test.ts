import { describe, it, expect, beforeEach } from 'vitest'
import { currentTraceEvent, useTraceStore } from '../../src/stores/trace-store'
import { describeEvent, formatVirtualTime, reachableChannels, type TraceEvent, type TraceRun } from '../../src/lib/trace'

const event = (sequence: number, kind: TraceEvent['kind'], extra: Partial<TraceEvent> = {}): TraceEvent => ({
  sequence, atMs: 0, kind, stepId: 's', attempt: 1, from: 'a', to: 'b', target: 'op', payload: null, detail: null, message: null, headers: null, ...extra,
})

const run: TraceRun = {
  engineVersion: '1',
  hash: 'h',
  issues: [],
  events: [event(1, 'RequestSent'), event(2, 'DataWritten'), event(3, 'ResponseReceived')],
  outcomes: {},
  assertions: [],
  labels: { a: 'Customer', b: 'Orders', op: 'POST /orders' },
}

beforeEach(() => useTraceStore.getState().load(run))

describe('trace store', () => {
  it('starts before the first event', () => {
    expect(useTraceStore.getState().cursor).toBe(-1)
    expect(currentTraceEvent(useTraceStore.getState())).toBeNull()
  })

  it('stops automatic playback on the last event', () => {
    const s = useTraceStore.getState()
    s.play()
    s.step(1)
    s.step(1)
    expect(useTraceStore.getState().playing).toBe(true)
    s.step(1)
    expect(useTraceStore.getState()).toMatchObject({ cursor: 2, playing: false })
    s.step(1)
    expect(useTraceStore.getState().cursor).toBe(2)
  })

  it('plays again from the start when play is pressed at the end', () => {
    const s = useTraceStore.getState()
    s.seek(2)
    s.play()
    expect(useTraceStore.getState()).toMatchObject({ cursor: -1, playing: true })
  })

  it('clamps seeking and pauses', () => {
    const s = useTraceStore.getState()
    s.play()
    s.seek(99)
    expect(useTraceStore.getState()).toMatchObject({ cursor: 2, playing: false })
    s.seek(-5)
    expect(useTraceStore.getState().cursor).toBe(-1)
  })
})

describe('trace wording', () => {
  it('names the parts of a request and its retry attempt', () => {
    expect(describeEvent(event(1, 'RequestSent', { attempt: 2 }), run.labels)).toBe('Customer calls POST /orders on Orders (attempt 2)')
  })

  it('says a dropped message is lost', () => {
    expect(describeEvent(event(1, 'Dropped', { from: 'b' }), run.labels)).toBe('The message for Orders is lost')
  })

  it('says which channel routes a message to which, and why a binding was not followed', () => {
    const labels = { x: 'order-events', q: 'billing.order-created', m: 'OrderCreated' }
    const binding = '00000000-0000-0000-0000-000000000080'
    const routing = { from: 'x', to: 'q', target: binding, message: 'm' }

    expect(describeEvent(event(1, 'Routed', { ...routing, detail: `Binding ${binding} has no routing key.` }), labels))
      .toBe('order-events routes OrderCreated to billing.order-created')
    expect(describeEvent(event(2, 'NotRouted', { ...routing, detail: `Binding ${binding} needs routing key 'order.created'; the step states none.` }), labels))
      .toBe("order-events does not route OrderCreated to billing.order-created: the binding needs routing key 'order.created'; the step states none")
    expect(describeEvent(event(3, 'NotRouted', { ...routing, detail: `Binding ${binding}: routing key 'a' does not match 'b.*'.` }), labels))
      .toBe("order-events does not route OrderCreated to billing.order-created: routing key 'a' does not match 'b.*'")
  })

  it('finds every channel a publish reaches through bindings, once, with a cycle', () => {
    const bindings = [
      { sourceChannelId: 'x', targetChannelId: 'relay' },
      { sourceChannelId: 'relay', targetChannelId: 'q' },
      { sourceChannelId: 'relay', targetChannelId: 'x' },
      { sourceChannelId: 'other', targetChannelId: 'z' },
    ]
    expect(reachableChannels('x', bindings)).toEqual(['x', 'relay', 'q'])
    expect(reachableChannels('q', bindings)).toEqual(['q'])
  })

  it('formats virtual time', () => {
    expect([formatVirtualTime(250), formatVirtualTime(1000), formatVirtualTime(1250)]).toEqual(['250 ms', '1 s', '1.25 s'])
  })
})
