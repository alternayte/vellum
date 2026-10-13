import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { KeyRound } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api, type SecretStatus } from '@/lib/settings-api'
import { shownInline } from '@/lib/mutation-errors'

interface SecretControlProps {
  workspaceId: string
  name: string
  label: string
  status: SecretStatus | undefined
  canEdit: boolean
}

/**
 * A stored credential. The value goes in once and never comes back: the control shows
 * only whether it is set, when it changed and a fingerprint to tell two keys apart.
 */
export function SecretControl({ workspaceId, name, label, status, canEdit }: SecretControlProps) {
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [value, setValue] = useState('')
  const url = `/api/workspaces/${workspaceId}/secrets/${encodeURIComponent(name)}`
  const refresh = () => {
    setEditing(false)
    setValue('')
    return queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'secrets'] })
  }
  const save = useMutation({ mutationFn: () => api<void>('PUT', url, { value }), onSuccess: refresh, onError: shownInline })
  const remove = useMutation({ mutationFn: () => api<void>('DELETE', url), onSuccess: refresh, onError: shownInline })
  const failure = save.error ?? remove.error

  return (
    <div className="rounded-lg border border-border p-3">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <KeyRound className="h-4 w-4 shrink-0 text-muted-foreground" />
        <div className="min-w-0 flex-1">
          <p className="text-sm text-foreground">{label}</p>
          <p className="text-xs text-muted-foreground">
            {status?.isSet
              ? <>Set{status.updatedAt ? ` on ${new Date(status.updatedAt).toLocaleDateString()}` : ''} · fingerprint <span className="font-mono">{status.fingerprint}</span></>
              : 'Not set'}
          </p>
        </div>
        {canEdit && !editing && (
          <>
            <Button variant="secondary" size="sm" onClick={() => setEditing(true)}>{status?.isSet ? 'Replace' : 'Set'}</Button>
            {status?.isSet && (
              <Button variant="ghost" size="sm" className="text-destructive" disabled={remove.isPending} onClick={() => remove.mutate()}>
                Remove
              </Button>
            )}
          </>
        )}
      </div>
      {editing && (
        <form
          className="mt-3 flex flex-wrap items-center gap-2"
          onSubmit={(e) => { e.preventDefault(); save.mutate() }}
        >
          <Input
            type="password"
            className="min-w-48 flex-1"
            value={value}
            onChange={(e) => setValue(e.target.value)}
            aria-label={`New value for ${label}`}
            autoComplete="off"
            autoFocus
            required
          />
          <Button type="submit" size="sm" disabled={save.isPending || !value}>Save</Button>
          <Button type="button" variant="ghost" size="sm" onClick={() => { setEditing(false); setValue('') }}>Cancel</Button>
          <p className="w-full text-xs text-muted-foreground">The value is encrypted when it is saved. Vellum does not show it again.</p>
        </form>
      )}
      {failure && <p role="alert" className="mt-2 text-xs text-destructive">{failure.message}</p>}
    </div>
  )
}
