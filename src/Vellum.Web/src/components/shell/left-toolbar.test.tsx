import { afterEach, describe, expect, it } from 'vitest'
import { act, cleanup, render, screen } from '@testing-library/react'
import { LeftToolbar } from './left-toolbar'
import { useShellStore } from '@/stores/shell-store'

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
    render(<LeftToolbar><button>Inside</button></LeftToolbar>)
    act(() => useShellStore.getState().openFlyout('files'))
    expect(screen.getByRole('region', { name: 'Project files panel' })).toBeTruthy()

    pressEscape(true)
    expect(useShellStore.getState().activeFlyout).toBe('files')

    const own = pressEscape(false)
    expect(useShellStore.getState().activeFlyout).toBeNull()
    // The flyout marks the key too, so the layer under it (the selection) stays.
    expect(own.defaultPrevented).toBe(true)
  })

  it('says which button has its flyout open', () => {
    render(<LeftToolbar><span /></LeftToolbar>)
    const files = screen.getByRole('button', { name: 'Project files' })
    expect(files.getAttribute('aria-expanded')).toBe('false')

    act(() => useShellStore.getState().openFlyout('files'))
    expect(files.getAttribute('aria-expanded')).toBe('true')
    expect(files.getAttribute('aria-controls')).toBe(screen.getByRole('region', { name: 'Project files panel' }).id)
  })
})
