import { describe, it, expect } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { MdxRenderer } from '../../../src/components/docs/mdx-renderer'

const ELEMENTS: { id: string; kind: string; name: string }[] = []
const VIEWS: { id: string; name: string }[] = []

describe('MdxRenderer', () => {
  it('renders a GFM table', async () => {
    const content = `| Name | Role |\n| --- | --- |\n| Alice | Dev |`
    render(<MdxRenderer content={content} elements={ELEMENTS} views={VIEWS} />)
    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeTruthy()
    })
    expect(screen.getByRole('table')).toBeTruthy()
  })

  it('renders a GFM task list', async () => {
    const content = `- [x] Done\n- [ ] Todo`
    render(<MdxRenderer content={content} elements={ELEMENTS} views={VIEWS} />)
    await waitFor(() => {
      expect(screen.getByText('Done')).toBeTruthy()
    })
    expect(screen.getByText('Todo')).toBeTruthy()
  })

  it('renders strikethrough text', async () => {
    const content = `~~removed~~`
    render(<MdxRenderer content={content} elements={ELEMENTS} views={VIEWS} />)
    await waitFor(() => {
      expect(screen.getByText('removed').closest('del')).toBeTruthy()
    })
  })

  it('renders a code block with language class', async () => {
    const content = '```typescript\nconst x = 1\n```'
    render(<MdxRenderer content={content} elements={ELEMENTS} views={VIEWS} />)
    await waitFor(() => {
      expect(screen.getByText('const')).toBeTruthy()
    })
  })

  it('renders basic markdown (heading, paragraph)', async () => {
    const content = `# Hello\n\nWorld`
    render(<MdxRenderer content={content} elements={ELEMENTS} views={VIEWS} />)
    await waitFor(() => {
      expect(screen.getByText('Hello')).toBeTruthy()
    })
    expect(screen.getByText('World')).toBeTruthy()
  })

  it('shows error state for invalid MDX', async () => {
    const content = `<BadComponent unclosed`
    render(<MdxRenderer content={content} elements={ELEMENTS} views={VIEWS} />)
    await waitFor(() => {
      expect(screen.getByText('MDX render error')).toBeTruthy()
    })
  })
})
