/**
 * The secret of an invitation link. The link carries it in the fragment
 * (`/invitations/{id}#secret=…`), which a browser never sends to a server. The app takes it
 * out of the address bar at once and keeps it in this tab's session storage until the
 * person answers, so a sign-in on the way does not lose it.
 */
const PENDING = 'vellum.invitation.pending'
const secretKey = (id: string) => `vellum.invitation.secret.${id}`
const LINK = /^\/invitations\/([0-9a-fA-F-]{36})\/?$/

function storage(): Storage | null {
  try {
    return window.sessionStorage
  } catch {
    return null
  }
}

/** Keeps the secret of the link the browser is on, and removes it from the address bar. */
export function captureInvitationLink(): void {
  const id = LINK.exec(window.location.pathname)?.[1]
  const secret = new URLSearchParams(window.location.hash.slice(1)).get('secret')
  if (!id || !secret) return
  try {
    storage()?.setItem(secretKey(id), secret)
    storage()?.setItem(PENDING, id)
  } catch {
    // No storage: the page reads the secret from the address bar instead.
    return
  }
  window.history.replaceState(window.history.state, '', window.location.pathname)
}

/** The secret of the invitation, from storage or from the address bar. */
export function invitationSecret(id: string): string | null {
  try {
    const stored = storage()?.getItem(secretKey(id))
    if (stored) return stored
  } catch {
    // Fall through to the address bar.
  }
  return new URLSearchParams(window.location.hash.slice(1)).get('secret')
}

/** The invitation whose link was opened before a sign-in, if any. */
export function pendingInvitation(): string | null {
  try {
    return storage()?.getItem(PENDING) ?? null
  } catch {
    return null
  }
}

export function clearPendingInvitation(): void {
  try {
    storage()?.removeItem(PENDING)
  } catch {
    // Nothing to clear.
  }
}

/** Drops the secret once the person has answered. */
export function forgetInvitation(id: string): void {
  try {
    storage()?.removeItem(secretKey(id))
    if (storage()?.getItem(PENDING) === id) storage()?.removeItem(PENDING)
  } catch {
    // Nothing to forget.
  }
}
