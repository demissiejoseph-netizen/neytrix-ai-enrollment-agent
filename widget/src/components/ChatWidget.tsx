'use client';

import React, { useState, useEffect, useRef, useCallback } from 'react';

interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: Date;
  suggestedActions?: string[];
}

interface ChatWidgetProps {
  tenantSlug: string;
  apiBaseUrl: string;
  primaryColor?: string;
  widgetTitle?: string;
  welcomeMessage?: string;
  position?: 'bottom-right' | 'bottom-left';
  /**
   * OPTIONAL. When provided, its resolved value is attached as a
   * `Authorization: Bearer <token>` header so the backend can link this
   * conversation to the signed-in guardian. Returning null (or omitting the prop
   * entirely) keeps the conversation fully anonymous — the backend treats a
   * missing/invalid token as an unauthenticated session, unchanged.
   */
  getToken?: () => Promise<string | null | undefined>;
}

interface ApiMessage {
  role: string;
  content: string;
  newState: string;
  requiresEscalation: boolean;
  suggestedActions: string[];
}

export default function ChatWidget({
  tenantSlug,
  apiBaseUrl,
  primaryColor = '#2563eb',
  widgetTitle = 'Enrollment Assistant',
  welcomeMessage = 'Hi! I can help you find and enroll in our programs. How can I help today?',
  position = 'bottom-right',
  getToken,
}: ChatWidgetProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [messages, setMessages] = useState<Message[]>([]);
  const [inputValue, setInputValue] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [sessionToken, setSessionToken] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const scrollToBottom = useCallback(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, []);

  useEffect(() => { scrollToBottom(); }, [messages, scrollToBottom]);

  useEffect(() => {
    if (isOpen && inputRef.current) {
      inputRef.current.focus();
    }
  }, [isOpen]);

  // Build request headers, attaching the optional Clerk bearer token when a
  // getToken callback is supplied AND resolves to a value. Any failure to obtain
  // a token is swallowed so the request still goes out anonymously.
  const buildHeaders = useCallback(async (): Promise<Record<string, string>> => {
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      'X-Tenant-Slug': tenantSlug,
    };
    if (getToken) {
      try {
        const token = await getToken();
        if (token) headers['Authorization'] = `Bearer ${token}`;
      } catch {
        /* stay anonymous on any token error */
      }
    }
    return headers;
  }, [tenantSlug, getToken]);

  const startSession = useCallback(async () => {
    try {
      const response = await fetch(`${apiBaseUrl}/api/v1/chat/sessions`, {
        method: 'POST',
        headers: await buildHeaders(),
        body: JSON.stringify({ channel: 'widget' }),
      });

      if (!response.ok) throw new Error('Failed to start session');

      const data = await response.json();
      setSessionToken(data.sessionToken);

      const welcomeMsg: Message = {
        id: crypto.randomUUID(),
        role: 'assistant',
        content: data.greetingMessage || welcomeMessage,
        timestamp: new Date(),
        suggestedActions: ['Tell me about programs', 'I want to enroll my child', 'I have a question'],
      };
      setMessages([welcomeMsg]);
    } catch (err) {
      setError('Unable to connect. Please try again.');
    }
  }, [apiBaseUrl, welcomeMessage, buildHeaders]);

  const handleOpen = useCallback(() => {
    setIsOpen(true);
    if (!sessionToken) {
      startSession();
    }
  }, [sessionToken, startSession]);

  const sendMessage = useCallback(async (content: string) => {
    if (!content.trim() || !sessionToken || isLoading) return;

    const userMsg: Message = {
      id: crypto.randomUUID(),
      role: 'user',
      content: content.trim(),
      timestamp: new Date(),
    };

    setMessages(prev => [...prev, userMsg]);
    setInputValue('');
    setIsLoading(true);
    setError(null);

    try {
      const response = await fetch(
        `${apiBaseUrl}/api/v1/chat/sessions/${sessionToken}/messages`,
        {
          method: 'POST',
          headers: await buildHeaders(),
          body: JSON.stringify({ content: content.trim() }),
        }
      );

      if (!response.ok) throw new Error('Message failed');

      const data: ApiMessage = await response.json();

      const assistantMsg: Message = {
        id: crypto.randomUUID(),
        role: 'assistant',
        content: data.content,
        timestamp: new Date(),
        suggestedActions: data.suggestedActions,
      };

      setMessages(prev => [...prev, assistantMsg]);
    } catch (err) {
      setError('Something went wrong. Please try again.');
    } finally {
      setIsLoading(false);
    }
  }, [sessionToken, isLoading, apiBaseUrl, buildHeaders]);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    sendMessage(inputValue);
  };

  const positionClass = position === 'bottom-right'
    ? 'bottom-4 right-4'
    : 'bottom-4 left-4';

  return (
    <div className={`fixed ${positionClass} z-50 flex flex-col items-end gap-3`}>
      {/* Chat panel */}
      {isOpen && (
        <div className="w-96 h-[600px] bg-white rounded-2xl shadow-2xl flex flex-col overflow-hidden border border-gray-200">
          {/* Header */}
          <div
            className="px-4 py-3 flex items-center justify-between text-white"
            style={{ backgroundColor: primaryColor }}
          >
            <div className="flex items-center gap-2">
              <div className="w-8 h-8 rounded-full bg-white/20 flex items-center justify-center">
                <span className="text-sm">AI</span>
              </div>
              <div>
                <p className="font-semibold text-sm">{widgetTitle}</p>
                <p className="text-xs opacity-80">Online</p>
              </div>
            </div>
            <button
              onClick={() => setIsOpen(false)}
              className="text-white/80 hover:text-white transition-colors"
              aria-label="Close chat"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>

          {/* Messages */}
          <div className="flex-1 overflow-y-auto p-4 space-y-3 bg-gray-50">
            {messages.map((msg) => (
              <div key={msg.id} className={`flex ${msg.role === 'user' ? 'justify-end' : 'justify-start'}`}>
                <div
                  className={`max-w-[80%] rounded-2xl px-4 py-2 text-sm ${
                    msg.role === 'user'
                      ? 'text-white rounded-br-sm'
                      : 'bg-white text-gray-800 rounded-bl-sm shadow-sm border border-gray-100'
                  }`}
                  style={msg.role === 'user' ? { backgroundColor: primaryColor } : {}}
                >
                  <p className="whitespace-pre-wrap">{msg.content}</p>
                  {msg.suggestedActions && msg.suggestedActions.length > 0 && (
                    <div className="mt-2 flex flex-wrap gap-1">
                      {msg.suggestedActions.map((action) => (
                        <button
                          key={action}
                          onClick={() => sendMessage(action)}
                          className="text-xs px-2 py-1 rounded-full border border-gray-200 bg-gray-50 hover:bg-gray-100 transition-colors"
                        >
                          {action}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              </div>
            ))}

            {isLoading && (
              <div className="flex justify-start">
                <div className="bg-white rounded-2xl rounded-bl-sm px-4 py-3 shadow-sm border border-gray-100">
                  <div className="flex gap-1">
                    <div className="w-2 h-2 bg-gray-400 rounded-full animate-bounce" style={{ animationDelay: '0ms' }} />
                    <div className="w-2 h-2 bg-gray-400 rounded-full animate-bounce" style={{ animationDelay: '150ms' }} />
                    <div className="w-2 h-2 bg-gray-400 rounded-full animate-bounce" style={{ animationDelay: '300ms' }} />
                  </div>
                </div>
              </div>
            )}

            {error && (
              <div className="text-xs text-red-500 text-center">{error}</div>
            )}

            <div ref={messagesEndRef} />
          </div>

          {/* Input */}
          <form onSubmit={handleSubmit} className="p-3 border-t border-gray-200 bg-white">
            <div className="flex items-center gap-2">
              <input
                ref={inputRef}
                type="text"
                value={inputValue}
                onChange={(e) => setInputValue(e.target.value)}
                placeholder="Type a message..."
                disabled={isLoading || !sessionToken}
                className="flex-1 text-sm px-3 py-2 rounded-full border border-gray-200 focus:outline-none focus:border-blue-400 disabled:opacity-50"
              />
              <button
                type="submit"
                disabled={!inputValue.trim() || isLoading || !sessionToken}
                className="w-9 h-9 rounded-full flex items-center justify-center text-white disabled:opacity-50 transition-opacity"
                style={{ backgroundColor: primaryColor }}
                aria-label="Send message"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 19l9 2-9-18-9 18 9-2zm0 0v-8" />
                </svg>
              </button>
            </div>
          </form>
        </div>
      )}

      {/* Toggle button */}
      <button
        onClick={isOpen ? () => setIsOpen(false) : handleOpen}
        className="w-14 h-14 rounded-full shadow-lg flex items-center justify-center text-white transition-transform hover:scale-105 active:scale-95"
        style={{ backgroundColor: primaryColor }}
        aria-label={isOpen ? 'Close chat' : 'Open chat'}
      >
        {isOpen ? (
          <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
          </svg>
        ) : (
          <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 10h.01M12 10h.01M16 10h.01M9 16H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-5l-5 5v-5z" />
          </svg>
        )}
      </button>
    </div>
  );
}
