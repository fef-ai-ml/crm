import React from 'react';
import ReactDOM from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { router } from './router';
import { bindQueryCacheToSession } from './lib/queryCacheSession';
import './index.css';

const queryClient = new QueryClient();
// I-1/J26: one QueryClient per tab; clear it whenever the session's identity changes (including to null).
bindQueryCacheToSession(queryClient);

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </React.StrictMode>,
);
