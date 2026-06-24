import { describe, it, expect, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { TemplatePickerDialog } from '../../../src/components/docs/template-picker-dialog'

vi.mock('../../../src/lib/doc-templates', () => ({
  loadTemplates: vi.fn().mockResolvedValue([
    { id: 'adr', name: 'ADR', description: 'Architecture Decision Record', icon: 'scale', content: '# ADR' },
    { id: 'prd', name: 'PRD', description: 'Product Requirements Document', icon: 'file-text', content: '# PRD' },
    { id: 'sdd', name: 'SDD', description: 'System Design Document', icon: 'boxes', content: '# SDD' },
  ]),
}))

describe('TemplatePickerDialog', () => {
  it('renders template options when open', async () => {
    render(
      <TemplatePickerDialog
        open={true}
        onOpenChange={vi.fn()}
        onCreateDoc={vi.fn()}
      />
    )
    await waitFor(() => {
      expect(screen.getByText('ADR')).toBeTruthy()
    })
    expect(screen.getByText('PRD')).toBeTruthy()
    expect(screen.getByText('SDD')).toBeTruthy()
    expect(screen.getByText('Blank')).toBeTruthy()
  })

  it('renders nothing when closed', () => {
    const { container } = render(
      <TemplatePickerDialog
        open={false}
        onOpenChange={vi.fn()}
        onCreateDoc={vi.fn()}
      />
    )
    expect(container.querySelector('[role="dialog"]')).toBeNull()
  })
})
