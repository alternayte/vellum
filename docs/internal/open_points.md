# Open Points — Document Review

> Generated from adversarial review of PRD, SDD, and UI/UX docs against rubrics. Items below are **not blocking the build** — they're product-management and operational concerns to address when relevant.

---

## PRD — non-blocking gaps (17/30)

The PRD scores 17/30 against the rubric. The remaining FAILs are in goals/metrics (section 2), user stories (section 3), and NFRs (section 4). These are important for a product-management process but don't block implementation — the SDD and UI/UX docs have enough detail to build from.

| Area | What's missing | When it matters |
|------|---------------|-----------------|
| Outcome-based goals (2.1) | Goals describe outputs not outcomes | When measuring success post-launch |
| Numeric success metrics (2.2) | Demo scenarios, not metrics with baselines/targets | When defining what "good" looks like |
| Leading/lagging indicators (2.3) | No metrics framework | When setting up analytics |
| Timeline (2.5) | No dates or durations per phase | When coordinating with others |
| User stories + acceptance criteria (3.1) | Features listed without testable stories | When writing QA test plans |
| Happy/sad paths (3.2) | No error/failure scenarios | When writing QA test plans |
| Edge cases (3.3) | No limits, concurrency, permission boundaries | When writing QA test plans |
| Performance targets (4.1) | No p95 latency numbers | When benchmarking |
| Availability/SLA (4.2) | No uptime target | When running hosted offering |
| Data retention/privacy (4.4) | No retention policy or GDPR stance | When handling user data |
| Observability (4.5) | No SLIs/SLOs | When setting up monitoring |
| Capacity (4.6) | No load/growth projections | When capacity planning |
| Stakeholder sign-off (6.3) | No approvers listed | May be N/A for solo project |

## SDD — refinement questions (50/50)

All rubric items pass. These are tune-later items:

| Area | Question | When it matters |
|------|----------|-----------------|
| Backup infra (§7.8) | Which cloud provider / managed DB for hosted? | When setting up hosted infrastructure |
| Rate limits (§8.3) | Are 200/60/100 req/min defaults appropriate? | When observing real traffic |
| Alerting (§19.3) | PagerDuty, Opsgenie, Slack? Who's on-call? | When setting up hosted monitoring |
| Encryption (§20.3) | App-level encryption beyond Postgres disk encryption? | When handling sensitive architecture data |
| Duration estimates (§22.1) | Are 2–4 weeks/phase realistic for team size? | When planning timeline |

## UI/UX — resolved and deferred

All implementation-blocking questions resolved:

- **MDX editor:** CodeMirror with MDX syntax highlighting, no preview pane in v1 ✓
- **Tag/owner/team CRUD:** Inline-create on elements + project/workspace settings for management ✓
- **Schema rendering:** Read-only syntax-highlighted code block with format badge ✓
- **Message node shape:** Horizontal pill/lozenge ✓

Remaining non-blocking items (defer to implementation):

| Item | When it matters |
|------|-----------------|
| Share link expiry default | When implementing share links |
| Mobile/responsive stance | State "desktop only v1" if asked |
| Keyboard shortcut bindings | During implementation |
| Display/mono typeface picks | During frontend setup |
| Docs inline vs full-width editor | During docs implementation |
| Auto-layout flavour | Tune against real models |
