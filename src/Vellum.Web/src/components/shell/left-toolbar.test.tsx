import { afterEach, describe, expect, it } from 'vitest'
import { act, cleanup, render, screen } from '@testing-library/react'
import { LeftToolbar } from './left-toolbar'
import { useShellStore } from '@/stores/shell-store'

const ACCESS = { hasElements: true, canShare: true }

function pressEscape(handledAbove: boolean) {
  const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
  // A layer above the flyout (a dialog, a menu, an inline editor) marks the key as used.
  if (handledAbove) event.preventDefault()
  act(() => { window.dispatchEvent(event) })
  return event
}

describe('LeftToolbar', () => {
  afterEach(() => {
    cleanup()
    act(() => useShellStore.getState().closeFlyout())
  })

  it('keeps the flyout open when a layer above it used Escape, and closes it on the next Escape', () => {
    render(<LeftToolbar access={ACCESS}><button>Inside</button></LeftToolbar>)
    act(() => useShellStore.getState().openFlyout('files'))
    expect(screen.getByRole('region', { name: 'Model panel' })).toBeTruthy()

    pressEscape(true)
    expect(useShellStore.getState().activeFlyout).toBe('files')

    const own = pressEscape(false)
    expect(useShellStore.getState().activeFlyout).toBeNull()
    // The flyout marks the key too, so the layer under it (the selection) stays.
    expect(own.defaultPrevented).toBe(true)
  })

  it('says which area has its panel open', () => {
    render(<LeftToolbar access={ACCESS}><span /></LeftToolbar>)
    const model = screen.getByRole('button', { name: 'Model' })
    expect(model.getAttribute('aria-expanded')).toBe('false')

    act(() => useShellStore.getState().openFlyout('files'))
    expect(model.getAttribute('aria-expanded')).toBe('true')
    expect(model.getAttribute('aria-controls')).toBe(screen.getByRole('region', { name: 'Model panel' }).id)
  })
})
