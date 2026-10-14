import { describe, expect, it } from 'vitest'
import { readableSegments } from '@/lib/readable-ids'

const SCENARIO = 'ed2fdc9e-5019-471c-8e0c-e1bdb5bf3dd6'
const SUBSCRIPTION = 'f93093eb-744b-45ec-a0c4-83750968cadd'
const STREAM = '42332a4f-1b99-4e6a-a6fb-7f8601fd914a'
const SCAN = 'b98ee46b-a8a5-4cfa-8456-b4e62441cf19'

const names: Record<string, string> = { [SUBSCRIPTION]: 'Billing on billing.order-created' }
const nameOf = (id: string) => names[id]
const render = (text: string) =>
  readableSegments(text, nameOf).map((s) => (s.kind === 'id' ? `[${s.text}]` : s.text)).join('')

describe('readableSegments', () => {
  it('drops an ID in brackets after a quoted name', () => {
    expect(render(`Play scenario 'Place an order' (${SCENARIO}) on a pinned model revision.`))
      .toBe("Play scenario 'Place an order' on a pinned model revision.")
  })

  it('puts the name in place of an ID with a description, without saying it twice', () => {
    expect(render(`Failure test for subscription ${SUBSCRIPTION} (Billing on billing.order-created). Run it.`))
      .toBe('Failure test for subscription Billing on billing.order-created. Run it.')
  })

  it('keeps the server description when the part is gone', () => {
    expect(render(`subscription 11111111-2222-3333-4444-555555555555 (Billing on billing.order-created)`))
      .toBe('subscription Billing on billing.order-created')
  })

  it('shows the short form of an ID that names nothing', () => {
    expect(render(`played on stream ${STREAM} at version 45`)).toBe('played on stream [42332a4f] at version 45')
  })

  it('reads an ID inside a bracket that holds more than a description', () => {
    expect(render(`commit d51a8709 (scan ${SCAN}, a tracking scan).`)).toBe('commit d51a8709 (scan [b98ee46b], a tracking scan).')
  })

  it('leaves a text without IDs as one segment', () => {
    expect(readableSegments('All 2 assertion(s) must hold.', nameOf)).toEqual([{ kind: 'text', text: 'All 2 assertion(s) must hold.' }])
  })
})
