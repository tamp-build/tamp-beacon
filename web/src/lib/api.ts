// Typed client over the tamp-beacon HTTP/JSON API. Mirrors the slice 5a
// surface (RBAC-filtered build listings, per-project rollups, project +
// member + token management). Cookie-based auth — every fetch implicitly
// carries the BeaconCookie session via `credentials: 'include'`.

export interface BuildSummary {
  id: number;
  seq: number;
  project_slug: string;
  organization: string;
  project_name: string;
  project_area: string | null;
  cli_version: string | null;
  started_unix_ns: number;
  duration_ns: number;
  exit_code: number;
  outcome: string;
  targets_total: number;
  targets_failed: number;
  commands_total: number;
  failure_target: string | null;
  host_os: string | null;
  host_arch: string | null;
  ci_vendor: string | null;
  peak_memory_b: number;
}

export interface TargetSummary {
  id: number;
  build_id: number;
  name: string;
  phase: string | null;
  status: string;
  started_unix_ns: number;
  duration_ns: number;
  cpu_time_ms: number;
  gc_allocated_b: number;
  gc_gen0: number;
  gc_gen1: number;
  gc_gen2: number;
  commands_count: number;
}

export interface CommandSummary {
  id: number;
  target_id: number;
  executable: string;
  args_count: number;
  exit_code: number;
  duration_ns: number;
  cpu_total_ms: number;
  peak_memory_b: number;
  stdout_bytes: number;
  stderr_bytes: number;
}

export interface EventSummary {
  id: number;
  build_id: number;
  target_id: number | null;
  command_id: number | null;
  name: string;
  at_unix_ns: number;
}

export interface BuildList {
  builds: BuildSummary[];
  next_seq: number;
}

export interface BuildDetail {
  build: BuildSummary;
  targets: TargetSummary[];
  commands: CommandSummary[];
  events: EventSummary[];
}

export interface ProjectSummary {
  slug: string;
  name: string;
  description: string | null;
  created_at: string;
  archived_at: string | null;
  member_count: number;
  my_role: 'admin' | 'viewer';
}

export interface ProjectMember {
  id: number;
  username: string;
  display_name: string;
  role: 'admin' | 'viewer';
  added_at: string;
}

export interface TokenSummary {
  id: number;
  label: string;
  created_at: string;
  last_used_at: string | null;
  revoked_at: string | null;
  created_by_username: string;
}

export interface MintedToken extends TokenSummary {
  token: string;
}

export interface Session {
  username: string;
  display_name: string;
  is_system_admin: boolean;
  last_login_at: string | null;
}

export interface TargetStat {
  name: string;
  avg_duration_ns: number;
  p95_duration_ns: number;
  samples: number;
}

export interface FlakyTarget {
  name: string;
  fail_rate: number;
  samples: number;
}

export class ApiError extends Error {
  constructor(public status: number, public body: unknown, message: string) {
    super(message);
  }
}

async function request<T>(url: string, init: RequestInit = {}): Promise<T> {
  const resp = await fetch(url, {
    credentials: 'include',
    headers: { accept: 'application/json', ...(init.headers ?? {}) },
    ...init,
  });
  if (!resp.ok) {
    let body: unknown = null;
    try {
      body = await resp.json();
    } catch {
      // Non-JSON body; leave null.
    }
    throw new ApiError(resp.status, body, `${resp.status} ${resp.statusText} fetching ${url}`);
  }
  if (resp.status === 204) return undefined as unknown as T;
  return (await resp.json()) as T;
}

function postJson<T>(url: string, body: unknown): Promise<T> {
  return request<T>(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
}

function patchJson<T>(url: string, body: unknown): Promise<T> {
  return request<T>(url, {
    method: 'PATCH',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
}

function del<T>(url: string): Promise<T> {
  return request<T>(url, { method: 'DELETE' });
}

export const api = {
  // ─── session ───────────────────────────────────────────────────────
  me: () => request<Session>('/me'),
  breakGlass: (body: { username: string; password: string }) =>
    postJson<Session>('/break-glass', body),
  logout: () => postJson<{ status: string }>('/logout', {}),

  // ─── projects ──────────────────────────────────────────────────────
  listProjects: () => request<{ projects: ProjectSummary[] }>('/api/projects'),
  getProject: (slug: string) => request<ProjectSummary>(`/api/projects/${slug}`),
  createProject: (body: { slug: string; name: string; description?: string | null }) =>
    postJson<ProjectSummary>('/api/projects', body),
  updateProject: (slug: string, body: { name?: string; description?: string | null }) =>
    patchJson<ProjectSummary>(`/api/projects/${slug}`, body),
  archiveProject: (slug: string) => del<void>(`/api/projects/${slug}`),

  // ─── members ───────────────────────────────────────────────────────
  listMembers: (slug: string) =>
    request<{ members: ProjectMember[] }>(`/api/projects/${slug}/members`),
  addMember: (slug: string, body: { username: string; role: 'admin' | 'viewer' }) =>
    postJson<ProjectMember>(`/api/projects/${slug}/members`, body),
  changeMemberRole: (slug: string, id: number, role: 'admin' | 'viewer') =>
    patchJson<ProjectMember>(`/api/projects/${slug}/members/${id}`, { role }),
  removeMember: (slug: string, id: number) =>
    del<void>(`/api/projects/${slug}/members/${id}`),

  // ─── ingest tokens ─────────────────────────────────────────────────
  listTokens: (slug: string) =>
    request<{ tokens: TokenSummary[] }>(`/api/projects/${slug}/tokens`),
  mintToken: (slug: string, label: string) =>
    postJson<MintedToken>(`/api/projects/${slug}/tokens`, { label }),
  revokeToken: (slug: string, id: number) =>
    del<void>(`/api/projects/${slug}/tokens/${id}`),

  // ─── builds ────────────────────────────────────────────────────────
  listBuilds: (params: {
    project?: string;
    outcome?: 'success' | 'failure';
    sinceSeq?: number;
    limit?: number;
  } = {}) => {
    const qs = new URLSearchParams();
    if (params.project) qs.set('project', params.project);
    if (params.outcome) qs.set('outcome', params.outcome);
    if (params.sinceSeq != null) qs.set('since_seq', String(params.sinceSeq));
    if (params.limit != null) qs.set('limit', String(params.limit));
    const url = `/api/builds${qs.toString() ? `?${qs.toString()}` : ''}`;
    return request<BuildList>(url);
  },
  listProjectBuilds: (slug: string, params: { sinceSeq?: number; limit?: number } = {}) => {
    const qs = new URLSearchParams();
    if (params.sinceSeq != null) qs.set('since_seq', String(params.sinceSeq));
    if (params.limit != null) qs.set('limit', String(params.limit));
    const url = `/api/projects/${slug}/builds${qs.toString() ? `?${qs.toString()}` : ''}`;
    return request<BuildList>(url);
  },
  getBuild: (id: number) => request<BuildDetail>(`/api/builds/${id}`),

  // ─── target rollups ────────────────────────────────────────────────
  slowestTargets: (slug: string, params: { limit?: number; sinceUnixNs?: number } = {}) => {
    const qs = new URLSearchParams();
    if (params.limit != null) qs.set('limit', String(params.limit));
    if (params.sinceUnixNs != null) qs.set('since_unix_ns', String(params.sinceUnixNs));
    const url = `/api/projects/${slug}/targets/slowest${qs.toString() ? `?${qs.toString()}` : ''}`;
    return request<{ targets: TargetStat[] }>(url);
  },
  flakiestTargets: (slug: string, params: { limit?: number; samplesMin?: number; sinceUnixNs?: number } = {}) => {
    const qs = new URLSearchParams();
    if (params.limit != null) qs.set('limit', String(params.limit));
    if (params.samplesMin != null) qs.set('samples_min', String(params.samplesMin));
    if (params.sinceUnixNs != null) qs.set('since_unix_ns', String(params.sinceUnixNs));
    const url = `/api/projects/${slug}/targets/flakiest${qs.toString() ? `?${qs.toString()}` : ''}`;
    return request<{ targets: FlakyTarget[] }>(url);
  },
};
