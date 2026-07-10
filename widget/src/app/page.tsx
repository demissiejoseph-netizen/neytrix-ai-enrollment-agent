'use client';

import ChatWidget from '@/components/ChatWidget';
import ClerkChat from '@/components/ClerkChat';

export default function Home() {
  const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';
  const tenantSlug = process.env.NEXT_PUBLIC_TENANT_SLUG ?? 'demo';
  const clerkPublishableKey = process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY;

  // Clerk is OPTIONAL: only wrap the widget when a publishable key is present.
  // Without one, the widget renders exactly as before — fully anonymous.
  if (clerkPublishableKey) {
    return (
      <ClerkChat
        publishableKey={clerkPublishableKey}
        tenantSlug={tenantSlug}
        apiBaseUrl={apiBaseUrl}
      />
    );
  }

  return <ChatWidget tenantSlug={tenantSlug} apiBaseUrl={apiBaseUrl} />;
}
