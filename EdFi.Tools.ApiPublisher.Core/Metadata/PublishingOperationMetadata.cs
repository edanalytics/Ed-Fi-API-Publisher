using System.Collections.Generic;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Metadata;

public record PublishingOperationMetadata
{
    public long? CurrentChangeVersion { get; set; }
    public JObject? SourceVersionMetadata { get; set; }
    public JObject? TargetVersionMetadata { get; set; }
    public ChangeWindow? ChangeWindow { get; set; }
    public IReadOnlyDictionary<string, long> ResourceItemCountByPath { get; set; }
}