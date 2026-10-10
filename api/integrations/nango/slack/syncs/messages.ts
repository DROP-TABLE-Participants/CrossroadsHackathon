import { createSync } from "nango";
import { UnifiedMessage } from "../../src/models/unified-message.js";
import { syncSlackMessages } from "../../src/messages/slack/list-messages.js";

export default createSync({
  description: "Syncs bounded Slack conversation history into ERP Generator UnifiedMessage records.",
  version: "1.0.0",
  frequency: "every hour",
  autoStart: false,
  models: { UnifiedMessage },
  exec: async (nango) => {
    const messages = await syncSlackMessages(nango);
    if (messages.length > 0) await nango.batchSave(messages, "UnifiedMessage");
  }
});
