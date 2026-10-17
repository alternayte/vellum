import { useRef } from 'react'
import { useNavigate } from '@tanstack/react-router'
import { Bell } from 'lucide-react'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { useInbox, useInboxActions, type InboxItem } from '@/hooks/use-threads'
import { useShellStore, type InboxTab } from '@/stores/shell-store'
import { ScopeNote } from '@/components/branch/scope-note'
import { cn } from '@/lib/utils'
import { CommentsFlyout } from './comments-flyout'

const when = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })

const TABS: { id: InboxTab; label: string }[] = [
  { id: 'inbox', label: 'Inbox' },
  { id: 'comments', label: 'Comments' },
]

/**
 * The bell panel. Its Inbox tab lists the mentions of the signed-in person, the replies on
 * threads they started or replied to, and proposals that their agents submitted, across their
 * workspaces; an entry opens its thread in its project, and a proposal opens on the canvas of
 * its project. In a project, its Comments tab lists every comment thread of the model shown.
 */
export function InboxButton({ project }: { project?: { id: string; name: string } }) {
  const open = useShellStore((s) => s.inboxOpen)
  const setOpen = useShellStore((s) => s.setInboxOpen)
  const chosenTab = useShellStore((s) => s.inboxTab)
  const setTab = useShellStore((s) => s.setInboxTab)
  const tab: InboxTab = project ? chosenTab : 'inbox'
  const opened = useRef(false)
  const { data, error } = useInbox()
  const { markRead, markAllRead } = useInboxActions()
  const navigate = useNavigate()
  const unread = data?.unread ?? 0

  const openItem = (item: InboxItem) => {
    if (!item.readAt) markRead.mutate(item.id)
    opened.current = true
    setOpen(false)
    if (item.proposal) {
      navigate({
        to: '/projects/$projectId',
        params: { projectId: item.projectId },
        search: item.proposal.kind === 'architecture' ? { proposal: item.proposal.id } : { flowProposal: item.proposal.id },
      })
      return
    }
    navigate({
      to: '/projects/$projectId',
      params: { projectId: item.projectId },
      search: { thread: item.threadId ?? undefined, draft: item.draftId ?? undefined },
    })
  }

  // A thread's target opens on the canvas or as its document. The focus stays there.
  const showTarget = () => {
    opened.current = true
    setOpen(false)
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label={`${project ? 'Inbox and comments' : 'Inbox'}${unread > 0 ? `, ${unread} unread` : ''}`}
          title={project ? 'Inbox and comments' : 'Inbox'}
          className="relative flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary"
        >
          <Bell className="h-4 w-4" aria-hidden="true" />
          {unread > 0 && (
            <span aria-hidden="true" className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-[9px] font-semibold tabular-nums text-primary-foreground">
              {unread > 99 ? '99+' : unread}
            </span>
          )}
        </button>
      </PopoverTrigger>
      <PopoverContent
        align="end"
        className={cn('gap-0 p-0', project ? 'flex w-[min(380px,calc(100vw-1rem))] flex-col' : 'w-80')}
        // After an entry opens a thread, focus stays where the thread opened. Focus that went
        // back to the bell would leave the detail panel, which then closes.
        onCloseAutoFocus={(e) => {
          if (opened.current) e.preventDefault()
          opened.current = false
        }}
      >
        {project && (
          <div role="tablist" aria-label="Bell panel" className="flex gap-1 border-b border-border p-1.5">
            {TABS.map((t) => (
              <button
                key={t.id}
                type="button"
                role="tab"
                aria-selected={tab === t.id}
                onClick={() => setTab(t.id)}
                className={cn(
                  'rounded-md px-2 py-1 text-xs text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary',
                  tab === t.id && 'bg-muted font-medium text-foreground',
                )}
              >
                {t.label}
                {t.id === 'inbox' && unread > 0 && <span className="ml-1 tabular-nums opacity-70">{unread}</span>}
              </button>
            ))}
          </div>
        )}
        {tab === 'comments' && project ? (
          <div className="flex h-[min(560px,70dvh)] flex-col">
            <ScopeNote flyout="comments" />
            <div className="min-h-0 flex-1">
              <CommentsFlyout projectId={project.id} projectName={project.name} onShowTarget={showTarget} />
            </div>
          </div>
        ) : (
          <>
            <div className="flex items-center justify-between border-b border-border px-3 py-2">
              <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Inbox</span>
              <button
                type="button"
                className="text-[11px] text-muted-foreground hover:text-foreground disabled:opacity-50"
                disabled={unread === 0 || markAllRead.isPending}
                onClick={() => markAllRead.mutate()}
              >
                Mark all read
              </button>
            </div>
            {error ? (
              <p role="alert" className="px-3 py-3 text-xs text-destructive">{error.message}</p>
            ) : !data?.items.length ? (
              <p className="px-3 py-3 text-xs leading-relaxed text-muted-foreground">
                Nothing here yet. A mention of you, a reply on a thread you started or replied to, and a proposal that your agent submitted show here.
              </p>
            ) : (
              <ul className="max-h-96 divide-y divide-border overflow-y-auto">
                {data.items.map((item) => (
                  <li key={item.id}>
                    <button
                      type="button"
                      onClick={() => openItem(item)}
                      className={cn('flex w-full gap-2 px-3 py-2 text-left text-xs hover:bg-muted focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary', !item.readAt && 'bg-primary/5')}
                    >
                      <span aria-hidden="true" className={cn('mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full', item.readAt ? 'bg-transparent' : 'bg-primary')} />
                      <span className="min-w-0 flex-1">
                        {item.proposal ? (
                          <span className="block text-foreground">
                            <span className="font-medium">Agent {item.authorName ?? 'token'}</span>
                            {item.proposal.kind === 'architecture'
                              ? ` proposed ${item.proposal.items ?? ''} ${item.proposal.items === 1 ? 'addition' : 'additions'}: `
                              : ' proposed a flow: '}
                            {/* The name is text that the agent wrote. It is shown as text. */}
                            <span className="font-medium">{item.proposal.name}</span>
                            {!item.proposal.pending && <span className="text-muted-foreground"> (decided)</span>}
                            {!item.readAt && <span className="sr-only"> (unread)</span>}
                          </span>
                        ) : (
                          <span className="block text-foreground">
                            <span className="font-medium">{item.authorName ?? 'Someone'}</span>
                            {item.reason === 'mention' ? ' mentioned you on ' : ' replied on '}
                            <span className="font-medium">{item.target?.name}</span>
                            {!item.readAt && <span className="sr-only"> (unread)</span>}
                          </span>
                        )}
                        <span className="block truncate text-[11px] text-muted-foreground">
                          {item.projectName}{item.draftId ? ' · draft' : ''} · {when(item.createdAt)}
                        </span>
                        <span className="mt-0.5 line-clamp-2 block text-muted-foreground">
                          {item.excerpt ?? <em>Comment deleted.</em>}
                        </span>
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </PopoverContent>
    </Popover>
  )
}
