export default function ScoreGauge({ label, value, grade, delta, size = 84 }: { label: string; value: number; grade: string; delta?: number; size?: number }) {
  const r = (size - 10) / 2
  const c = 2 * Math.PI * r
  const pct = Math.max(0, Math.min(100, value)) / 100
  const color = value >= 75 ? 'var(--color-ok)' : value >= 50 ? 'var(--color-medium)' : value >= 30 ? 'var(--color-high)' : 'var(--color-critical)'
  return (
    <div className="flex items-center gap-3">
      <svg width={size} height={size} className="shrink-0">
        <circle cx={size / 2} cy={size / 2} r={r} stroke="var(--color-line)" strokeWidth={8} fill="none" />
        <circle cx={size / 2} cy={size / 2} r={r} stroke={color} strokeWidth={8} fill="none" strokeLinecap="round"
          strokeDasharray={`${c * pct} ${c * (1 - pct)}`} transform={`rotate(-90 ${size / 2} ${size / 2})`}
          style={{ transition: 'stroke-dasharray .6s ease' }} />
        <text x="50%" y="50%" dominantBaseline="central" textAnchor="middle" fill="var(--color-ink)" fontSize={size * 0.3} fontWeight={700}>{value}</text>
      </svg>
      <div>
        <div className="text-xs uppercase tracking-wide text-muted">{label}</div>
        <div className="flex items-baseline gap-2">
          <span className="text-lg font-semibold" style={{ color }}>Grade {grade}</span>
          {delta !== undefined && delta !== 0 && (
            <span className={`text-sm font-medium ${delta > 0 ? 'text-ok' : 'text-critical'}`}>{delta > 0 ? '+' : ''}{delta}</span>
          )}
        </div>
      </div>
    </div>
  )
}
