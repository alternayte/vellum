import { describe, expect, it } from 'vitest'
import { ApiError, apiFieldErrors, formFailure } from '../../src/hooks/use-contracts'

describe('formFailure', () => {
  it('puts the reason under the field the form shows', () => {
    const failure = formFailure(new ApiError('Content is not valid JSON', { content: 'Content is not valid JSON' }), ['name', 'content'])
    expect(failure).toEqual({ fields: { content: 'Content is not valid JSON' }, message: null })
  })

  it('keeps the reason for a field the form does not show as the reason of the form', () => {
    const failure = formFailure(new ApiError('An ID is required', { id: 'An ID is required' }), ['name', 'content'])
    expect(failure).toEqual({ fields: {}, message: 'An ID is required' })
  })

  it('keeps a refusal that names no field, such as a schema in use', () => {
    const failure = formFailure(new ApiError('Schema cannot be deleted.'), ['name'])
    expect(failure).toEqual({ fields: {}, message: 'Schema cannot be deleted.' })
  })
})

describe('apiFieldErrors', () => {
  it('reads the first reason of each field from the error envelope', () => {
    const envelope = {
      type: 'validation_error',
      title: 'Validation failed',
      errors: [
        { field: 'name', message: 'Name is required' },
        { field: 'name', message: 'Second reason' },
        { field: 'content', message: 'Content is not valid JSON' },
      ],
    }
    expect(apiFieldErrors(envelope)).toEqual({ name: 'Name is required', content: 'Content is not valid JSON' })
  })

  it('reads none from a refusal without field errors', () => {
    expect(apiFieldErrors({ type: 'conflict', title: 'Schema cannot be deleted.' })).toEqual({})
    expect(apiFieldErrors(undefined)).toEqual({})
  })
})
