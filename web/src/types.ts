export type ComponentType =
  | 'User' | 'Gateway' | 'LoadBalancer' | 'Cdn' | 'WebApp' | 'Api' | 'Worker' | 'Database' | 'Cache'
  | 'Queue' | 'ObjectStorage' | 'Identity' | 'Secrets' | 'Monitoring' | 'External'

export interface Evidence { source: string; path?: string | null; line?: number | null; snippet?: string | null }

export interface ComponentProps {
  replicas?: number | null; zones?: number | null; regions?: number | null
  hasBackup?: boolean | null; hasHealthCheck?: boolean | null; autoscale?: boolean | null
  encryptionAtRest?: boolean | null; publiclyExposed?: boolean | null; managed?: boolean | null
  rateLimited?: boolean | null; auditLogging?: boolean | null; inputValidation?: boolean | null
  hardcodedSecrets?: boolean | null; technology?: string | null; notes: string[]
}

export interface Component {
  id: string; name: string; type: ComponentType; tier?: string | null; description?: string | null
  props: ComponentProps; evidence: Evidence[]
}

export interface Flow {
  id: string; from: string; to: string; protocol?: string | null; auth?: string | null; isSync?: boolean | null
  timeoutMs?: number | null; retries?: number | null; circuitBreaker?: boolean | null; encrypted?: boolean | null
  description?: string | null; evidence: Evidence[]
}

export interface TrustBoundary { id: string; name: string; componentIds: string[] }

export interface ArchitectureModel {
  name: string; description?: string | null; components: Component[]; flows: Flow[]
  trustBoundaries: TrustBoundary[]; assumptions: string[]
}

export type Severity = 'Critical' | 'High' | 'Medium' | 'Low' | 'Info'
export type Confidence = 'Low' | 'Medium' | 'High'
export type FindingStatus = 'Open' | 'Fixed' | 'Accepted'
export type FindingCategory = 'Resilience' | 'Security'

export interface Finding {
  id: string; ruleId: string; category: FindingCategory; tag: string; severity: Severity; confidence: Confidence
  source: 'Rule' | 'Ai'; status: FindingStatus; title: string; description: string; rationale: string; mitigation: string
  componentIds: string[]; flowIds: string[]; evidence: Evidence[]; fixHint?: string | null
}

export interface ScoreCard {
  resilience: number; security: number; overall: number; resilienceGrade: string; securityGrade: string
  openBySeverity: Record<Severity, number>; componentHeat: Record<string, number>
}

export interface AiRisk { title: string; why: string; componentIds: string[]; relatedFindingIds: string[] }
export interface AiAnalysis {
  executiveSummary: string; businessImpact: string; topRisks: AiRisk[]; quickWins: string[]
  additionalFindings: unknown[]
}

export interface PatchOp { kind: string; id?: string | null; property?: string | null; value?: unknown; object?: unknown }
export interface CodeChange { path?: string | null; language: string; description: string; snippet: string }
export interface FixProposal {
  findingId: string; summary: string; explanation: string; patch: { ops: PatchOp[] }
  codeChanges: CodeChange[]; resolvesFindingIds: string[]; residualRisk: string
}

export type ProjectStatus = 'Queued' | 'Extracting' | 'Analyzing' | 'Reviewing' | 'Ready' | 'Error'
export interface LogEntry { at: string; level: string; message: string }
export interface HistoryPoint { at: string; action: string; resilience: number; security: number; openFindings: number }

export interface Project {
  id: string; name: string; createdAt: string; updatedAt: string; source: string; sourceRef?: string | null
  status: ProjectStatus; error?: string | null; aiProvider?: string | null
  model: ArchitectureModel; findings: Finding[]; score: ScoreCard; analysis?: AiAnalysis | null
  history: HistoryPoint[]; log: LogEntry[]; proposals: Record<string, FixProposal>
}

export interface ProjectSummary {
  id: string; name: string; source: string; status: ProjectStatus; createdAt: string
  resilience: number; security: number; openFindings: number; components: number
}

export type ImpactLevel = 'Killed' | 'Down' | 'Degraded' | 'Ok'
export interface ComponentImpact { componentId: string; name: string; level: ImpactLevel; reason: string; via: string[] }
export interface BlastRadiusResult {
  killedComponentId: string; impacts: ComponentImpact[]; downCount: number; degradedCount: number
  totalCount: number; blastRatio: number; summary: string
}

export interface Health { status: string; ai: { provider: string; available: boolean; error?: string | null }; projects: number; offline: boolean }
export interface RuleInfo { id: string; category: FindingCategory; tag: string; defaultSeverity: Severity; title: string; rationale: string; mitigation: string }
export interface SampleInfo { id: string; name: string; description: string }
