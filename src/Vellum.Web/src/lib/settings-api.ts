/** Typed access to the setup, instance, settings, secrets and audit endpoints. */

export interface SetupStatus {
  needsSetup: boolean
  profile: 'local' | 'hosted'
  requiresSetupToken: boolean
  /** Where the setup code comes from: the server log, or the operator who configured it. */
  setupTokenHint: 'log' | 'operator' | null
}

export interface InstanceInfo {
  profile: 'local' | 'hosted'
  version: string
  isInstanceAdmin: boolean
  allowSelfRegistration: boolean
}

export type ProviderKind = 'openai' | 'anthropic' | 'gemini' | 'openai-compatible'

export interface AiProvider {
  id: string
  kind: ProviderKind
  name: string
  baseUrl: string
  model: string
  enabled: boolean
  supportsStructuredOutput: boolean
  supportsStreaming: boolean
  temperature: number | null
  timeoutSeconds: number
  monthlyBudgetUsd: number | null
  requestsPerMinute: number | null
}

export interface SettingsDocument {
  ai: {
    providers: AiProvider[]
    taskRouting: Record<'inferArchitecture' | 'proposeFlow' | 'critique' | 'explain', string | null>
  }
  github: { apiBaseUrl: string; defaultRef: string }
  import: { maxFiles: number; maxFileBytes: number; maxTotalBytes: number; excludeGlobs: string[] }
  cost: { provider: string; region: string; currency: string }
  simulation: { seed: number; durationSeconds: number; arrivalRatePerSecond: number }
  privacy: { sendPrivateCodeToAi: boolean }
  usage: { monthlyAiBudgetUsd: number | null; maxScansPerDay: number }
  notifications: { webhookUrl: string | null }
}

export type SettingsSection = keyof SettingsDocument
export type SettingsOverrides = { [S in SettingsSection]?: Partial<SettingsDocument[S]> }

export interface PolicyViolation {
  field: string
  message: string
}

export interface WorkspaceSettings {
  effective: SettingsDocument
  overrides: SettingsOverrides
  defaults: SettingsDocument
  /** Values the instance policy refuses now. The effective document already leaves them out. */
  policyViolations: PolicyViolation[]
  /** Sent back as If-Match, so a save cannot overwrite a change it did not see. */
  version: number
}

export interface InstanceSettings {
  policy: {
    allowSelfRegistration: boolean
    allowPrivateNetworkUrls: boolean
    allowedAiProviderKinds: ProviderKind[]
    allowSendingPrivateCodeToAi: boolean
  }
  defaults: SettingsDocument
  version: number
  workspacesWithPolicyViolations: number
}

export interface SecretStatus {
  name: string
  isSet: boolean
  updatedAt: string | null
  updatedBy: string | null
  fingerprint: string | null
  /** The host the value was saved for. Vellum sends it to that host only. */
  boundOrigin: string | null
  matchesCurrentUrl: boolean
}

export interface ConnectionTestError {
  code: string
  message: string
}

export interface ProviderTestResult {
  ok: boolean
  latencyMs: number
  model: string
  reportedModel: string | null
  supportsStructuredOutput: boolean | null
  error: ConnectionTestError | null
}

export interface GitHubTestResult {
  ok: boolean
  authenticatedAs: string | null
  rateLimitRemaining: number | null
  error: ConnectionTestError | null
}

export interface AiUsageTotal {
  providerId: string
  providerKind: ProviderKind
  model: string
  task: string
  calls: number
  inputTokens: number | null
  outputTokens: number | null
  costUsd: number | null
  costKnown: boolean
  /** Part of the cost comes from a ceiling, because the provider reported no token count. */
  costEstimated: boolean
}

export interface AiUsage {
  from: string
  to: string
  totals: AiUsageTotal[]
}

export interface ModelPrice {
  providerKind: ProviderKind
  model: string
  inputUsdPerMillionTokens: number
  outputUsdPerMillionTokens: number
  effectiveFrom: string
}

export interface RotateKeysResult {
  rewrapped: number
  remaining: number
  message: string
}

export interface AuditEntry {
  at: string
  actorId: string
  action: string
  target: string
}

export interface FieldError {
  field: string
  message: string
}

/** A refused request. `fields` holds per-field messages when the server gave them. */
export class ApiError extends Error {
  status: number
  fields: FieldError[]

  constructor(status: number, message: string, fields: FieldError[] = []) {
    super(message)
    this.status = status
    this.fields = fields
  }
}

export async function api<T>(method: string, url: string, body?: unknown, options?: { ifMatch?: number }): Promise<T> {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (options?.ifMatch !== undefined) headers['If-Match'] = `"${options.ifMatch}"`
  const response = await fetch(url, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await response.text()
  const data = text ? safeParse(text) : null
  if (!response.ok) {
    // The shared error envelope: { type, title, detail, errors: [{ field, message }] }.
    const payload = (data ?? {}) as { title?: string; detail?: string | null; errors?: FieldError[] | null }
    throw new ApiError(
      response.status,
      payload.title
        ? (payload.detail ? `${payload.title} ${payload.detail}` : payload.title)
        : response.status === 403 ? 'You do not have permission to do this.' : 'The request did not complete.',
      Array.isArray(payload.errors) ? payload.errors : [],
    )
  }
  return data as T
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return null
  }
}

export const PROVIDER_PRESETS: Record<ProviderKind, { label: string; baseUrl: string; model: string }> = {
  openai: { label: 'OpenAI', baseUrl: 'https://api.openai.com/v1', model: '' },
  anthropic: { label: 'Anthropic', baseUrl: 'https://api.anthropic.com', model: '' },
  gemini: { label: 'Google Gemini', baseUrl: 'https://generativelanguage.googleapis.com', model: '' },
  'openai-compatible': { label: 'OpenAI-compatible (OpenRouter, Ollama, LM Studio)', baseUrl: 'http://localhost:11434/v1', model: '' },
}

/** The value a field has now: the workspace override when there is one, else the default. */
export function effectiveSection<S extends SettingsSection>(
  settings: Pick<WorkspaceSettings, 'defaults'>,
  overrides: SettingsOverrides,
  section: S,
): SettingsDocument[S] {
  return { ...settings.defaults[section], ...(overrides[section] ?? {}) } as SettingsDocument[S]
}

export function setOverride<S extends SettingsSection, K extends keyof SettingsDocument[S]>(
  overrides: SettingsOverrides,
  section: S,
  field: K,
  value: SettingsDocument[S][K] | undefined,
): SettingsOverrides {
  const current = { ...(overrides[section] ?? {}) } as Partial<SettingsDocument[S]>
  if (value === undefined) delete current[field]
  else current[field] = value
  const next = { ...overrides }
  if (Object.keys(current).length === 0) delete next[section]
  else next[section] = current as never
  return next
}

export function countOverrides(overrides: SettingsOverrides): number {
  return Object.values(overrides).reduce((total, section) => total + Object.keys(section ?? {}).length, 0)
}
