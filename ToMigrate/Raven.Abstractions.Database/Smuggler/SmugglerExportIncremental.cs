using System.Collections.Generic;

using Raven.Abstractions.Data;

namespace Raven.Abstractions.Database.Smuggler
{
    public class SmugglerExportIncremental
    {
        public const string RavenDocumentKey = "Raven/Smuggler/Export/Incremental";

        public Dictionary<string, ExportIncremental> ExportIncremental { get; set; }

        public SmugglerExportIncremental()
        {
            ExportIncremental = new Dictionary<string, ExportIncremental>();
        }
    }

    public class ExportIncremental
    {
        public ExportIncremental()
        {
            LastDocsEtag = Etag.Empty;
        }

        public Etag LastDocsEtag { get; set; }

        public Etag LastFilesEtag { get; set; }
    }
}
