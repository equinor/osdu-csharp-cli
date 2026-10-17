using Equinor.OsduCsharpClient.CrsCatalog;
using Equinor.OsduCsharpClient.CrsConversion;
using Equinor.OsduCsharpClient.Dataset;
using Equinor.OsduCsharpClient.Entitlements;
using Equinor.OsduCsharpClient.Facade;
using Equinor.OsduCsharpClient.FileNamespace;
using Equinor.OsduCsharpClient.Indexer;
using Equinor.OsduCsharpClient.Legal;
using Equinor.OsduCsharpClient.Register;
using Equinor.OsduCsharpClient.SchemaService;
using Equinor.OsduCsharpClient.Search;
using Equinor.OsduCsharpClient.Storage;
using Equinor.OsduCsharpClient.UnitV3;
using Equinor.OsduCsharpClient.WellboreDdms;
using Equinor.OsduCsharpClient.Workflow;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Abstractions.Store;

namespace Equinor.OsduCli.Runtime;

/// <summary>
/// The service clients commands send through: <see cref="OsduClient"/>'s, over its adapters,
/// but reading a refusal their specs do not describe for the reason the service gave.
/// </summary>
/// <remarks>
/// Named as <see cref="OsduClient"/> names them, so a command reads
/// <c>context.Client.Storage</c> either way. See <see cref="ServiceError"/> for what was lost
/// without this.
/// </remarks>
public sealed class ServiceClients(OsduClient client) : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, IRequestAdapter> _adapters = [];
    private readonly Dictionary<Type, object> _clients = [];

    public CrsCatalogClient CrsCatalog => Get("crs_catalog", adapter => new CrsCatalogClient(adapter));
    public CrsConversionClient CrsConversion => Get("crs_conversion", adapter => new CrsConversionClient(adapter));
    public DatasetClient Dataset => Get("dataset", adapter => new DatasetClient(adapter));
    public EntitlementsClient Entitlements => Get("entitlements", adapter => new EntitlementsClient(adapter));
    public FileClient File => Get("file", adapter => new FileClient(adapter));
    public IndexerClient Indexer => Get("indexer", adapter => new IndexerClient(adapter));
    public LegalClient Legal => Get("legal", adapter => new LegalClient(adapter));
    public RegisterClient Register => Get("register", adapter => new RegisterClient(adapter));
    public SchemaServiceClient SchemaService => Get("schema_service", adapter => new SchemaServiceClient(adapter));
    public SearchClient Search => Get("search", adapter => new SearchClient(adapter));
    public StorageClient Storage => Get("storage", adapter => new StorageClient(adapter));
    public UnitV3Client UnitV3 => Get("unit_v3", adapter => new UnitV3Client(adapter));
    public WellboreDdmsClient WellboreDdms => Get("wellbore_ddms", adapter => new WellboreDdmsClient(adapter));
    public WorkflowClient Workflow => Get("workflow", adapter => new WorkflowClient(adapter));

    /// <summary>
    /// The adapter for <paramref name="service"/>, for a request a command builds itself. A
    /// caller's own error types still come first.
    /// </summary>
    public IRequestAdapter GetRequestAdapter(string service)
    {
        lock (_sync)
        {
            if (!_adapters.TryGetValue(service, out var adapter))
                _adapters[service] = adapter = new ReasonReadingAdapter(client.GetRequestAdapter(service));
            return adapter;
        }
    }

    private T Get<T>(string service, Func<IRequestAdapter, T> create) where T : class
    {
        var adapter = GetRequestAdapter(service);
        lock (_sync)
        {
            if (!_clients.TryGetValue(typeof(T), out var existing))
                _clients[typeof(T)] = existing = create(adapter);
            return (T)existing;
        }
    }

    public void Dispose() => client.Dispose();
}

/// <summary>
/// Sends through another adapter, reading every failure the caller has no error type for as a
/// <see cref="ServiceError"/>.
/// </summary>
/// <remarks>
/// Only a fallback: a status the caller maps, exactly or by its class (<c>4XX</c>), is read as
/// it was, and a caller's own <c>XXX</c> is kept.
/// </remarks>
internal sealed class ReasonReadingAdapter(IRequestAdapter inner) : IRequestAdapter
{
    private const string AnyStatus = "XXX";

    public ISerializationWriterFactory SerializationWriterFactory => inner.SerializationWriterFactory;

    public string? BaseUrl
    {
        get => inner.BaseUrl;
        set => inner.BaseUrl = value;
    }

    public void EnableBackingStore(IBackingStoreFactory backingStoreFactory) =>
        inner.EnableBackingStore(backingStoreFactory);

    public Task<ModelType?> SendAsync<ModelType>(RequestInformation requestInfo, ParsableFactory<ModelType> factory,
        Dictionary<string, ParsableFactory<IParsable>>? errorMapping = null, CancellationToken cancellationToken = default)
        where ModelType : IParsable =>
        inner.SendAsync(requestInfo, factory, WithFallback(errorMapping), cancellationToken);

    public Task<IEnumerable<ModelType>?> SendCollectionAsync<ModelType>(RequestInformation requestInfo,
        ParsableFactory<ModelType> factory, Dictionary<string, ParsableFactory<IParsable>>? errorMapping = null,
        CancellationToken cancellationToken = default)
        where ModelType : IParsable =>
        inner.SendCollectionAsync(requestInfo, factory, WithFallback(errorMapping), cancellationToken);

    public Task<ModelType?> SendPrimitiveAsync<ModelType>(RequestInformation requestInfo,
        Dictionary<string, ParsableFactory<IParsable>>? errorMapping = null, CancellationToken cancellationToken = default) =>
        inner.SendPrimitiveAsync<ModelType>(requestInfo, WithFallback(errorMapping), cancellationToken);

    public Task<IEnumerable<ModelType>?> SendPrimitiveCollectionAsync<ModelType>(RequestInformation requestInfo,
        Dictionary<string, ParsableFactory<IParsable>>? errorMapping = null, CancellationToken cancellationToken = default) =>
        inner.SendPrimitiveCollectionAsync<ModelType>(requestInfo, WithFallback(errorMapping), cancellationToken);

    public Task SendNoContentAsync(RequestInformation requestInfo,
        Dictionary<string, ParsableFactory<IParsable>>? errorMapping = null, CancellationToken cancellationToken = default) =>
        inner.SendNoContentAsync(requestInfo, WithFallback(errorMapping), cancellationToken);

    public Task<T?> ConvertToNativeRequestAsync<T>(RequestInformation requestInfo,
        CancellationToken cancellationToken = default) =>
        inner.ConvertToNativeRequestAsync<T>(requestInfo, cancellationToken);

    internal static Dictionary<string, ParsableFactory<IParsable>> WithFallback(
        Dictionary<string, ParsableFactory<IParsable>>? errorMapping)
    {
        if (errorMapping?.ContainsKey(AnyStatus) == true)
            return errorMapping;
        var mapping = errorMapping is null
            ? new Dictionary<string, ParsableFactory<IParsable>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ParsableFactory<IParsable>>(errorMapping, errorMapping.Comparer);
        mapping[AnyStatus] = ServiceError.CreateFromDiscriminatorValue;
        return mapping;
    }
}
