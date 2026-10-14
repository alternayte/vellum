import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { HeadersField } from '../../../src/components/trace/scenario-steps'

function Harness({ initial }: { initial: Record<string, string> }) {
  const [headers, setHeaders] = useState(initial)
  return (
    <>
      <HeadersField headers={headers} onChange={setHeaders} />
      <output data-testid="headers">{JSON.stringify(headers)}</output>
    </>
  )
}

describe('HeadersField', () => {
  // The defect: rows were keyed by position and the name input keeps its own text, so after
  // header a was removed the row of b showed "a", and leaving it renamed b to a.
  it('keeps each row on its own header when an earlier one is removed', () => {
    render(<Harness initial={{ a: '1', b: '2' }} />)
    fireEvent.click(screen.getByRole('button', { name: 'Remove header a' }))

    const name = screen.getByLabelText('Header 1 name') as HTMLInputElement
    expect(name.value).toBe('b')
    fireEvent.blur(name)
    expect(JSON.parse(screen.getByTestId('headers').textContent!)).toEqual({ b: '2' })
  })
})
