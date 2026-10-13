import type { Unavailable } from '@/lib/design-api'

/** Says why AI did not answer and what to do. The rest of Vellum works without it. */
export function UnavailableNote({ state }: { state: Unavailable }) {
  const notSetUp = state.reason.code === 'not_configured' || state.reason.code === 'disabled'
  return (
    <div role="status" className="rounded-md border border-border px-3 py-2 text-xs">
      <p className="font-medium text-foreground">
        {state.status === 'failed' ? 'The assistant did not give a usable answer.' : notSetUp ? 'No AI provider is set for this task.' : 'The assistant cannot answer now.'}
      </p>
      <p className="mt-1 leading-relaxed text-muted-foreground">{state.reason.message}</p>
      {state.reason.retryAfterSeconds !== null && (
        <p className="mt-1 text-muted-foreground">Try again in {state.reason.retryAfterSeconds} seconds.</p>
      )}
    </div>
  )
}
