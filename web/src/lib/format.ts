// Small formatters shared across pages. Kept here so the table cells
// stay terse and we don't drift in how "duration" or "CPU time" reads.

export function formatNs(ns: number): string {
  const ms = ns / 1_000_000;
  if (ms < 1000) return `${ms.toFixed(0)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  const m = s / 60;
  if (m < 60) return `${m.toFixed(1)} m`;
  const h = m / 60;
  return `${h.toFixed(1)} h`;
}

export function formatCpuMs(ms: number): string {
  if (ms < 1000) return `${ms.toFixed(0)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  const m = s / 60;
  if (m < 60) return `${m.toFixed(1)} m`;
  const h = m / 60;
  if (h < 24) return `${Math.floor(h)} h ${Math.round((h - Math.floor(h)) * 60)} m`;
  const d = h / 24;
  return `${Math.floor(d)} d ${Math.round((d - Math.floor(d)) * 24)} h`;
}

export function formatUnix(ns: number): string {
  return new Date(ns / 1_000_000).toLocaleString();
}
