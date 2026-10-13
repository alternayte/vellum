import { afterEach, describe, expect, it } from 'vitest'
import { isPlainShortcut, type ShortcutKeyEvent } from '../../src/lib/shortcuts'

function press(target: EventTarget | null, over: Partial<ShortcutKeyEvent> = {}): ShortcutKeyEvent {
  return { key: 'n', metaKey: false, ctrlKey: false, altKey: false, target, ...over }
}

function mount(html: string): HTMLElement {
  document.body.innerHTML = html
  return document.body.querySelector('[data-target]') as HTMLElement
}

describe('isPlainShortcut', () => {
  afterEach(() => { document.body.innerHTML = '' })

  it('runs on the page and on a button', () => {
    expect(isPlainShortcut(press(document.body))).toBe(true)
    expect(isPlainShortcut(press(mount('<button data-target>Go</button>')))).toBe(true)
  })

  it.each([
    ['an input', '<input data-target />'],
    ['a textarea', '<textarea data-target></textarea>'],
    ['a select', '<select data-target><option>a</option></select>'],
    ['an editable region', '<div contenteditable="true"><span data-target>x</span></div>'],
    ['an editable region without a value', '<div contenteditable data-target></div>'],
    ['a text box role', '<div role="textbox" data-target></div>'],
    ['a combo box role', '<div role="combobox" data-target></div>'],
  ])('does not run while the focus is in %s', (_name, html) => {
    expect(isPlainShortcut(press(mount(html)))).toBe(false)
  })

  it('does not run on a button of an open dialog or menu', () => {
    expect(isPlainShortcut(press(mount('<div data-slot="dialog-content"><button data-target>Add</button></div>')))).toBe(false)
    expect(isPlainShortcut(press(mount('<div role="menu"><button data-target>Delete</button></div>')))).toBe(false)
  })

  it('does not run with Ctrl, Cmd or Alt, so a browser or system chord stays what it is', () => {
    expect(isPlainShortcut(press(document.body, { ctrlKey: true }))).toBe(false)
    expect(isPlainShortcut(press(document.body, { metaKey: true }))).toBe(false)
    expect(isPlainShortcut(press(document.body, { altKey: true }))).toBe(false)
  })

  it('does not run on a held key, a key another handler used, or a key of an input method', () => {
    expect(isPlainShortcut(press(document.body, { repeat: true }))).toBe(false)
    expect(isPlainShortcut(press(document.body, { defaultPrevented: true }))).toBe(false)
    expect(isPlainShortcut(press(document.body, { isComposing: true }))).toBe(false)
  })
})
