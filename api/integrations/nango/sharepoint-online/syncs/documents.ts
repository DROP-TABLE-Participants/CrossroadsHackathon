import { createSync } from "nango";
import { UnifiedDocument } from "../../src/models/unified-document.js";
import { syncSharePointDocuments } from "../../src/files/sharepoint/list-documents.js";

export default createSync({
  description: "Syncs bounded, extractable SharePoint document-library content into ERP Generator UnifiedDocument records.",
  version: "1.0.0",
  frequency: "every hour",
  autoStart: false,
  models: { UnifiedDocument },
  exec: async (nango) => {
    const documents = await syncSharePointDocuments(nango);
    if (documents.length > 0) {
      await nango.batchSave(documents, "UnifiedDocument");
    }
  }
});

export {
  extractSharePointDriveItemContent,
  mapSharePointDriveItemToUnifiedDocument,
  mapSharePointDriveItemsToUnifiedDocuments,
  syncSharePointDocuments
} from "../../src/files/sharepoint/list-documents.js";
