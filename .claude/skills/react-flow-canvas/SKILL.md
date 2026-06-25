---
name: react-flow-canvas
description: >
  Build node-based canvas UIs with React Flow v12 (@xyflow/react) — diagram editors,
  architecture/C4 modelling tools (IcePanel/Structurizr-style), flow builders, and any
  draggable-node + connectable-edge interface. USE THIS SKILL WHENEVER the task mentions
  React Flow, @xyflow/react, "reactflow", a node/edge canvas, a flow/graph/diagram editor,
  drag-and-drop nodes, an architecture or C4 diagramming tool, or building anything that
  looks like a whiteboard/Miro/FigJam-style canvas — even if the user doesn't name the
  library. React Flow's API changed substantially in v12 and most model training data is
  stale, so DO NOT write React Flow code from memory; follow this skill first.
---

# React Flow v12 Canvas

## 0. Before writing any code

You almost certainly have a stale mental model of React Flow. The package was renamed and
fields were moved in v12. **Treat your priors as v11 and wrong.** When in doubt, fetch the
canonical, current docs (they are written for LLMs):

- Index:  https://reactflow.dev/llms.txt
- Full:   https://reactflow.dev/llms-full.txt   (all guides + API + examples)

Fetch `llms-full.txt` and grep it before reaching for memory on any non-trivial feature
(custom edges, connection validation, viewport control, server rendering).

## 1. Non-negotiable setup (the failure happens here)

```bash
npm install @xyflow/react        # NOT "reactflow" (that is the dead v11 package)
```

```tsx
import { ReactFlow } from '@xyflow/react';   // named import — NOT a default import
import '@xyflow/react/dist/style.css';        // REQUIRED — nothing renders without this
```

Checklist — if any of these is missing, the canvas renders blank or broken:

1. Package is `@xyflow/react`, never `reactflow`.
2. `ReactFlow` is a **named** import (`import { ReactFlow }`), never `import ReactFlow`.
3. The stylesheet is imported (`dist/style.css`, or `dist/base.css` for unstyled).
4. The `<ReactFlow>` parent has an **explicit height** (e.g. `style={{ width: '100%', height: '100vh' }}` or a sized flex/grid cell). A zero-height container = invisible canvas. This is the #1 "it renders nothing" cause after the import issues.
5. Any hook (`useReactFlow`, `useNodes`, `useStore`, `useConnection`, ...) is called **inside** a `<ReactFlowProvider>`. The provider goes *outside* the component that uses the hook.

## 2. v11 → v12 rename trap table (this is why your agent's code is wrong)

| Wrote from memory (v11) | Correct (v12) |
|---|---|
| `import ReactFlow from 'reactflow'` | `import { ReactFlow } from '@xyflow/react'` |
| `node.width` / `node.height` (measured) | `node.measured.width` / `node.measured.height` |
| `node.parentNode` (sub-flows) | `node.parentId` |
| `reactFlowInstance.project(...)` | `reactFlowInstance.screenToFlowPosition(...)` (pass `clientX/clientY`, no manual bounds subtraction) |
| custom node props `xPos` / `yPos` | `positionAbsoluteX` / `positionAbsoluteY` |
| store `nodeInternals` | `nodeLookup` |
| store `connectionNodeId` etc. | `connection.fromHandle.nodeId` etc. |
| `getBezierEdgeCenter` | `getBezierPath` returns `[path, labelX, labelY]` |

`node.width` / `node.height` now mean *optional dimensions you set* (used for SSR). The
library's measured values live in `node.measured`. **Any dagre/elk layout code must read
`node.measured.width/height`, not `node.width/height`** — getting this wrong silently
collapses your layout because measured dims are `undefined` on first pass.

## 3. Canonical controlled-flow skeleton

This is the shape to build from. Controlled state via the helper hooks; changes applied
through the `onNodesChange`/`onEdgesChange` handlers (do **not** mutate nodes in place —
v12 requires new node objects for updates).

```tsx
import { useCallback } from 'react';
import {
  ReactFlow, Background, Controls, MiniMap,
  useNodesState, useEdgesState, addEdge,
  type Node, type Edge, type Connection,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';

const initialNodes: Node[] = [
  { id: '1', position: { x: 0, y: 0 }, data: { label: 'A' } },
  { id: '2', position: { x: 200, y: 120 }, data: { label: 'B' } },
];
const initialEdges: Edge[] = [{ id: 'e1-2', source: '1', target: '2' }];

export function Flow() {
  const [nodes, setNodes, onNodesChange] = useNodesState(initialNodes);
  const [edges, setEdges, onEdgesChange] = useEdgesState(initialEdges);
  const onConnect = useCallback(
    (c: Connection) => setEdges((eds) => addEdge(c, eds)),
    [setEdges],
  );

  return (
    <div style={{ width: '100%', height: '100vh' }}>
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={onConnect}
        fitView
      >
        <Background />
        <Controls />
        <MiniMap />
      </ReactFlow>
    </div>
  );
}
```

For your own store (Zustand/Redux/event-sourced projection) instead of the helper hooks,
apply changes manually: `import { applyNodeChanges, applyEdgeChanges }` and call them in
your `onNodesChange`/`onEdgesChange` reducers. Same contract, your state.

## 4. Custom nodes (memoize the maps or pay re-render tax)

```tsx
import { Handle, Position, type NodeProps } from '@xyflow/react';

function ServiceNode({ data, selected }: NodeProps) {
  return (
    <div className={`service-node${selected ? ' selected' : ''}`}>
      <Handle type="target" position={Position.Top} />
      <strong>{data.label}</strong>
      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}

// DEFINE OUTSIDE THE COMPONENT (or useMemo with [] deps). A fresh object each render
// triggers a React Flow warning + full re-render every frame.
const nodeTypes = { service: ServiceNode };
// ...then <ReactFlow nodeTypes={nodeTypes} ... />
```

Rules:
- The node's `type` string must **exactly** match a key in `nodeTypes` ("missing node type" error otherwise).
- `nodeTypes` / `edgeTypes` defined module-scope or `useMemo(() => ({...}), [])` — never inline.
- A custom node renders no handles by default — add `<Handle>` explicitly or edges can't connect.

## 5. Nesting / containers / sub-flows — the heart of a C4 tool

C4 boundaries (System → Container → Component) map to parent/child nodes.

```tsx
const nodes: Node[] = [
  // PARENT MUST APPEAR BEFORE ITS CHILDREN IN THE ARRAY (ordering is load-bearing).
  { id: 'sys', type: 'group', position: { x: 0, y: 0 },
    style: { width: 400, height: 300 }, data: { label: 'Payment System' } },
  // child position is RELATIVE to parent's top-left ({0,0} = parent corner)
  { id: 'api', position: { x: 24, y: 48 }, parentId: 'sys', extent: 'parent',
    data: { label: 'API' } },
];
```

- `parentId` (NOT `parentNode`) establishes the relationship and makes child positions parent-relative; moving the parent moves children.
- `extent: 'parent'` confines a child to the parent's bounds. Setting `extent` with no `parentId` throws a warning — always pair them.
- `type: 'group'` is a built-in container node that renders without handles. For styled C4 boundaries, write your own group node component instead and register it in `nodeTypes`.
- Give parents explicit `style.width/height` so children have a box to live in.

## 6. Architecture-tool design (IcePanel / Structurizr / EventCatalog-style)

The mistake agents make on these tools is bigger than the API: **they treat React Flow
nodes/edges as the source of truth.** For a C4 modelling tool they are not. The source of
truth is your domain model; the canvas is a *projection*.

- **Model vs view.** One semantic model (systems, containers, components, relationships, events). A "diagram" is a *view* over that model with per-view layout. The same element appears in multiple diagrams; a relationship is defined once. Persist the model and the layout (positions/sizes per diagram) separately. React Flow `nodes` = `map(modelElement → RF node)` for the current view, recomputed when the view or model changes.
- **C4 levels are different node/edge sets, not one giant graph.** Drill-down = swap the projected `nodes`/`edges` for the child level (or expand a group in place). Don't try to hold every level in a single flow.
- **Event sourcing fit.** RF interaction events (`onNodesChange` with `type: 'position'` on drag-stop, `onConnect`, `onNodesDelete`) are your command sources → emit domain events (`ElementMoved`, `RelationshipAdded`) → re-derive the view. Don't let RF's ephemeral state (selection, in-flight drag) leak into the event log; only commit on drag-stop / connect-end.
- **Auto-layout** with `elkjs` (hierarchical, handles nesting — better than dagre for C4) or `dagre` (simpler, flat). Both must read `node.measured.width/height`, so layout runs *after* first measure: either two-pass (render → `useNodesInitialized` → layout → set positions) or provide explicit `width/height` up front so layout has dimensions on pass one.
- **Persistence:** `toObject()` from the instance gives `{ nodes, edges, viewport }` for snapshotting a view; but prefer serializing your model + a layout map rather than RF's node shape, so the canvas library stays swappable.

## 7. Common errors → cause → fix

| Symptom | Cause | Fix |
|---|---|---|
| Blank canvas, no errors | No height on parent / missing CSS import | Size the container; import `dist/style.css` |
| "It looks like you have created a new nodeTypes object" warning + lag | `nodeTypes`/`edgeTypes` created inline each render | Hoist to module scope or `useMemo([],...)` |
| "Couldn't create edge for source/target handle" | Custom node missing `<Handle>`, or handle `id` mismatch | Add handles; match `sourceHandle`/`targetHandle` ids |
| "Node type not found. Using fallback type 'default'" | `node.type` ≠ a `nodeTypes` key | Align the strings exactly |
| Zustand "store not found" / `useReactFlow` returns nothing | Hook used outside provider, or two `@xyflow/react` versions | Wrap in `<ReactFlowProvider>`; dedupe the dependency |
| Layout collapses to a point | Read `node.width` instead of `node.measured.width` | Read `measured`, run layout after measure |
| Nodes drag but snap back | Uncontrolled vs controlled mismatch — not applying changes | Wire `onNodesChange`→`applyNodeChanges` (or use `useNodesState`) |
| Child node ignores its parent | Used `parentNode` (v11) | Rename to `parentId`; put parent before child in array |

## 8. Pre-flight check before returning code

- [ ] Imports: `@xyflow/react`, named `{ ReactFlow }`, CSS imported.
- [ ] Container has explicit height.
- [ ] `nodeTypes`/`edgeTypes` hoisted/memoized; every `node.type` has a matching key.
- [ ] No `parentNode`, no `node.width` for measured reads, no `.project()`.
- [ ] Hooks live under `<ReactFlowProvider>`.
- [ ] Changes applied via handlers (no in-place node mutation).
- [ ] For uncertainty on any feature: grepped `llms-full.txt`.
