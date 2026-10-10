import type { UnifiedMessage } from "../../models/unified-message.js";
import { normalizeMessage } from "../message-normalizer.js";

export type TeamsSyncRuntime = {
  get<TResponse>(request: { endpoint: string; params?: Record<string, string | number> }): Promise<{ data: TResponse }>;
};

type GraphList<T> = { value?: T[] };
type Team = { id?: string; displayName?: string };
type Channel = { id?: string; displayName?: string };
export type TeamsMessage = {
  id?: string;
  createdDateTime?: string;
  from?: { user?: { id?: string; displayName?: string }; application?: { displayName?: string } };
  body?: { content?: string; contentType?: string };
  webUrl?: string;
  replyToId?: string;
};

export async function syncTeamsMessages(
  nango: TeamsSyncRuntime,
  maxTeams = 25,
  maxChannelsPerTeam = 50,
  maxMessagesPerChannel = 200
): Promise<UnifiedMessage[]> {
  const teams = (await nango.get<GraphList<Team>>({ endpoint: "/v1.0/me/joinedTeams" })).data.value ?? [];
  const messages: UnifiedMessage[] = [];

  for (const team of teams.slice(0, maxTeams)) {
    if (!team.id) continue;
    const channels = (await nango.get<GraphList<Channel>>({ endpoint: `/v1.0/teams/${team.id}/channels` })).data.value ?? [];
    for (const channel of channels.slice(0, maxChannelsPerTeam)) {
      if (!channel.id) continue;
      const response = await nango.get<GraphList<TeamsMessage>>({
        endpoint: `/v1.0/teams/${team.id}/channels/${channel.id}/messages`,
        params: { "$top": maxMessagesPerChannel }
      });
      for (const source of response.data.value ?? []) {
        const mapped = mapTeamsMessage(source, team, channel);
        if (mapped) messages.push(mapped);
      }
    }
  }

  return messages;
}

export function mapTeamsMessage(source: TeamsMessage, team: Team, channel: Channel): UnifiedMessage | null {
  return normalizeMessage({
    id: source.id ? `microsoft-teams:${team.id ?? "unknown"}:${channel.id ?? "unknown"}:${source.id}` : undefined,
    channelOrThread: `${team.displayName ?? team.id ?? "unknown"}/${channel.displayName ?? channel.id ?? "unknown"}`,
    author: source.from?.user?.displayName ?? source.from?.application?.displayName,
    text: htmlToText(source.body?.content),
    timestamp: source.createdDateTime,
    provider: "microsoft-teams",
    metadata: { teamId: team.id, channelId: channel.id, replyToId: source.replyToId, webUrl: source.webUrl },
    metadataKeys: ["teamId", "channelId", "replyToId", "webUrl"]
  });
}

function htmlToText(value?: string): string {
  return (value ?? "")
    .replace(/<br\s*\/?>/gi, "\n")
    .replace(/<[^>]+>/g, " ")
    .replace(/&nbsp;/gi, " ")
    .replace(/&amp;/gi, "&")
    .replace(/&lt;/gi, "<")
    .replace(/&gt;/gi, ">")
    .replace(/[ \t]+/g, " ");
}
