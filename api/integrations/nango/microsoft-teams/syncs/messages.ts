import { createSync } from "nango";
import { UnifiedMessage } from "../../src/models/unified-message.js";
import { syncTeamsMessages } from "../../src/messages/microsoft-teams/list-messages.js";

export default createSync({
  description: "Syncs bounded Microsoft Teams channel messages into ERP Generator UnifiedMessage records.",
  version: "1.0.0",
  frequency: "every hour",
  autoStart: false,
  models: { UnifiedMessage },
  exec: async (nango) => {
    const messages = await syncTeamsMessages(nango);
    if (messages.length > 0) await nango.batchSave(messages, "UnifiedMessage");
  }
});
