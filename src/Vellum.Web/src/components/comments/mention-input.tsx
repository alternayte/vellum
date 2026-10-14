import { useId, useMemo, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import type { Member } from '@/lib/branches-api'
import { cn } from '@/lib/utils'

const nameOf = (m: Member) => m.displayName || m.email || `user ${m.userId.slice(0, 8)}`

/** The "@query" that ends the text before the caret, or null. */
function mentionQuery(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret)
  const match = /(^|\s)@([^\s@]{0,40})$/.exec(before)
  return match ? { start: caret - match[2].length - 1, query: match[2].toLowerCase() } : null
}

interface MentionInputProps {
  members: Member[]
  label: string
  placeholder: string
  submitLabel: string
  initial?: string
  initialMentions?: string[]
  pending?: boolean
  autoFocus?: boolean
  onSubmit: (body: string, mentions: string[]) => void
  onCancel?: () => void
}

/**
 * The text of a comment. Typing @ opens a list of the workspace members; a pick writes
 * "@Name" into the text, and the user IDs of the names still in the text go with the comment.
 */
export function MentionInput({
  members, label, placeholder, submitLabel, initial = '', initialMentions = [], pending, autoFocus, onSubmit, onCancel,
}: MentionInputProps) {
  const [body, setBody] = useState(initial)
  const [picked, setPicked] = useState<Map<string, string>>(
    () => new Map(initialMentions.flatMap((id) => {
      const member = members.find((m) => m.userId === id)
      return member ? [[nameOf(member), id] as [string, string]] : []
    })),
  )
  const [query, setQuery] = useState<{ start: number; query: string } | null>(null)
  const [active, setActive] = useState(0)
  const area = useRef<HTMLTextAreaElement>(null)
  const listId = useId()

  const options = useMemo(
    () => query
      ? members.filter((m) => nameOf(m).toLowerCase().includes(query.query) || (m.email ?? '').toLowerCase().includes(query.query)).slice(0, 8)
      : [],
    [members, query],
  )
  const open = query !== null && options.length > 0

  const read = (text: string, caret: number) => {
    setQuery(mentionQuery(text, caret))
    setActive(0)
  }

  const pick = (member: Member) => {
    if (!query || !area.current) return
    const caret = area.current.selectionStart
    const name = nameOf(member)
    const next = `${body.slice(0, query.start)}@${name} ${body.slice(caret)}`
    setBody(next)
    setPicked((current) => new Map(current).set(name, member.userId))
    setQuery(null)
    const at = query.start + name.length + 2
    requestAnimationFrame(() => {
      area.current?.focus()
      area.current?.setSelectionRange(at, at)
    })
  }

  const submit = () => {
    const trimmed = body.trim()
    if (!trimmed) return
    // A name the person removed from the text is no mention any more.
    const mentions = [...new Set([...picked].filter(([name]) => trimmed.includes(`@${name}`)).map(([, id]) => id))]
    onSubmit(trimmed, mentions)
    if (!onCancel) {
      setBody('')
      setPicked(new Map())
    }
  }

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (open) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault()
        setActive((i) => (i + (e.key === 'ArrowDown' ? 1 : options.length - 1)) % options.length)
        return
      }
      if (e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault()
        pick(options[active])
        return
      }
      if (e.key === 'Escape') {
        // The list closes; the panel under it stays.
        e.preventDefault()
        setQuery(null)
        return
      }
    }
    if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') {
      e.preventDefault()
      submit()
    } else if (e.key === 'Escape' && onCancel) {
      e.preventDefault()
      onCancel()
    }
  }

  return (
    <div className="relative space-y-1.5">
      <Textarea
        ref={area}
        aria-label={label}
        placeholder={placeholder}
        value={body}
        autoFocus={autoFocus}
        rows={2}
        role="combobox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-autocomplete="list"
        aria-activedescendant={open ? `${listId}-${active}` : undefined}
        onChange={(e) => {
          setBody(e.target.value)
          read(e.target.value, e.target.selectionStart)
        }}
        onClick={(e) => read(e.currentTarget.value, e.currentTarget.selectionStart)}
        onBlur={() => setQuery(null)}
        onKeyDown={onKeyDown}
        className="min-h-0 resize-none text-xs"
      />
      {open && (
        <ul id={listId} role="listbox" aria-label="Members to mention" className="absolute left-0 right-0 top-full z-50 mt-1 max-h-48 overflow-y-auto rounded-md border border-border bg-popover py-1 text-xs shadow-md">
          {options.map((m, i) => (
            <li
              key={m.userId}
              id={`${listId}-${i}`}
              role="option"
              aria-selected={i === active}
              // The pick runs before the blur of the text area closes the list.
              onMouseDown={(e) => { e.preventDefault(); pick(m) }}
              className={cn('flex cursor-pointer items-baseline gap-2 px-2 py-1', i === active && 'bg-accent text-accent-foreground')}
            >
              <span className="truncate">{nameOf(m)}</span>
              {m.email && m.displayName && <span className="truncate text-[10px] text-muted-foreground">{m.email}</span>}
            </li>
          ))}
        </ul>
      )}
      <div className="flex items-center gap-1">
        <Button size="sm" className="h-6 px-2 text-xs" onClick={submit} disabled={!body.trim() || pending}>
          {pending ? 'Sending…' : submitLabel}
        </Button>
        {onCancel && (
          <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={onCancel}>Cancel</Button>
        )}
        <span className="ml-auto text-[10px] text-muted-foreground">@ to mention · ⌘↵ to send</span>
      </div>
    </div>
  )
}
