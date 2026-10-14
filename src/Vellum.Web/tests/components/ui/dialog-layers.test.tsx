import { describe, expect, it } from 'vitest'
import { useState } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '../../../src/components/ui/dialog'

function Stacked() {
  const [inner, setInner] = useState(false)
  return (
    <Dialog open>
      <DialogContent>
        <DialogTitle>Lower</DialogTitle>
        <DialogDescription>The lower dialog</DialogDescription>
        <button type="button" onClick={() => setInner(true)}>Open upper</button>
        {inner && (
          <Dialog open onOpenChange={(open) => { if (!open) setInner(false) }}>
            <DialogContent>
              <DialogTitle>Upper</DialogTitle>
              <DialogDescription>The upper dialog</DialogDescription>
              <button type="button" onClick={() => setInner(false)}>Close upper</button>
            </DialogContent>
          </Dialog>
        )}
      </DialogContent>
    </Dialog>
  )
}

describe('stacked dialogs', () => {
  it('takes the lower dialog out of the accessibility tree while a higher one is open', () => {
    render(<Stacked />)
    const lower = screen.getByRole('dialog', { name: 'Lower' })
    expect(lower.inert).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Open upper' }))
    expect(lower.inert).toBe(true)
    expect(lower.closest('[aria-hidden="true"]')).not.toBeNull()
    expect(screen.getAllByRole('dialog').map((d) => d.querySelector('h2')?.textContent)).toEqual(['Upper'])

    fireEvent.click(screen.getByRole('button', { name: 'Close upper' }))
    expect(lower.inert).toBe(false)
    expect(lower.closest('[aria-hidden="true"]')).toBeNull()
    expect(screen.getByRole('dialog', { name: 'Lower' })).toBe(lower)
  })
})
