import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api, type BuildSummary } from '@/lib/api';
import { formatCpuMs, formatNs, formatUnix } from '@/lib/format';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';

export default function ConfigDetailPage() {
  const { slug = '', configSlug = '' } = useParams<{ slug: string; configSlug: string }>();

  const config = useQuery({
    queryKey: ['config', slug, configSlug],
    queryFn: () => api.getConfig(slug, configSlug),
    enabled: !!slug && !!configSlug,
    refetchInterval: 15_000,
  });
  const builds = useQuery({
    queryKey: ['configBuilds', slug, configSlug],
    queryFn: () => api.listConfigBuilds(slug, configSlug, { limit: 50 }),
    enabled: !!slug && !!configSlug,
    refetchInterval: 5_000,
  });
  const slowest = useQuery({
    queryKey: ['configSlowest', slug, configSlug],
    queryFn: () => api.slowestConfigTargets(slug, configSlug, { limit: 5 }),
    enabled: !!slug && !!configSlug,
    refetchInterval: 30_000,
  });
  const flakiest = useQuery({
    queryKey: ['configFlakiest', slug, configSlug],
    queryFn: () => api.flakiestConfigTargets(slug, configSlug, { limit: 5 }),
    enabled: !!slug && !!configSlug,
    refetchInterval: 30_000,
  });

  if (!slug || !configSlug) return null;

  return (
    <div className="space-y-6">
      <div>
        <Link to={`/projects/${slug}`} className="text-sm text-muted-foreground hover:text-foreground">
          ← Project
        </Link>
        <h1 className="text-2xl font-semibold mt-1">
          {config.data?.name ?? configSlug}
        </h1>
        <p className="font-mono text-xs text-muted-foreground mt-1">
          {slug}/{configSlug}
        </p>
        {config.data?.description && (
          <p className="text-sm text-muted-foreground mt-2">{config.data.description}</p>
        )}
        {config.data && (
          <div className="flex gap-6 mt-3 text-sm text-muted-foreground">
            <span>{config.data.total_builds} builds</span>
            <span>{formatCpuMs(config.data.cpu_time_ms_sum)} CPU</span>
            {config.data.last_build && (
              <span>
                last:{' '}
                <Badge
                  variant={config.data.last_build.outcome === 'success' ? 'default' : 'destructive'}
                >
                  {config.data.last_build.outcome}
                </Badge>{' '}
                {formatNs(config.data.last_build.duration_ns)}
              </span>
            )}
          </div>
        )}
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Recent builds</CardTitle>
        </CardHeader>
        <CardContent>
          {builds.isLoading && <p className="text-muted-foreground text-sm">Loading…</p>}
          {builds.data && builds.data.builds.length === 0 && (
            <p className="text-muted-foreground text-sm">No builds yet under this config.</p>
          )}
          {builds.data && builds.data.builds.length > 0 && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-16">Seq</TableHead>
                  <TableHead>Started</TableHead>
                  <TableHead>Duration</TableHead>
                  <TableHead>Outcome</TableHead>
                  <TableHead>Targets</TableHead>
                  <TableHead>CI</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {[...builds.data.builds].reverse().map((b: BuildSummary) => (
                  <TableRow key={b.id} className="cursor-pointer hover:bg-muted/30">
                    <TableCell className="font-mono">
                      <Link to={`/builds/${b.id}`}>{b.seq}</Link>
                    </TableCell>
                    <TableCell>
                      <Link to={`/builds/${b.id}`}>{formatUnix(b.started_unix_ns)}</Link>
                    </TableCell>
                    <TableCell>{formatNs(b.duration_ns)}</TableCell>
                    <TableCell>
                      <Badge variant={b.outcome === 'success' ? 'default' : 'destructive'}>
                        {b.outcome}
                      </Badge>
                    </TableCell>
                    <TableCell>
                      {b.targets_total - b.targets_failed}/{b.targets_total}
                    </TableCell>
                    <TableCell className="text-muted-foreground">{b.ci_vendor ?? '—'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Slowest targets</CardTitle>
          </CardHeader>
          <CardContent>
            {(slowest.data?.targets ?? []).length === 0 ? (
              <p className="text-muted-foreground text-sm">No target data yet.</p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Name</TableHead>
                    <TableHead>Avg</TableHead>
                    <TableHead>p95</TableHead>
                    <TableHead>Samples</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {slowest.data!.targets.map((t) => (
                    <TableRow key={t.name}>
                      <TableCell className="font-mono">{t.name}</TableCell>
                      <TableCell>{formatNs(t.avg_duration_ns)}</TableCell>
                      <TableCell>{formatNs(t.p95_duration_ns)}</TableCell>
                      <TableCell className="text-muted-foreground">{t.samples}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Flakiest targets</CardTitle>
          </CardHeader>
          <CardContent>
            {(flakiest.data?.targets ?? []).length === 0 ? (
              <p className="text-muted-foreground text-sm">
                No flaky targets — all targets succeed (or fewer than 3 samples).
              </p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Name</TableHead>
                    <TableHead>Fail rate</TableHead>
                    <TableHead>Samples</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {flakiest.data!.targets.map((t) => (
                    <TableRow key={t.name}>
                      <TableCell className="font-mono">{t.name}</TableCell>
                      <TableCell>{(t.fail_rate * 100).toFixed(0)}%</TableCell>
                      <TableCell className="text-muted-foreground">{t.samples}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
