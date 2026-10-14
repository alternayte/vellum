import { MessageSquare } from 'lucide-react'
import { cn } from '@/lib/utils'

/** The number of open comment threads beside a listed item. Nothing when there is none. */
export function ThreadCount({ count, className }: { count: number; className?: string }) {
  if (count === 0) return null
  return (
    <span
      className={cn('inline-flex shrink-0 items-center gap-0.5 rounded-full border border-primary/50 px-1.5 text-[10px] text-primary', className)}
      title={`${count} open comment ${count === 1 ? 'thread' : 'threads'}`}
    >
      <MessageSquare aria-hidden="true" className="h-2.5 w-2.5" />
      {count}
      <span className="sr-only"> open comment {count === 1 ? 'thread' : 'threads'}</span>
    </span>
  )
}
