import { describe, it, expect } from 'vitest'
import { filterMatchingNodes } from '../../../src/components/canvas/search-overlay'

const nodes = [
  { id: '1', data: { name: 'Payment API', description: 'Handles payments' } },
  { id: '2', data: { name: 'User Service', description: null } },
  { id: '3', data: { name: 'Database', description: 'PostgreSQL store' } },
  { id: 'msg-4', data: { name: 'Event' } },
]

describe('filterMatchingNodes', () => {
  it('returns empty array for empty query', () => {
    expect(filterMatchingNodes(nodes, '')).toEqual([])
  })

  it('matches by name case-insensitive', () => {
    expect(filterMatchingNodes(nodes, 'pay')).toEqual(['1'])
  })

  it('matches by description', () => {
    expect(filterMatchingNodes(nodes, 'postgres')).toEqual(['3'])
  })

  it('matches multiple nodes', () => {
    expect(filterMatchingNodes(nodes, 'se')).toEqual(['2'])
  })

  it('excludes message nodes', () => {
    expect(filterMatchingNodes(nodes, 'event')).toEqual([])
  })

  it('matches substring anywhere in name', () => {
    expect(filterMatchingNodes(nodes, 'api')).toEqual(['1'])
  })
})
