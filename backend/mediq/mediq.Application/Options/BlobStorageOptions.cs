namespace mediq.Application.Options;

/// <summary>Selects + configures the IBlobStorage adapter. Dev defaults to the local filesystem; tests use
/// in-memory; prod sets an object-store provider (with provider-side SSE/KMS) out-of-band.</summary>
public sealed class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    /// <summary>'local_fs' (dev default) | 'in_memory' (tests). Prod adds 's3' / 'gcs' / 'azure_blob'.</summary>
    public string Provider { get; set; } = "local_fs";

    /// <summary>Root directory for the local_fs adapter (relative paths resolve from the process cwd).</summary>
    public string RootPath { get; set; } = "App_Data/blobs";
}
