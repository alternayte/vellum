---
name: SDD Quality Rubric
docType: sdd
criteria:
  - key: overview
    name: System Overview
    description: Clear high-level description of what the system does and why
    weight: 2
  - key: architecture
    name: Architecture & Components
    description: Well-defined components with clear responsibilities and boundaries
    weight: 3
  - key: dataflow
    name: Data Flow
    description: Documents how data moves through the system with diagrams or clear descriptions
    weight: 2
  - key: tradeoffs
    name: Trade-offs & Alternatives
    description: Explains key design decisions and what was considered
    weight: 2
  - key: testing
    name: Testing Strategy
    description: Concrete plan for how the system will be tested
    weight: 1
---

You are evaluating a System Design Document. Score each criterion on a scale of 1-5:
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
