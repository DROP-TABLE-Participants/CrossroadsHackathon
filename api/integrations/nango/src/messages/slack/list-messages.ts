import type { UnifiedMessage } from "../../models/unified-message.js";
import { normalizeMessage } from "../message-normalizer.js";

export type SlackConversation = { id?: string; name?: string; is_im?: boolean; is_mpim?: boolean };
export type SlackMessage = { ts?: string; thread_ts?: string; user?: string; username?: string; text?: string; subtype?: string };

export type SlackSyncRuntime = {
  get<TResponse>(request: { endpoint: string; params?: string | Record<string, string | number | string[] | number[]> }): Promise<{ data: TResponse }>;
  log?(message: string): Promise<void> | void;
};

export async function syncSlackMessages(
  nango: SlackSyncRuntime,
  maxConversations = 50,
  maxMessagesPerConversation = 200
): Promise<UnifiedMessage[]> {
  const conversations = await listSlackConversations(nango, maxConversations);
  const messages: UnifiedMessage[] = [];

  for (const conversation of conversations) {
    if (!conversation.id) continue;
    const response = await nango.get<{ ok?: boolean; messages?: SlackMessage[] }>({
      endpoint: "/conversations.history",
      params: { channel: conversation.id, limit: maxMessagesPerConversation }
    });
    for (const source of response.data.messages ?? []) {
      const mapped = mapSlackMessage(source, conversation);
      if (mapped) messages.push(mapped);
    }
  }

  return messages;
}

export function mapSlackMessage(source: SlackMessage, conversation: SlackConversation): UnifiedMessage | null {
  return normalizeMessage({
    id: source.ts ? `slack:${conversation.id ?? "unknown"}:${source.ts}` : undefined,
    channelOrThread: source.thread_ts
      ? `${conversation.name ?? conversation.id ?? "unknown"}:${source.thread_ts}`
      : conversation.name ?? conversation.id,
    author: source.user ?? source.username,
    text: source.text,
    timestamp: slackTimestamp(source.ts),
    provider: "slack",
    metadata: { conversationId: conversation.id, threadTimestamp: source.thread_ts, subtype: source.subtype },
    metadataKeys: ["conversationId", "threadTimestamp", "subtype"]
  });
}

async function listSlackConversations(nango: SlackSyncRuntime, limit: number): Promise<SlackConversation[]> {
  const response = await nango.get<{ ok?: boolean; channels?: SlackConversation[] }>({
    endpoint: "/conversations.list",
    params: { limit, types: "public_channel,private_channel,mpim,im", exclude_archived: "true" }
  });
  return (response.data.channels ?? []).slice(0, limit);
}

function slackTimestamp(value?: string): string | undefined {
  if (!value) return undefined;
  const seconds = Number.parseFloat(value);
  return Number.isFinite(seconds) ? new Date(seconds * 1000).toISOString() : undefined;
}
