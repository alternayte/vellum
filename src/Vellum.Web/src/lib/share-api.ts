/** Typed access to share links: the member endpoints and the read API for visitors. */
import { api } from '@/lib/settings-api'
import type { LineVisibility } from './line-visibility'

export type ShareScope = 'project' | 'view'
export type ShareMode = 'live' | 'pinned'
export type LinkPolicy = 'off' | 'password-only' | 'public'

export interface ShareLink {
  id: string
  label: string
  scope: ShareScope
  viewId: string | null
  viewName: string | null
  mode: ShareMode
  revisionId: string | null
  revisionName: string | null
  revisionCreatedAt: string | null
  showCosts: boolean
  showPromises: boolean
  hasPassword: boolean
  expiresAt: string | null
  createdBy: string
  createdAt: string
  updatedAt: string
  revokedAt: string | null
  lastAccessedAt: string | null
  /** `blocked`: the workspace link policy does not allow this link now; it answers 404. */
  status: 'active' | 'expired' | 'revoked' | 'blocked'
  canManage: boolean
}

export interface ShareLinkCreated {
  link: ShareLink
  /** Shown once. The server keeps only its hash. */
  token: string
  path: string
  embedPath: string | null
}

export interface CreateShareLink {
  scope: ShareScope
  viewId: string | null
  mode: ShareMode
  revisionId: string | null
  label: string
  showCosts: boolean
  showPromises: boolean
  password: string | null
  expiresAt: string | null
}

export interface ChangeShareLink {
  label?: string
  showCosts?: boolean
  showPromises?: boolean
  password?: string
  removePassword?: boolean
  expiresAt?: string
  clearExpiry?: boolean
}

const links = (projectId: string) => `/api/projects/${projectId}/share-links`

export const shareLinksApi = {
  list: (projectId: string) => api<ShareLink[]>('GET', links(projectId)),
  create: (projectId: string, body: CreateShareLink) => api<ShareLinkCreated>('POST', links(projectId), body),
  change: (projectId: string, linkId: string, body: ChangeShareLink) =>
    api<ShareLink>('PATCH', `${links(projectId)}/${linkId}`, { removePassword: false, clearExpiry: false, ...body }),
  revoke: (projectId: string, linkId: string) => api<void>('POST', `${links(projectId)}/${linkId}/revoke`),
}

// The page a visitor reads. These types mirror SharePage.cs on the server.

export interface SharedElement {
  id: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  status: string
  parentId: string | null
  tags: string[]
  icon: string | null
  truthLabel: string
  /** `path:lines` only; never the text of the source. */
  citation: string | null
  stale: boolean
}

export interface SharedRelationship {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
  kind: string
  messageId: string | null
  lineShape: string | null
  truthLabel: string
  citation: string | null
  stale: boolean
}

export interface SharedMessage {
  id: string
  name: string
  description: string | null
  producerId: string
  consumerIds: string[]
  schemaId: string | null
  tags: string[]
  truthLabel: string
  citation: string | null
  stale: boolean
}

export interface SharedOperation {
  id: string
  ownerId: string
  name: string
  transport: string
  requestSchemaId: string | null
  responseSchemaId: string | null
  timeoutMs: number
  retry: { maxAttempts: number; backoffMs: number }
  truthLabel: string
  citation: string | null
  stale: boolean
}

export interface SharedChannel { id: string; name: string; kind: string; truthLabel: string; citation: string | null; stale: boolean }
export interface SharedSubscription {
  id: string
  channelId: string
  consumerId: string
  retry: { maxAttempts: number; backoffMs: number }
  deadLetterChannelId: string | null
  truthLabel: string
  citation: string | null
  stale: boolean
}

/** The path of a line from the auto layout: coordinates only. */
export interface SharedRoute {
  from: { x: number; y: number; width: number; height: number }
  to: { x: number; y: number; width: number; height: number }
  points: { x: number; y: number }[]
  label: { x: number; y: number } | null
}
export interface SharedView {
  id: string
  name: string
  positions: { elementId: string; x: number; y: number; width: number | null; height: number | null }[]
  /** The saved sides and routes of the relationships on the diagram. Null on a page pinned before sides existed. */
  edges?: { relationshipId: string; sourceSide: string | null; targetSide: string | null; route?: SharedRoute | null }[] | null
  /** The saved routes of the derived lines. Null on a page pinned before routes existed. */
  lines?: { fromId: string; toId: string; route: SharedRoute }[] | null
  /**
   * View link only: the relationships the link does not share, by pair of shared cards. A count
   * and, for one, its label: never the ID or the name of a hidden part.
   */
  hiddenLines?: { fromId: string; toId: string; label: string | null; count: number }[] | null
  /** Which lines the diagram draws, as the view saved it. Null when the view hides no line. */
  lineVisibility?: LineVisibility | null
  /** The cards a member hid from the diagram. Null on a page pinned before cards could be hidden. */
  hiddenElementIds?: string[] | null
}
export interface SharedSchema { id: string; name: string; description: string | null; content: string; version: number }
/** One step of a flow. Payloads, headers and write changes are not shared. */
export interface SharedStep {
  id: string
  type: 'call' | 'publish' | 'write' | 'parallel' | 'branch' | 'unknown'
  /** The element that calls, publishes or writes. */
  fromId: string | null
  /** The operation called, the message published or the store written. */
  targetId: string | null
  channelId: string | null
  dataset: string | null
  whenStep: string | null
  is: string | null
  groups: { name: 'then' | 'on-error' | 'else' | 'branch' | 'handler'; steps: SharedStep[] }[]
}
export interface SharedFlow { id: string; name: string; steps: SharedStep[]; faults: number; assertions: number }
export interface SharedDoc { id: string; title: string; type: string | null; elementId: string | null; space: string | null; content: string; updatedAt: string }
export interface SharedDecision {
  id: string
  label: string
  status: string
  title: string
  context: string
  decision: string
  alternatives: { title: string; whyNot: string | null }[]
  consequences: string | null
  affectedIds: string[]
  supersedes: string | null
  acceptedAt: string | null
}
export interface SharedCosts {
  asOf: string
  currency: string
  totals: { currency: string; amount: number }[]
  lines: { elementId: string; resource: string; name: string; category: string; provider: string; region: string; status: string; reason: string | null; amount: number | null; currency: string | null }[]
  unknownLines: number
}
export interface SharedPromise {
  requirementId: string
  revision: number
  title: string
  claim: string
  scope: string | null
  preconditions: string | null
  expectedOutcome: string | null
  forbiddenOutcome: string | null
  approvedAt: string | null
  counts: { passed: number; failed: number; notRun: number; unsupported: number; stale: number }
  obligations: { id: string; title: string; kind: string; result: string; current: boolean | null; at: string | null; environment: string | null }[]
}

export interface SharedPage {
  projectName: string
  scope: ShareScope
  mode: ShareMode
  revision: { name: string; createdAt: string } | null
  asOf: string
  model: {
    elements: SharedElement[]
    relationships: SharedRelationship[]
    messages: SharedMessage[]
    operations: SharedOperation[]
    channels: SharedChannel[]
    subscriptions: SharedSubscription[]
    bindings: { id: string; sourceChannelId: string; targetChannelId: string; routingKey: string | null; truthLabel: string }[]
  }
  views: SharedView[]
  schemas: SharedSchema[]
  flows: SharedFlow[]
  docs: SharedDoc[]
  decisions: SharedDecision[]
  costs: SharedCosts | null
  promises: SharedPromise[] | null
}

export type ShareRead =
  | { kind: 'page'; page: SharedPage }
  | { kind: 'password' }
  | { kind: 'gone' }
  | { kind: 'error'; message: string }

const GRANT_HEADER = 'X-Vellum-Share-Grant'
const grantKey = (token: string) => `vellum.share.grant.${token}`

/**
 * The proof of a typed password lives in the page and in session storage of this tab. It is
 * no cookie: inside the frame of another site a browser holds cookies back, and the app's own
 * session cookie stays as it is. Storage can be refused in a third-party frame; then the page
 * keeps the grant in memory only.
 */
const memoryGrants = new Map<string, string>()

export function storedGrant(token: string): string | null {
  try {
    return sessionStorage.getItem(grantKey(token)) ?? memoryGrants.get(token) ?? null
  } catch {
    return memoryGrants.get(token) ?? null
  }
}

function keepGrant(token: string, grant: string) {
  memoryGrants.set(token, grant)
  try {
    sessionStorage.setItem(grantKey(token), grant)
  } catch {
    // Storage is refused here: memory keeps it for this page.
  }
}

export async function readShare(token: string): Promise<ShareRead> {
  const grant = storedGrant(token)
  const response = await fetch(`/api/share/${encodeURIComponent(token)}`, {
    headers: grant ? { [GRANT_HEADER]: grant } : {},
    credentials: 'omit',
  })
  if (response.status === 404) return { kind: 'gone' }
  if (response.status === 401) return { kind: 'password' }
  if (!response.ok) return { kind: 'error', message: 'The shared page did not load. Try again later.' }
  return { kind: 'page', page: (await response.json()) as SharedPage }
}

/** Null on success; else the message to show. */
export async function unlockShare(token: string, password: string): Promise<string | null> {
  const response = await fetch(`/api/share/${encodeURIComponent(token)}/unlock`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ password }),
    credentials: 'omit',
  })
  if (response.ok) {
    const { grant } = (await response.json()) as { grant: string }
    keepGrant(token, grant)
    return null
  }
  if (response.status === 429) {
    const wait = response.headers.get('Retry-After')
    return `Too many wrong passwords for this link. Wait ${wait ?? 'a few'} seconds and try again.`
  }
  if (response.status === 404) return 'This link does not exist or no longer works.'
  return 'The password is not right.'
}
