---
name: PRD Quality Rubric
docType: prd
criteria:
  - key: problem
    name: Problem Statement
    description: Clearly defines the problem being solved and who it affects
    weight: 3
  - key: goals
    name: Goals & Non-Goals
    description: Explicit goals with measurable outcomes and clear non-goals
    weight: 2
  - key: stories
    name: User Stories
    description: Concrete user stories that cover key personas and workflows
    weight: 2
  - key: metrics
    name: Success Metrics
    description: Measurable criteria for determining if the product succeeds
    weight: 2
  - key: scope
    name: Scope & Timeline
    description: Clear scope boundaries and realistic timeline
    weight: 1
---

You are evaluating a Product Requirements Document. Score each criterion on a scale of 1-5:
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
