import { useEffect, useMemo } from 'react'
import {
  Background, Controls, Handle, MiniMap, Position, ReactFlow, useEdgesState, useNodesState, useReactFlow, ReactFlowProvider,
  type Edge, type Node, type NodeProps,
} from '@xyflow/react'
import dagre from '@dagrejs/dagre'
import type { ArchitectureModel, BlastRadiusResult, Component, Finding, ImpactLevel } from '../types'
import { TypeIcon, heatToColor, impactColor } from '../ui'

type SfNodeData = {
  component: Component
  heat: number
  impact?: ImpactLevel
  impactReason?: string
  highlighted: boolean
  dimmed: boolean
  findingCount: number
}
type SfNode = Node<SfNodeData, 'sf'>

const NODE_W = 172
const NODE_H = 64

function SfNodeView({ data, selected }: NodeProps<SfNode>) {
  const c = data.component
  const border = data.impact ? impactColor[data.impact] : data.highlighted ? 'var(--color-accent)' : heatToColor(data.heat)
  const glow = data.impact === 'Down' || data.impact === 'Killed' ? '0 0 0 3px rgba(239,68,68,.25)' : data.impact === 'Degraded' ? '0 0 0 3px rgba(245,158,11,.25)' : selected ? '0 0 0 3px rgba(110,168,255,.3)' : 'none'
  const facts: string[] = []
  if (c.props.replicas != null) facts.push(`×${c.props.replicas}`)
  if (c.props.zones != null && c.props.zones > 1) facts.push(`${c.props.zones} AZ`)
  if (c.props.technology) facts.push(c.props.technology)
  return (
    <div title={data.impactReason ?? c.description ?? ''}
      className="rounded-xl border-2 bg-panel px-3 py-2 text-ink"
      style={{ width: NODE_W, height: NODE_H, borderColor: border, boxShadow: glow, opacity: data.dimmed ? 0.35 : 1, transition: 'all .25s' }}>
      <Handle type="target" position={Position.Left} style={{ opacity: 0 }} />
      <Handle type="source" position={Position.Right} style={{ opacity: 0 }} />
      <div className="flex items-center gap-2">
        <span className="grid h-7 w-7 shrink-0 place-items-center rounded-md bg-panel-2" style={{ color: border }}><TypeIcon type={c.type} /></span>
        <div className="min-w-0">
          <div className="truncate text-[13px] font-semibold leading-tight">{c.name}</div>
          <div className="truncate text-[10px] uppercase tracking-wide text-muted">{c.type}{facts.length ? ' · ' + facts.join(' · ') : ''}</div>
        </div>
      </div>
      <div className="mt-1 flex items-center gap-1 text-[10px] text-muted">
        {data.impact && data.impact !== 'Ok' && <span className="font-semibold uppercase" style={{ color: impactColor[data.impact] }}>{data.impact}</span>}
        {!data.impact && data.findingCount > 0 && <span>{data.findingCount} finding{data.findingCount > 1 ? 's' : ''}</span>}
        {c.props.hardcodedSecrets && <span className="text-critical">· secrets in config</span>}
      </div>
    </div>
  )
}

const nodeTypes = { sf: SfNodeView }

function layout(model: ArchitectureModel): Map<string, { x: number; y: number }> {
  const g = new dagre.graphlib.Graph()
  g.setGraph({ rankdir: 'LR', nodesep: 22, ranksep: 48, marginx: 20, marginy: 20 })
  g.setDefaultEdgeLabel(() => ({}))
  for (const c of model.components) g.setNode(c.id, { width: NODE_W, height: NODE_H })
  for (const f of model.flows) if (f.from !== f.to) g.setEdge(f.from, f.to)
  dagre.layout(g)
  const pos = new Map<string, { x: number; y: number }>()
  for (const c of model.components) {
    const n = g.node(c.id)
    pos.set(c.id, { x: n.x - NODE_W / 2, y: n.y - NODE_H / 2 })
  }
  return pos
}

export interface GraphProps {
  model: ArchitectureModel
  heat: Record<string, number>
  findings: Finding[]
  selectedFinding?: Finding | null
  blast?: BlastRadiusResult | null
  selectedComponentId?: string | null
  onSelectComponent: (id: string | null) => void
}

function GraphInner({ model, heat, findings, selectedFinding, blast, selectedComponentId, onSelectComponent }: GraphProps) {
  const { fitView } = useReactFlow()

  const { nodes: builtNodes, edges: builtEdges } = useMemo(() => {
    const pos = layout(model)
    const impacts = new Map(blast?.impacts.map(i => [i.componentId, i]) ?? [])
    const hlComponents = new Set(selectedFinding?.componentIds ?? [])
    const hlFlows = new Set(selectedFinding?.flowIds ?? [])
    const viaEdges = new Set<string>()
    if (blast) for (const i of blast.impacts) for (let k = 0; k + 1 < i.via.length; k++) viaEdges.add(`${i.via[k]}>${i.via[k + 1]}`)
    const countBy = new Map<string, number>()
    for (const f of findings) if (f.status === 'Open') for (const id of f.componentIds) countBy.set(id, (countBy.get(id) ?? 0) + 1)

    // Trust boundaries as group nodes wrapping their members. A boundary whose members are not
    // contiguous (typically "Internet": the user on the left, third parties on the right) would draw a
    // box over everything, so it is split into one small box per member instead.
    const groups: Node[] = []
    const parentOf = new Map<string, string>()
    const PAD = 26
    const addGroup = (gid: string, label: string, members: string[]) => {
      const xs = members.map(id => pos.get(id)!.x), ys = members.map(id => pos.get(id)!.y)
      const minX = Math.min(...xs) - PAD, minY = Math.min(...ys) - PAD - 16
      const maxX = Math.max(...xs) + NODE_W + PAD, maxY = Math.max(...ys) + NODE_H + PAD
      groups.push({
        id: gid, type: 'group', position: { x: minX, y: minY }, data: { label },
        style: { width: maxX - minX, height: maxY - minY, background: 'rgba(110,168,255,.04)', border: '1.5px dashed rgba(110,168,255,.35)', borderRadius: 16 },
        selectable: false, draggable: false,
      } as Node)
      for (const id of members) parentOf.set(id, gid)
    }
    for (const b of model.trustBoundaries) {
      const members = b.componentIds.filter(id => pos.has(id) && !parentOf.has(id))
      if (members.length === 0) continue
      const xs = members.map(id => pos.get(id)!.x), ys = members.map(id => pos.get(id)!.y)
      const minX = Math.min(...xs), minY = Math.min(...ys), maxX = Math.max(...xs) + NODE_W, maxY = Math.max(...ys) + NODE_H
      const memberSet = new Set(members)
      const byId = new Map(model.components.map(c => [c.id, c]))
      const isActor = (id: string) => { const t = byId.get(id)?.type; return t === 'User' || t === 'External' }
      // Actors (users, third parties) legitimately sit at both ends of the graph; a box around them would cover everything.
      const actorBoundary = members.every(isActor)
      const foreignInside = model.components.some(c => {
        if (memberSet.has(c.id) || isActor(c.id)) return false
        const p = pos.get(c.id); if (!p) return false
        const cx = p.x + NODE_W / 2, cy = p.y + NODE_H / 2
        return cx > minX && cx < maxX && cy > minY && cy < maxY
      })
      if (actorBoundary || foreignInside) members.forEach((id, i) => addGroup(`tb:${b.id}:${i}`, b.name, [id]))
      else addGroup(`tb:${b.id}`, b.name, members)
    }
    // Labels for groups (React Flow group nodes render no label by default).
    const labels: Node[] = groups.map(g => ({
      id: `${g.id}:label`, type: 'default', position: { x: 10, y: 6 }, parentId: g.id, data: { label: (g.data as { label: string }).label },
      style: { background: 'transparent', border: 'none', color: 'var(--color-accent)', fontSize: 11, fontWeight: 600, padding: 0, width: 'auto', textTransform: 'uppercase', letterSpacing: '.06em' },
      selectable: false, draggable: false, connectable: false,
    }))

    const nodes: Node[] = [...groups, ...labels, ...model.components.map(c => {
      const p = pos.get(c.id)!
      const parent = parentOf.get(c.id)
      const gp = parent ? groups.find(g => g.id === parent)!.position : { x: 0, y: 0 }
      const impact = impacts.get(c.id)
      return {
        id: c.id, type: 'sf', parentId: parent, extent: parent ? ('parent' as const) : undefined,
        position: { x: p.x - gp.x, y: p.y - gp.y },
        selected: c.id === selectedComponentId,
        data: {
          component: c, heat: heat[c.id] ?? 0, impact: impact?.level, impactReason: impact?.reason,
          highlighted: hlComponents.has(c.id), dimmed: !!selectedFinding && !hlComponents.has(c.id) && hlComponents.size > 0,
          findingCount: countBy.get(c.id) ?? 0,
        } satisfies SfNodeData,
      } as SfNode
    })]

    const edges: Edge[] = model.flows.filter(f => pos.has(f.from) && pos.has(f.to) && f.from !== f.to).map(f => {
      const label = [f.protocol, f.auth && f.auth !== 'none' ? f.auth : null].filter(Boolean).join(' · ')
      const hl = hlFlows.has(f.id)
      const via = viaEdges.has(`${f.from}>${f.to}`)
      const async = f.isSync === false
      const stroke = via ? 'var(--color-down)' : hl ? 'var(--color-accent)' : f.encrypted === false ? '#b45309' : '#4b5a7a'
      return {
        id: f.id, source: f.from, target: f.to, label: label || undefined, type: 'smoothstep',
        animated: async || via, style: { stroke, strokeWidth: hl || via ? 2.6 : 1.6, strokeDasharray: async ? '6 4' : undefined, opacity: selectedFinding && !hl ? 0.3 : 1 },
        labelStyle: { fill: hl ? 'var(--color-accent)' : 'var(--color-muted)', fontSize: 10 },
        labelBgStyle: { fill: 'var(--color-panel)', fillOpacity: 0.9 },
        markerEnd: { type: 'arrowclosed' as const, color: stroke },
      }
    })
    return { nodes, edges }
  }, [model, heat, findings, selectedFinding, blast, selectedComponentId])

  const [nodes, setNodes, onNodesChange] = useNodesState(builtNodes)
  const [edges, setEdges, onEdgesChange] = useEdgesState(builtEdges)
  useEffect(() => { setNodes(builtNodes); setEdges(builtEdges) }, [builtNodes, builtEdges, setNodes, setEdges])
  useEffect(() => { const t = setTimeout(() => fitView({ padding: 0.06, duration: 400 }), 50); return () => clearTimeout(t) }, [model, fitView])

  return (
    <ReactFlow
      nodes={nodes} edges={edges} nodeTypes={nodeTypes}
      onNodesChange={onNodesChange} onEdgesChange={onEdgesChange}
      onNodeClick={(_, n) => { if (n.type === 'sf') onSelectComponent(n.id) }}
      onPaneClick={() => onSelectComponent(null)}
      fitView minZoom={0.2} maxZoom={2} proOptions={{ hideAttribution: true }} nodesConnectable={false}
    >
      <Background color="#1c2740" gap={24} />
      <Controls showInteractive={false} />
      <MiniMap pannable zoomable style={{ width: 150, height: 90 }} nodeColor={n => (n.type === 'sf' ? heatToColor((n.data as SfNodeData).heat) : 'transparent')} maskColor="rgba(11,16,32,.7)" />
    </ReactFlow>
  )
}

export default function ArchitectureGraph(props: GraphProps) {
  return (
    <ReactFlowProvider>
      <GraphInner {...props} />
    </ReactFlowProvider>
  )
}
