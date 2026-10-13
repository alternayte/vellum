import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '@testing-library/react'
import { renderPage, type PageComponents } from './page-tree'

const components: PageComponents = {
  ElementLink: ({ id }) => <span data-testid="element-link">{id}</span>,
  LiveView: ({ id }) => <span data-testid="live-view">{id}</span>,
}

const marker = globalThis as { pagePwned?: number }

describe('renderPage', () => {
  afterEach(() => {
    cleanup()
    delete marker.pagePwned
  })

  it('renders a page as a document and runs nothing that the page holds', async () => {
    const page = await renderPage(
      [
        '# Orders',
        '',
        'See <ElementLink id="e-1" /> and the view below.',
        '',
        '<LiveView id="v-1" />',
        '',
        '{globalThis.pagePwned = 1}',
        '',
        'Text {globalThis.pagePwned = 2} in a line.',
        '',
        '<img src="x.png" alt="pic" onError={() => { globalThis.pagePwned = 3 }} />',
        '',
        '<ElementLink id={(globalThis.pagePwned = 4, "e-2")} />',
        '',
        '<a href="javascript:globalThis.pagePwned = 5">bad link</a> and [bad too](javascript:void(0)) and [good](https://example.com/a).',
        '',
        '<script>globalThis.pagePwned = 6</script>',
        '',
        '<iframe src="https://example.com"></iframe>',
        '',
        '| a | b |',
        '|---|---|',
        '| 1 | 2 |',
      ].join('\n'),
      components,
    )
    const { container, getAllByTestId, getByTestId } = render(<>{page}</>)

    expect(marker.pagePwned).toBeUndefined()
    expect(container.querySelector('h1')?.textContent).toBe('Orders')
    expect(container.querySelector('table')).not.toBeNull()
    // The first link has its literal id. The one whose id was an expression has none.
    expect(getAllByTestId('element-link').map((e) => e.textContent)).toEqual(['e-1', ''])
    expect(getByTestId('live-view').textContent).toBe('v-1')

    expect(container.querySelector('script')).toBeNull()
    expect(container.querySelector('iframe')).toBeNull()
    const image = container.querySelector('img')
    expect(image?.getAttribute('alt')).toBe('pic')
    expect(image?.getAttributeNames().some((name) => name.toLowerCase().startsWith('on'))).toBe(false)
    const links = [...container.querySelectorAll('a')]
    expect(links.map((a) => a.getAttribute('href'))).toEqual([null, null, 'https://example.com/a'])
    expect(container.textContent).not.toContain('pagePwned')
  })
})
