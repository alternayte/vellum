import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { ReactFlowProvider } from '@xyflow/react'
import { TruthLegend } from '../../../src/components/canvas/truth-legend'

function renderLegend(overrides: Partial<Parameters<typeof TruthLegend>[0]> = {}) {
  return render(
    <ReactFlowProvider>
      <TruthLegend
        factStatuses={['declared', 'declared', 'inferred']}
        relationshipKinds={['structural', 'behavioural']}
        onClose={vi.fn()}
        {...overrides}
      />
    </ReactFlowProvider>,
  )
}

describe('TruthLegend', () => {
  it('counts fact statuses and relationship kinds', () => {
    renderLegend()
    expect(screen.getByText('Declared')).toBeTruthy()
    expect(screen.getByText('Inferred')).toBeTruthy()
    expect(screen.getByText('Structural')).toBeTruthy()
    expect(screen.getByText('Behavioural')).toBeTruthy()
    expect(screen.getByText('2')).toBeTruthy()
    expect(screen.getAllByText('1')).toHaveLength(3)
  })

  it('omits statuses with no facts', () => {
    renderLegend()
    expect(screen.queryByText('Measured')).toBeNull()
    expect(screen.queryByText('Unknown')).toBeNull()
  })

  it('closes on the close button', () => {
    const onClose = vi.fn()
    renderLegend({ onClose })
    fireEvent.click(screen.getByLabelText('Close truth labels'))
    expect(onClose).toHaveBeenCalled()
  })
})
