import { QueryClient } from '@tanstack/react-query';

// Polling-only refresh cadence per the no-streaming policy. 5s default;
// individual queries can override (e.g. slower-changing Projects view goes to 15s).
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchInterval: 5000,
      refetchOnWindowFocus: true,
      retry: 1,
      staleTime: 2000,
    },
  },
});
