import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatDurationNs } from '@/lib/utils';

export default function TargetsPage() {
  const slowest = useQuery({ queryKey: ['targets', 'slowest'], queryFn: () => api.getSlowestTargets({ limit: 20 }) });
  const flaky = useQuery({ queryKey: ['targets', 'flaky'], queryFn: () => api.getFlakyTargets({ limit: 20 }) });

  return (
    <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
      <Card>
        <CardHeader>
          <CardTitle>Slowest targets</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Project</TableHead>
                <TableHead>Target</TableHead>
                <TableHead>Avg</TableHead>
                <TableHead>p95</TableHead>
                <TableHead>Samples</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {slowest.data?.targets.map((t) => (
                <TableRow key={`${t.project_name}/${t.name}`}>
                  <TableCell className="text-muted-foreground">{t.project_name}</TableCell>
                  <TableCell className="font-medium">{t.name}</TableCell>
                  <TableCell>{formatDurationNs(t.avg_duration_ns)}</TableCell>
                  <TableCell>{formatDurationNs(t.p95_duration_ns)}</TableCell>
                  <TableCell>{t.samples}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Flakiest targets</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Project</TableHead>
                <TableHead>Target</TableHead>
                <TableHead>Fail rate</TableHead>
                <TableHead>Samples</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {flaky.data?.targets.map((t) => (
                <TableRow key={`${t.project_name}/${t.name}`}>
                  <TableCell className="text-muted-foreground">{t.project_name}</TableCell>
                  <TableCell className="font-medium">{t.name}</TableCell>
                  <TableCell>{(t.fail_rate * 100).toFixed(1)}%</TableCell>
                  <TableCell>{t.samples}</TableCell>
                </TableRow>
              ))}
              {flaky.data && flaky.data.targets.length === 0 && (
                <TableRow>
                  <TableCell colSpan={4} className="text-center text-muted-foreground py-8">
                    No flaky targets yet (need at least 3 samples + 1 failure to qualify).
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  );
}
