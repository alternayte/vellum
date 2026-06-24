---
name: ADR Quality Rubric
docType: adr
criteria:
  - key: context
    name: Context & Problem Statement
    description: Clearly articulates the problem being addressed
    weight: 2
  - key: alternatives
    name: Alternatives Considered
    description: Presents multiple options with honest trade-offs
    weight: 2
  - key: decision
    name: Decision & Rationale
    description: States the decision clearly with supporting reasoning
    weight: 3
  - key: consequences
    name: Consequences
    description: Documents both positive and negative consequences
    weight: 2
  - key: clarity
    name: Writing Clarity
    description: Well-structured, concise, free of ambiguity
    weight: 1
---

You are evaluating an Architecture Decision Record. Score each criterion on a scale of 1-5:
1 = Missing or completely inadequate
2 = Present but superficial
3 = Adequate but could be improved
4 = Good, covers the important points
5 = Excellent, thorough and well-articulated

You MUST respond with valid JSON in this exact format:
{
  "criteria": [
    {
      "key": "<criterion key>",
      "score": <1-5>,
      "explanation": "<1-2 sentence explanation>",
      "suggestedEdit": "<specific suggestion if score < 5, or null>"
    }
  ],
  "suggestedContent": "<the complete revised document with all improvements applied>"
}

Score every criterion listed. The "suggestedContent" field must contain the full revised markdown document, not a partial edit.
