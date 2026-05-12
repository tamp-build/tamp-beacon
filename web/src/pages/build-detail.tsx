import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatBytes, formatDurationNs, formatUnixNs } from '@/lib/utils';

export default function BuildDetailPage() {
  const { id } = useParams<{ id: string }>();
  const detail = useQuery({
    queryKey: ['build', id],
    queryFn: () => api.getBuild(Number(id)),
    enabled: !!id,
  });

  if (detail.isLoading) return <p className="text-muted-foreground">Loading…</p>;
  if (!detail.data) return <p className="text-muted-foreground">Build not found.</p>;
  const b = detail.data.build;

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-3">
            <Badge variant={b.outcome === 'success' ? 'success' : 'destructive'}>{b.outcome}</Badge>
            <span>{b.project_name}</span>
            {b.project_area && <span className="text-muted-foreground font-normal text-base">/ {b.project_area}</span>}
          </CardTitle>
        </CardHeader>
        <CardContent>
          <dl className="grid grid-cols-2 sm:grid-cols-4 gap-x-6 gap-y-2 text-sm">
            <Field label="Started" value={formatUnixNs(b.started_unix_ns)} />
            <Field label="Duration" value={formatDurationNs(b.duration_ns)} />
            <Field label="Exit code" value={String(b.exit_code)} />
            <Field label="Peak memory" value={formatBytes(b.peak_memory_b)} />
            <Field label="Targets" value={`${b.targets_total} (${b.targets_failed} failed)`} />
            <Field label="Commands" value={String(b.commands_total)} />
            <Field label="Host" value={`${b.host_os ?? '—'} ${b.host_arch ?? ''}`} />
            <Field label="CI" value={b.ci_vendor ?? 'local'} />
          </dl>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Targets</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Status</TableHead>
                <TableHead>Name</TableHead>
                <TableHead>Phase</TableHead>
                <TableHead>Duration</TableHead>
                <TableHead>CPU (ms)</TableHead>
                <TableHead>Allocated</TableHead>
                <TableHead>GC (0/1/2)</TableHead>
                <TableHead>Commands</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {detail.data.targets.map((t) => (
                <TableRow key={t.id}>
                  <TableCell>
                    <Badge variant={t.status === 'success' ? 'success' : t.status === 'failure' ? 'destructive' : 'secondary'}>
                      {t.status}
                    </Badge>
                  </TableCell>
                  <TableCell className="font-medium">{t.name}</TableCell>
                  <TableCell className="text-muted-foreground">{t.phase ?? '—'}</TableCell>
                  <TableCell>{formatDurationNs(t.duration_ns)}</TableCell>
                  <TableCell>{t.cpu_time_ms.toFixed(1)}</TableCell>
                  <TableCell>{formatBytes(t.gc_allocated_b)}</TableCell>
                  <TableCell>{t.gc_gen0}/{t.gc_gen1}/{t.gc_gen2}</TableCell>
                  <TableCell>{t.commands_count}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Commands</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Executable</TableHead>
                <TableHead>Args</TableHead>
                <TableHead>Exit</TableHead>
                <TableHead>Duration</TableHead>
                <TableHead>Peak mem</TableHead>
                <TableHead>stdout</TableHead>
                <TableHead>stderr</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {detail.data.commands.map((c) => (
                <TableRow key={c.id}>
                  <TableCell className="font-mono">{c.executable}</TableCell>
                  <TableCell>{c.args_count}</TableCell>
                  <TableCell>{c.exit_code}</TableCell>
                  <TableCell>{formatDurationNs(c.duration_ns)}</TableCell>
                  <TableCell>{formatBytes(c.peak_memory_b)}</TableCell>
                  <TableCell>{formatBytes(c.stdout_bytes)}</TableCell>
                  <TableCell>{formatBytes(c.stderr_bytes)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="font-medium">{value}</dd>
    </>
  );
}
