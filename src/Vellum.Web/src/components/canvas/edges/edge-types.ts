import type { EdgeTypes } from '@xyflow/react'
import { C4Edge } from './c4-edge'
import { ImpactStepEdge } from './impact-step-edge'
import { TraceEdge } from './trace-edge'
import { GhostEdge } from './ghost-edge'

export const edgeTypes: EdgeTypes = {
  'c4-edge': C4Edge,
  'trace-edge': TraceEdge,
  'impact-step-edge': ImpactStepEdge,
  'ghost-edge': GhostEdge,
}
