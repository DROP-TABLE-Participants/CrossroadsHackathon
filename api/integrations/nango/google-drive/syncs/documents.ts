import { createSync } from "nango";
import { UnifiedDocument } from "../../src/models/unified-document.js";
import { syncGoogleDriveDocuments } from "../../src/files/google-drive/list-documents.js";

export default createSync({
  description: "Syncs extractable Google Drive file content and explicit content-state metadata into ERP Generator UnifiedDocument records.",
  version: "1.0.0",
  frequency: "every hour",
  autoStart: false,
  models: { UnifiedDocument },
  exec: async (nango) => {
    const documents = await syncGoogleDriveDocuments(nango);
    if (documents.length > 0) {
      await nango.batchSave(documents, "UnifiedDocument");
    }
  }
});

export {
  mapGoogleDriveFileToUnifiedDocument,
  mapGoogleDriveFilesToUnifiedDocuments,
  syncGoogleDriveDocuments
} from "../../src/files/google-drive/list-documents.js";
