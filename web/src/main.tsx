import React from 'react';
import ReactDOM from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter } from 'react-router-dom';
import { queryClient } from '@/lib/query-client';
import App from './App';
import './index.css';

// Service worker registration for Web Push. Falls through silently if the
// browser doesn't support service workers (legacy browsers, plain http on
// non-localhost contexts, etc.) — the dashboard still works without push.
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch((err) => {
    // eslint-disable-next-line no-console
    console.warn('service worker registration failed', err);
  });
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </React.StrictMode>,
);
