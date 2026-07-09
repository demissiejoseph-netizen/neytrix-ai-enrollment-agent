'use client';

import ChatWidget from '@/components/ChatWidget';

export default function Home() {
  const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';
  const tenantSlug = process.env.NEXT_PUBLIC_TENANT_SLUG ?? 'demo';

  return <ChatWidget tenantSlug={tenantSlug} apiBaseUrl={apiBaseUrl} />;
}
