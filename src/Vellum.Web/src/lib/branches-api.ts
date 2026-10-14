import { ApiError, api, type FieldError } from '@/lib/settings-api'
import type { Element } from '@/hooks/use-elements'
import type { Relationship } from '@/hooks/use-relationships'
import type { Message } from '@/hooks/use-messages'
import type { Binding, Channel, Operation, Subscription } from '@/hooks/use-contracts'
import type { ModelSelection, ReviewData } from '@/stores/draft-store'

export type Role = 'Owner' | 'Editor' | 'Viewer'

export interface ProjectDetail {
  id: string
  workspaceId: string
  name: string
  description: string | null
  /** The stream of the main model. A stress run and an estimate name it as their branch. */
  streamId: string
  yourRole: Role
}

export interface DraftDto {
  id: string
  name: string
  streamId: string
  forkVersion: number
  status: string
  createdBy: string
  createdAt: string
}

export interface Revision {
  id: string
  name: string
  note: string | null
  version: number
  createdBy: string
  createdAt: string
}

export interface Variant {
  id: string
  name: string
  streamId: string
  baseRevisionId: string | null
  forkVersion: number
  status: string
  createdBy: string
  createdAt: string
}

export interface RevisionModel {
  revision: Revision
  elements: Element[]
  relationships: Relationship[]
  messages: Message[]
  operations: Operation[]
  channels: Channel[]
  subscriptions: Subscription[]
  bindings: Binding[]
}

export type EntityType = 'element' | 'relationship' | 'message' | 'operation' | 'channel' | 'subscription' | 'binding'

export interface ModelDifference {
  entityType: EntityType
  entityId: string
  changeKind: 'added' | 'removed' | 'modified'
  left: Record<string, unknown> | null
  right: Record<string, unknown> | null
}

export interface ComparedModel {
  streamId: string
  version: number
  revisionId: string | null
  revisionName: string | null
}

export interface ModelComparison {
  left: ComparedModel
  right: ComparedModel
  changes: ModelDifference[]
}

export interface Member {
  userId: string
  email: string | null
  displayName: string | null
  role: Role
  /** Null for a membership that is older than the record of this date. */
  createdAt: string | null
}

/** An open invitation of the workspace, as an owner reads it. */
export interface Invitation {
  id: string
  workspaceId: string
  email: string
  role: Role
  status: 'pending' | 'accepted' | 'declined' | 'revoked' | 'expired'
  createdAt: string
  expiresAt: string
}

/** An open invitation for the signed-in person. */
export interface MyInvitation {
  id: string
  workspaceId: string
  workspaceName: string
  role: Role
  /** The name of the owner who sent it. Null when that account does not exist any more. */
  invitedBy: string | null
  createdAt: string
  expiresAt: string
}

export interface ImportItem {
  kind: EntityType | 'schema'
  id: string
  name: string | null
  change: 'added' | 'changed' | 'unchanged' | 'refused'
  error: string | null
  /** For a schema whose ID belongs to another project: the ID it gets in this project. */
  importedAs: string | null
}

export interface ImportResult {
  applied: boolean
  modelVersion: number
  added: number
  changed: number
  unchanged: number
  refused: number
  items: ImportItem[]
}

const project = (projectId: string) => `/api/projects/${projectId}`

/** A side of a model comparison as the server reads it: `main`, a branch stream, or `revision:<id>`. */
export function sideOf(selection: ModelSelection): string {
  return selection.kind === 'main' ? 'main' : selection.kind === 'revision' ? `revision:${selection.id}` : selection.streamId
}

export const branchesApi = {
  project: (projectId: string) => api<ProjectDetail>('GET', project(projectId)),
  drafts: async (projectId: string) =>
    (await api<{ items: DraftDto[] }>('GET', `${project(projectId)}/drafts?status=open&limit=200`)).items,
  createDraft: (projectId: string, name: string) =>
    api<DraftDto>('POST', `${project(projectId)}/drafts`, { id: crypto.randomUUID(), name }),
  abandonDraft: (projectId: string, draftId: string) => api<DraftDto>('POST', `${project(projectId)}/drafts/${draftId}/abandon`),
  /** What a merge of the draft into main would do. It writes nothing. */
  mergePreview: (projectId: string, draftId: string) =>
    api<ReviewData>('POST', `${project(projectId)}/drafts/${draftId}/merge/preview`),
  merge: (projectId: string, draftId: string, resolutions: { entityId: string; resolution: string }[], expectedMainVersion: number) =>
    api<DraftDto>('POST', `${project(projectId)}/drafts/${draftId}/merge/execute`, { resolutions, expectedMainVersion }),
  revisions: (projectId: string) => api<Revision[]>('GET', `${project(projectId)}/revisions`),
  createRevision: (projectId: string, name: string, note: string | null) =>
    api<Revision>('POST', `${project(projectId)}/revisions`, { id: crypto.randomUUID(), name, note }),
  revisionModel: (projectId: string, revisionId: string) =>
    api<RevisionModel>('GET', `${project(projectId)}/revisions/${revisionId}/model`),
  variants: async (projectId: string) =>
    (await api<Variant[]>('GET', `${project(projectId)}/variants`)).filter((v) => v.status === 'open'),
  /** The variant copies the model of the revision, or of main when there is none, and the cost sheet of that model's branch. */
  createVariant: (projectId: string, name: string, baseRevisionId: string | null) =>
    api<Variant>('POST', `${project(projectId)}/variants`, { id: crypto.randomUUID(), name, baseRevisionId, costSheetFrom: null }),
  compareModels: (projectId: string, left: ModelSelection, right: ModelSelection) =>
    api<ModelComparison>('GET', `${project(projectId)}/model/compare?left=${sideOf(left)}&right=${sideOf(right)}`),

  members: (workspaceId: string) =>
    api<{ yourRole: Role; members: Member[] }>('GET', `/api/workspaces/${workspaceId}/members`),
  invitations: (workspaceId: string) => api<Invitation[]>('GET', `/api/workspaces/${workspaceId}/invitations`),
  /** The answer is the same whether the address has an account here or not. */
  invite: (workspaceId: string, email: string, role: Role) =>
    api<Invitation>('POST', `/api/workspaces/${workspaceId}/invitations`, { email, role }),
  revokeInvitation: (workspaceId: string, invitationId: string) =>
    api<void>('DELETE', `/api/workspaces/${workspaceId}/invitations/${invitationId}`),
  myInvitations: () => api<MyInvitation[]>('GET', '/api/invitations'),
  acceptInvitation: (invitationId: string) => api<unknown>('POST', `/api/invitations/${invitationId}/accept`),
  declineInvitation: (invitationId: string) => api<void>('POST', `/api/invitations/${invitationId}/decline`),
  changeRole: (workspaceId: string, userId: string, role: Role) =>
    api<unknown>('PATCH', `/api/workspaces/${workspaceId}/members/${userId}`, { role }),
  removeMember: (workspaceId: string, userId: string) =>
    api<void>('DELETE', `/api/workspaces/${workspaceId}/members/${userId}`),
}

/** The model document of main as a file. The server names no file, so the name is made here. */
export async function downloadModel(projectId: string, projectName: string, format: 'json' | 'yaml'): Promise<void> {
  const response = await fetch(`${project(projectId)}/export?format=${format}`)
  if (!response.ok) throw new Error(response.status === 403 ? 'You do not have permission to do this.' : 'The export did not complete.')
  const url = URL.createObjectURL(await response.blob())
  const link = document.createElement('a')
  link.href = url
  link.download = `${projectName.trim().replace(/[^\w.-]+/g, '-').toLowerCase() || 'model'}.vellum.${format}`
  link.click()
  URL.revokeObjectURL(url)
}

/**
 * Sends a model document to the import. With `expectedVersion` null the call is a preview
 * and writes nothing. The file goes as it is: the server reads JSON and YAML.
 */
export async function importModel(projectId: string, file: File, expectedVersion: number | null): Promise<ImportResult> {
  const yaml = /\.ya?ml$/i.test(file.name)
  const query = expectedVersion === null ? 'dryRun=true' : `expectedVersion=${expectedVersion}`
  const response = await fetch(`${project(projectId)}/import?${query}`, {
    method: 'POST',
    headers: { 'Content-Type': yaml ? 'application/x-yaml' : 'application/json' },
    body: file,
  })
  const data = parse(await response.text())
  if (!response.ok) {
    const payload = (data ?? {}) as { title?: string; detail?: string | null; errors?: FieldError[] | null }
    throw new ApiError(
      response.status,
      payload.title
        ? (payload.detail ? `${payload.title} ${payload.detail}` : payload.title)
        : response.status === 413 ? 'The file is larger than the limit.'
          : response.status === 403 ? 'You do not have permission to do this.' : 'The import did not complete.',
      Array.isArray(payload.errors) ? payload.errors : [],
    )
  }
  return data as ImportResult
}

function parse(text: string): unknown {
  try {
    return text ? JSON.parse(text) : null
  } catch {
    return null
  }
}

/** The name of a person for "who": the display name, else the email address, else a short ID. */
export function personName(members: Member[] | undefined, userId: string): string {
  const member = members?.find((m) => m.userId === userId)
  return member?.displayName || member?.email || `user ${userId.slice(0, 8)}`
}
