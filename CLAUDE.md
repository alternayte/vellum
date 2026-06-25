# Vellum

Self-hostable C4 architecture modelling tool. Event-sourced .NET 10 backend, React 19 SPA frontend.

## Conventions

- The `docs/` folder is local-only internal reference material. Do not commit it.

## React Flow / canvas
- Canvas is React Flow v12: package `@xyflow/react` (NOT `reactflow`); `ReactFlow`
  is a named import. Before writing or editing any canvas/node/edge code, consult
    the `react-flow-canvas` skill. Current API: https://reactflow.dev/llms-full.txt
    - The React Flow graph is a *projection* of the event-sourced domain model, never
      the source of truth. Drag/connect/delete emit domain events; views are re-derived.
        Do not persist RF node shape as the model or hold all C4 levels in one graph.
