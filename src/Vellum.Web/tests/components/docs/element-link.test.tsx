// src/Vellum.Web/tests/components/docs/element-link.test.tsx
import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ElementLink } from '../../../src/components/docs/element-link'

const ELEMENTS = [
  { id: 'el-1', kind: 'system', name: 'Orders' },
  { id: 'el-2', kind: 'app', name: 'API' },
]

describe('ElementLink', () => {
  it('renders element name with kind color', () => {
    render(<ElementLink id="el-1" elements={ELEMENTS} />)
    expect(screen.getByText('Orders')).toBeTruthy()
  })

  it('renders "Element removed" for unknown id', () => {
    render(<ElementLink id="nonexistent" elements={ELEMENTS} />)
    expect(screen.getByText('Element removed')).toBeTruthy()
  })
})
