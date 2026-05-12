import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

export function formatDurationNs(ns: number): string {
  if (!Number.isFinite(ns) || ns <= 0) return '—';
  const ms = ns / 1_000_000;
  if (ms < 1) return `${ns} ns`;
  if (ms < 1000) return `${ms.toFixed(0)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(2)} s`;
  const m = Math.floor(s / 60);
  const rem = s - m * 60;
  return `${m}m ${rem.toFixed(0)}s`;
}

export function formatUnixNs(ns: number): string {
  if (!Number.isFinite(ns) || ns <= 0) return '—';
  const ms = Math.floor(ns / 1_000_000);
  return new Date(ms).toLocaleString();
}

export function formatBytes(b: number): string {
  if (!Number.isFinite(b) || b <= 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  let v = b;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(1)} ${units[i]}`;
}
