/** Card width by kind. Tidy reads it too, so a layout leaves room for each card. */
export const KIND_WIDTHS: Record<string, number> = {
  actor: 248,
  system: 288,
  app: 264,
  store: 264,
  component: 248,
}

/** The size Tidy reserves for a card. Every card is this high unless a lens adds chips. */
export function cardSize(kind: string): { width: number; height: number } {
  return { width: KIND_WIDTHS[kind] ?? 264, height: 128 }
}
