// Typed client over the tamp-beacon HTTP/JSON API. Mirrors the C# Tamp.Beacon.Sdk.BeaconClient.

export interface BuildSummary {
  id: number;
  seq: number;
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

export interface ProjectFacet {
  organization: string;
  name: string;
  area: string | null;
  last_seen_unix_ns: number;
  builds_count: number;
  failed_count: number;
}

export interface ProjectList {
  projects: ProjectFacet[];
}

export interface OrganizationFacet {
  name: string;
  projects_count: number;
  builds_count: number;
  failed_count: number;
  last_seen_unix_ns: number;
}

export interface OrganizationList {
  organizations: OrganizationFacet[];
}

export interface TargetStat {
  name: string;
  project_name: string;
  avg_duration_ns: number;
  p95_duration_ns: number;
  samples: number;
}

export interface FlakyTarget {
  name: string;
  project_name: string;
  fail_rate: number;
  samples: number;
}

export interface HealthStatus {
  status: string;
  db_path: string;
  rows_total: number;
  vapid_public_key: string;
}

async function getJson<T>(url: string): Promise<T> {
  const resp = await fetch(url, { headers: { accept: 'application/json' } });
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText} fetching ${url}`);
  return (await resp.json()) as T;
}

export const api = {
  getBuilds: (params: { organization?: string; project?: string; area?: string; sinceSeq?: number; limit?: number } = {}) => {
    const qs = new URLSearchParams();
    if (params.organization) qs.set('organization', params.organization);
    if (params.project) qs.set('project', params.project);
    if (params.area) qs.set('area', params.area);
    if (params.sinceSeq != null) qs.set('since_seq', String(params.sinceSeq));
    if (params.limit != null) qs.set('limit', String(params.limit));
    const url = `/api/builds${qs.toString() ? `?${qs.toString()}` : ''}`;
    return getJson<BuildList>(url);
  },
  getBuild: (id: number) => getJson<BuildDetail>(`/api/builds/${id}`),
  getOrganizations: () => getJson<OrganizationList>('/api/organizations'),
  getProjects: () => getJson<ProjectList>('/api/projects'),
  getSlowestTargets: (params: { project?: string; sinceUnixNs?: number; limit?: number } = {}) => {
    const qs = new URLSearchParams();
    if (params.project) qs.set('project', params.project);
    if (params.sinceUnixNs != null) qs.set('since_unix_ns', String(params.sinceUnixNs));
    if (params.limit != null) qs.set('limit', String(params.limit));
    return getJson<{ targets: TargetStat[] }>(`/api/targets/slowest${qs.toString() ? `?${qs.toString()}` : ''}`);
  },
  getFlakyTargets: (params: { project?: string; sinceUnixNs?: number; limit?: number } = {}) => {
    const qs = new URLSearchParams();
    if (params.project) qs.set('project', params.project);
    if (params.sinceUnixNs != null) qs.set('since_unix_ns', String(params.sinceUnixNs));
    if (params.limit != null) qs.set('limit', String(params.limit));
    return getJson<{ targets: FlakyTarget[] }>(`/api/targets/flakiest${qs.toString() ? `?${qs.toString()}` : ''}`);
  },
  getHealth: () => getJson<HealthStatus>('/healthz'),
  subscribePush: async (body: {
    endpoint: string;
    keys: { p256dh: string; auth: string };
    project_filter?: string;
    area_filter?: string;
  }) => {
    const resp = await fetch('/api/push/subscribe', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!resp.ok) throw new Error(`push subscribe failed: ${resp.status}`);
  },
};
