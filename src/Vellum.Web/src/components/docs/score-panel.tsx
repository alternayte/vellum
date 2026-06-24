import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Button } from '@/components/ui/button'
import type { Score, ScoreListItem } from '@/hooks/use-scores'

function scoreColor(score: number): string {
  if (score >= 4) return 'text-green-500'
  if (score >= 3) return 'text-yellow-500'
  return 'text-red-500'
}

function ScoreBar({ score, maxScore }: { score: number; maxScore: number }) {
  const pct = (score / maxScore) * 100
  return (
    <div className="h-2 w-full rounded-full bg-muted">
      <div
        className={`h-2 rounded-full ${score >= 4 ? 'bg-green-500' : score >= 3 ? 'bg-yellow-500' : 'bg-red-500'}`}
        style={{ width: `${pct}%` }}
      />
    </div>
  )
}

interface ScorePanelProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  score: Score | null
  scoreHistory: ScoreListItem[]
  onSelectScore: (scoreId: string) => void
  onReviewSuggestions: () => void
  isLoading: boolean
}

export function ScorePanel({
  open, onOpenChange, score, scoreHistory, onSelectScore, onReviewSuggestions, isLoading,
}: ScorePanelProps) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="right" className="w-96 overflow-y-auto">
        <SheetHeader>
          <SheetTitle>Score Results</SheetTitle>
        </SheetHeader>

        {isLoading && (
          <div className="flex items-center justify-center py-12">
            <div className="h-8 w-8 animate-spin rounded-full border-2 border-primary border-t-transparent" />
            <span className="ml-3 text-sm text-muted-foreground">Scoring document...</span>
          </div>
        )}

        {score && !isLoading && (
          <div className="mt-4 space-y-6">
            <div className="text-center">
              <span className={`text-4xl font-bold ${scoreColor(score.overallScore)}`}>
                {score.overallScore}
              </span>
              <span className="text-lg text-muted-foreground">/5</span>
            </div>

            <div className="space-y-4">
              {score.criteriaResults.map((c) => (
                <div key={c.key} className="space-y-1">
                  <div className="flex items-center justify-between">
                    <span className="text-sm font-medium">{c.name}</span>
                    <span className={`text-sm font-bold ${scoreColor(c.score)}`}>
                      {c.score}/{c.maxScore}
                    </span>
                  </div>
                  <ScoreBar score={c.score} maxScore={c.maxScore} />
                  <p className="text-xs text-muted-foreground">{c.explanation}</p>
                  {c.suggestedEdit && (
                    <p className="text-xs italic text-muted-foreground">
                      Suggestion: {c.suggestedEdit}
                    </p>
                  )}
                </div>
              ))}
            </div>

            {score.suggestedContent && (
              <Button variant="outline" className="w-full" onClick={onReviewSuggestions}>
                Review Suggestions
              </Button>
            )}

            {scoreHistory.length > 1 && (
              <div className="border-t border-border pt-4">
                <p className="mb-2 text-xs font-medium uppercase tracking-wider text-muted-foreground">
                  History
                </p>
                {scoreHistory.map((s) => (
                  <button
                    key={s.id}
                    className={`flex w-full items-center justify-between rounded px-2 py-1 text-sm hover:bg-muted ${
                      s.id === score.id ? 'bg-accent' : ''
                    }`}
                    onClick={() => onSelectScore(s.id)}
                  >
                    <span className={scoreColor(s.overallScore)}>{s.overallScore}/5</span>
                    <span className="text-xs text-muted-foreground">
                      {new Date(s.createdAt).toLocaleDateString()}
                    </span>
                  </button>
                ))}
              </div>
            )}
          </div>
        )}
      </SheetContent>
    </Sheet>
  )
}
