import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/lib/settings-api'

/** What a thread is about, by stable ID. Never a canvas position. */
export type TargetKind =
  | 'project' | 'element' | 'relationship' | 'message' | 'operation' | 'channel'
  | 'step' | 'promise' | 'document' | 'decision'

export type ThreadStatus = 'open' | 'resolved' | 'detached'

export interface ThreadTarget {
  kind: TargetKind
  id: string
  stepId: string | null
  /** The current name, or the last known name when the thread is detached. */
  name: string
}

export interface ThreadComment {
  id: string
  threadId: string
  author: string
  authorName: string | null
  /** Null once deleted: the comment stays as a tombstone. */
  body: string | null
  mentions: string[]
  createdAt: string
  editedAt: string | null
  deleted: boolean
  deletedAt: string | null
}

export interface Thread {
  id: string
  projectId: string
  draftId: string | null
  target: ThreadTarget
  modelVersion: number
  /** The model stream that `modelVersion` counts: main, or the draft the thread was made on. */
  modelStreamId: string
  status: ThreadStatus
  createdBy: string
  createdAt: string
  resolvedBy: string | null
  resolvedAt: string | null
  raisedQuestionId: string | null
  raisedDecisionId: string | null
  comments: ThreadComment[]
}

export interface InboxItem {
  id: string
  reason: 'mention' | 'reply'
  workspaceId: string
  projectId: string
  projectName: string
  draftId: string | null
  threadId: string
  commentId: string
  target: ThreadTarget
  author: string
  authorName: string | null
  excerpt: string | null
  createdAt: string
  readAt: string | null
}

export interface Inbox {
  unread: number
  items: InboxItem[]
}

const threadsUrl = (projectId: string) => `/api/projects/${projectId}/threads`

/** Every thread query of a project, on any branch. */
const threadsKey = (projectId: string) => ['projects', projectId, 'threads']

/** The threads of one branch: main when `draftId` is null. */
export function useThreads(projectId: string, draftId: string | null, enabled = true) {
  return useQuery({
    queryKey: [...threadsKey(projectId), draftId ?? 'main'],
    queryFn: async () =>
      (await api<{ items: Thread[] }>('GET', `${threadsUrl(projectId)}${draftId ? `?draftId=${draftId}` : ''}`)).items,
    enabled,
    staleTime: 15_000,
  })
}

/** The mutations of threads. Each one reads the threads and the inbox again when it is done. */
export function useThreadActions(projectId: string, draftId: string | null) {
  const queryClient = useQueryClient()
  const settled = () => {
    queryClient.invalidateQueries({ queryKey: threadsKey(projectId) })
    queryClient.invalidateQueries({ queryKey: ['inbox'] })
  }
  const url = threadsUrl(projectId)

  return {
    create: useMutation({
      mutationFn: (input: { target: { kind: TargetKind; id: string; stepId?: string | null }; body: string; mentions: string[] }) =>
        api<Thread>('POST', url, { id: crypto.randomUUID(), draftId, ...input }),
      onSettled: settled,
    }),
    reply: useMutation({
      mutationFn: ({ threadId, body, mentions }: { threadId: string; body: string; mentions: string[] }) =>
        api<ThreadComment>('POST', `${url}/${threadId}/replies`, { id: crypto.randomUUID(), body, mentions }),
      onSettled: settled,
    }),
    edit: useMutation({
      mutationFn: ({ threadId, commentId, body, mentions }: { threadId: string; commentId: string; body: string; mentions: string[] }) =>
        api<ThreadComment>('PATCH', `${url}/${threadId}/comments/${commentId}`, { body, mentions }),
      onSettled: settled,
    }),
    remove: useMutation({
      mutationFn: ({ threadId, commentId }: { threadId: string; commentId: string }) =>
        api<void>('DELETE', `${url}/${threadId}/comments/${commentId}`),
      onSettled: settled,
    }),
    setStatus: useMutation({
      mutationFn: ({ threadId, resolved }: { threadId: string; resolved: boolean }) =>
        api<Thread>('POST', `${url}/${threadId}/${resolved ? 'resolve' : 'reopen'}`),
      onSettled: settled,
    }),
    raiseQuestion: useMutation({
      mutationFn: (threadId: string) => api<{ id: string }>('POST', `${url}/${threadId}/raise-question`),
      onSettled: () => {
        settled()
        queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'questions'] })
      },
    }),
    raiseDecision: useMutation({
      mutationFn: ({ threadId, title, decision }: { threadId: string; title: string; decision: string }) =>
        api<{ id: string; number: string }>('POST', `${url}/${threadId}/raise-decision`, { title, decision }),
      onSettled: () => {
        settled()
        queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'decisions'] })
      },
    }),
  }
}

/** The inbox of the signed-in person, across their workspaces. */
export function useInbox() {
  return useQuery({
    queryKey: ['inbox'],
    queryFn: () => api<Inbox>('GET', '/api/inbox'),
    refetchInterval: 60_000,
    staleTime: 15_000,
  })
}

export function useInboxActions() {
  const queryClient = useQueryClient()
  const settled = () => queryClient.invalidateQueries({ queryKey: ['inbox'] })
  return {
    markRead: useMutation({ mutationFn: (itemId: string) => api<void>('POST', `/api/inbox/${itemId}/read`), onSettled: settled }),
    markAllRead: useMutation({ mutationFn: () => api<void>('POST', '/api/inbox/read-all'), onSettled: settled }),
  }
}

/** The open threads of each target ID, for the badges on the canvas: how many, and the first one. */
export function openThreadMarkers(threads: Thread[] | undefined): Record<string, { count: number; threadId: string }> {
  const markers: Record<string, { count: number; threadId: string }> = {}
  for (const thread of threads ?? []) {
    if (thread.status !== 'open') continue
    const current = markers[thread.target.id]
    markers[thread.target.id] = current ? { ...current, count: current.count + 1 } : { count: 1, threadId: thread.id }
  }
  return markers
}

/** The key of one target for counts: the kind, the ID and, for a flow step, the step ID. */
export function targetKey(kind: TargetKind, id: string, stepId?: string | null): string {
  return kind === 'step' ? `step:${id}:${stepId ?? ''}` : `${kind}:${id}`
}

/** The open threads of each target, by `targetKey`, for the counts beside listed items. */
export function openThreadCounts(threads: Thread[] | undefined): Record<string, number> {
  const counts: Record<string, number> = {}
  for (const thread of threads ?? []) {
    if (thread.status !== 'open') continue
    const key = targetKey(thread.target.kind, thread.target.id, thread.target.stepId)
    counts[key] = (counts[key] ?? 0) + 1
  }
  return counts
}
