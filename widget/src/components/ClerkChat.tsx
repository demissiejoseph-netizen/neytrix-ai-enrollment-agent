'use client';

import React from 'react';
import {
  ClerkProvider,
  SignInButton,
  SignedIn,
  SignedOut,
  UserButton,
  useAuth,
} from '@clerk/nextjs';
import ChatWidget from './ChatWidget';

interface ClerkChatProps {
  publishableKey: string;
  tenantSlug: string;
  apiBaseUrl: string;
}

// Bridges Clerk's session into ChatWidget: getToken() resolves the current
// session JWT (or null when signed out), which ChatWidget attaches as a bearer
// token. Signed-out users still get a fully functional anonymous widget.
function ClerkBridgedWidget({ tenantSlug, apiBaseUrl }: { tenantSlug: string; apiBaseUrl: string }) {
  const { getToken } = useAuth();

  return (
    <>
      <div className="fixed top-4 right-4 z-50 flex items-center gap-3">
        <SignedOut>
          <SignInButton mode="modal">
            <button className="text-sm px-3 py-1.5 rounded-full bg-blue-600 text-white hover:bg-blue-700 transition-colors">
              Sign in
            </button>
          </SignInButton>
        </SignedOut>
        <SignedIn>
          <UserButton afterSignOutUrl="/" />
        </SignedIn>
      </div>
      <ChatWidget
        tenantSlug={tenantSlug}
        apiBaseUrl={apiBaseUrl}
        getToken={() => getToken()}
      />
    </>
  );
}

export default function ClerkChat({ publishableKey, tenantSlug, apiBaseUrl }: ClerkChatProps) {
  return (
    <ClerkProvider publishableKey={publishableKey}>
      <ClerkBridgedWidget tenantSlug={tenantSlug} apiBaseUrl={apiBaseUrl} />
    </ClerkProvider>
  );
}
