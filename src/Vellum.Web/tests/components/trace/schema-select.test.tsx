import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { SchemaSelect } from '../../../src/components/trace/contract-parts'

const schemas = [{ id: 's-1', name: 'OrderRequest', version: 1 }, { id: 's-2', name: 'ChargeRequest', version: 2 }]

const formValue = () => new FormData(screen.getByRole('combobox').closest('form')!).get('requestSchemaId')

describe('SchemaSelect', () => {
  // The defect: the select was given its default value before the schema list loaded, showed
  // "None" and submitted "None", which removed the operation's schema.
  it('keeps the chosen schema when the schema list loads after the form', () => {
    const { rerender } = render(<form><SchemaSelect name="requestSchemaId" initial="s-2" schemas={[]} loading /></form>)
    expect(formValue()).toBe('s-2')
    expect(screen.getByRole('option', { name: 'Loading the schema…' })).toBeTruthy()

    rerender(<form><SchemaSelect name="requestSchemaId" initial="s-2" schemas={schemas} /></form>)
    expect(formValue()).toBe('s-2')
    expect((screen.getByRole('combobox') as HTMLSelectElement).selectedOptions[0].textContent).toBe('ChargeRequest · v2')
  })

  it('names a chosen schema that the list does not hold', () => {
    render(<form><SchemaSelect name="requestSchemaId" initial="gone" schemas={schemas} /></form>)
    expect(formValue()).toBe('gone')
    expect(screen.getByRole('option', { name: 'Schema not found' })).toBeTruthy()
  })
})
