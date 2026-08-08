using System.Collections;
using System.Net;
using System.Text.Json;
using Avro.Generic;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Lakehold.Api.Endpoints;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>
/// Reads a bounded window of Confluent-wire-format Avro records and normalises each record into
/// LakeHold's existing connector JSON staging contract.
/// </summary>
/// <remarks>
/// Broker commits are deliberately disabled here. The runner must publish the resulting DuckLake
/// change and durably record its checkpoint before any offset becomes externally acknowledged.
/// </remarks>
internal sealed class KafkaAvroDataConnectorSource(
    ConnectorSecretResolver secrets,
    IOptions<ConnectorOptions> options) : IDataConnectorSource, IConnectorPostPublicationAcknowledger
{
    private IConsumer<Ignore, GenericRecord>? _consumer;
    private List<TopicPartitionOffset>? _pendingOffsets;
    public ConnectorAdapterManifest Manifest { get; } = new(
        "lakehold.kafka-avro",
        1,
        DataConnectorKind.KafkaAvro,
        new HashSet<DataConnectorReadMode> { DataConnectorReadMode.Incremental },
        new HashSet<DataConnectorAuthenticationKind>
        {
            DataConnectorAuthenticationKind.None,
            DataConnectorAuthenticationKind.KafkaSaslPlain,
        },
        SupportsSourceVersion: true);

    public async Task<ConnectorSourceResult> ReadAsync(
        ConnectorReadContext context,
        IDataConnectorRecordWriter destination,
        CancellationToken cancellationToken)
    {
        var settings = context.Connector.SourceSettings();
        var registryUrl = settings.SchemaRegistryUrl
            ?? throw new InvalidOperationException("Kafka Avro connectors require a schema registry URL.");
        var bootstrap = settings.KafkaBootstrapServers
            ?? throw new InvalidOperationException("Kafka Avro connectors require broker endpoints.");
        var topic = settings.KafkaTopic
            ?? throw new InvalidOperationException("Kafka Avro connectors require a topic.");
        var group = settings.KafkaConsumerGroup
            ?? throw new InvalidOperationException("Kafka Avro connectors require a consumer group.");

        var destinationError = await DataConnectorEndpoints.ValidateKafkaDestinationsAsync(
                bootstrap,
                new Uri(registryUrl, UriKind.Absolute),
                options.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (destinationError is not null)
        {
            throw new InvalidOperationException(destinationError);
        }
        _ = options.Value.TryGetKafkaEgressGateway(out var egress, out var egressError)
            ? egress
            : throw new InvalidOperationException(egressError);

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = group,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = true,
        };
        // Kafka metadata can advertise further brokers. librdkafka exposes this setting as a
        // native configuration key rather than a generated ConsumerConfig property.
        // librdkafka does not expose a supported broker SOCKS client configuration. Connect only
        // to the deployment-owned TCP gateway; it owns the allowed broker routes (including
        // advertised listeners) and may tunnel them through SOCKS outside the application.
        consumerConfig.BootstrapServers = egress.KafkaBootstrapGateway;
        var authentication = context.Connector.Authentication();
        // Broker SASL and Schema Registry Basic are distinct protocols. A protected registry must
        // not force a plaintext/TLS/SASL choice on the customer's Kafka listener.
        if (authentication.Kind == DataConnectorAuthenticationKind.KafkaSaslPlain)
        {
            var brokerHost = DataConnectorEndpoints.GetKafkaBootstrapHosts(bootstrap)[0];
            consumerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
            consumerConfig.SaslMechanism = SaslMechanism.Plain;
            consumerConfig.SaslUsername = await secrets.ResolveAsync(
                    authentication.UsernameSecretReference
                    ?? throw new InvalidOperationException("Kafka SASL/PLAIN authentication requires a username reference."),
                    context.TenantSlug,
                    context.CatalogName,
                    brokerHost,
                    cancellationToken)
                .ConfigureAwait(false);
            consumerConfig.SaslPassword = await secrets.ResolveAsync(
                    authentication.PasswordSecretReference
                    ?? throw new InvalidOperationException("Kafka SASL/PLAIN authentication requires a password reference."),
                    context.TenantSlug,
                    context.CatalogName,
                    brokerHost,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var registryConfig = new SchemaRegistryConfig { Url = registryUrl };
        if (!string.IsNullOrWhiteSpace(egress.SchemaRegistryCaCertificatePath))
        {
            // Confluent's supported CA bundle setting retains hostname and chain validation.
            // The connector never disables certificate verification for a private registry.
            registryConfig.SslCaLocation = egress.SchemaRegistryCaCertificatePath;
        }
        var registryUsernameReference = authentication.SchemaRegistryUsernameSecretReference
            ?? (authentication.Kind == DataConnectorAuthenticationKind.KafkaSaslPlain
                ? authentication.UsernameSecretReference
                : null);
        var registryPasswordReference = authentication.SchemaRegistryPasswordSecretReference
            ?? (authentication.Kind == DataConnectorAuthenticationKind.KafkaSaslPlain
                ? authentication.PasswordSecretReference
                : null);
        if (registryUsernameReference is not null && registryPasswordReference is not null)
        {
            var registryHost = new Uri(registryUrl, UriKind.Absolute).DnsSafeHost;
            var username = await secrets.ResolveAsync(registryUsernameReference, context.TenantSlug, context.CatalogName, registryHost, cancellationToken).ConfigureAwait(false);
            var password = await secrets.ResolveAsync(registryPasswordReference, context.TenantSlug, context.CatalogName, registryHost, cancellationToken).ConfigureAwait(false);
            registryConfig.BasicAuthCredentialsSource = AuthCredentialsSource.UserInfo;
            registryConfig.BasicAuthUserInfo = $"{username}:{password}";
        }
        using var registry = new CachedSchemaRegistryClient(
            registryConfig,
            new WebProxy(new Uri(egress.SchemaRegistryHttpProxy, UriKind.Absolute)));
        _consumer = new ConsumerBuilder<Ignore, GenericRecord>(consumerConfig)
            .SetValueDeserializer(new AvroDeserializer<GenericRecord>(registry).AsSyncOverAsync())
            .Build();
        _consumer.Subscribe(topic);

        // Kafka ordering is per partition, never per topic. Keep one next offset for every partition
        // observed in this batch; committing only the last consumed record would replay other
        // partitions after a successful DuckLake publication.
        var nextOffsets = new Dictionary<TopicPartition, Offset>();
        var limit = Math.Clamp(settings.PageSize, 1, 10_000);
        // Consumed records, staged rows, or neither. A tombstone advances the position without
        // staging a row, so "have we seen anything yet" is a different question from "do we have
        // rows", and only the first one says whether the group has finished joining.
        var consumed = 0;
        // Assignment is asynchronous. A first one-second poll commonly returns no record while
        // the group joins, so wait for the configured bounded request window rather than treating
        // that transport heartbeat as an empty source.
        var readDeadline = DateTimeOffset.UtcNow + options.Value.RequestTimeout;
        while (destination.Rows < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = _consumer.Consume(TimeSpan.FromSeconds(1));
            if (record is null)
            {
                if (DateTimeOffset.UtcNow < readDeadline)
                {
                    continue;
                }
                break;
            }
            if (record.IsPartitionEOF)
            {
                // EOF is per assigned partition and may be delivered while the group is still
                // establishing its starting position. Do not turn that transient marker into an
                // empty successful batch before the bounded read window has had a chance to see
                // a record. Once a batch has consumed records, EOF is the natural batch boundary.
                if (consumed == 0 && DateTimeOffset.UtcNow < readDeadline)
                {
                    continue;
                }
                break;
            }

            consumed++;

            nextOffsets[record.TopicPartition] = record.Offset + 1;
            destination.RecordSourceVersion($"{record.TopicPartition.Topic}:{record.TopicPartition.Partition.Value}:{record.Offset.Value}");
            if (record.Message.Value is null)
            {
                // A tombstone is a null-valued record, ordinary on a keyed topic. It carries no
                // fields to stage, and this adapter does not represent source deletions. Advance
                // past it: staging a literal `null` fails the key and not-null gates, which fails
                // the batch, which commits no offset — so every replay would re-read the same
                // record and the connector would never move again.
                continue;
            }

            await destination.WriteAsync(JsonSerializer.Serialize(ToPlain(record.Message.Value)), cancellationToken)
                .ConfigureAwait(false);
        }

        var checkpoint = nextOffsets.Count == 0
            ? context.Checkpoint
            : string.Join(",", nextOffsets
                .OrderBy(item => item.Key.Topic, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Partition.Value)
                .Select(item => $"{item.Key.Topic}:{item.Key.Partition.Value}:{item.Value.Value}"));
        _pendingOffsets = nextOffsets
            .Select(item => new TopicPartitionOffset(item.Key, item.Value))
            .ToList();
        return new ConnectorSourceResult(checkpoint, checkpoint, checkpoint);
    }

    public Task AcknowledgePublishedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_consumer is not null && _pendingOffsets is { Count: > 0 })
            {
                _consumer.Commit(_pendingOffsets);
            }
        }
        finally
        {
            DisposeConsumer();
        }

        return Task.CompletedTask;
    }

    public Task AbandonAsync()
    {
        DisposeConsumer();
        return Task.CompletedTask;
    }

    private void DisposeConsumer()
    {
        var consumer = _consumer;
        _pendingOffsets = null;
        _consumer = null;
        if (consumer is null)
        {
            return;
        }

        try
        {
            // Close leaves the group cleanly, but it talks to the broker — and the common reason to
            // be tearing down is that the broker is unreachable. Never let that failure replace the
            // outcome the caller is already reporting, and never let it skip Dispose.
            consumer.Close();
        }
        catch (KafkaException)
        {
        }
        finally
        {
            consumer.Dispose();
        }
    }

    private static object? ToPlain(object? value) => value switch
    {
        null => null,
        GenericRecord record => record.Schema.Fields.ToDictionary(
            field => field.Name,
            field => ToPlain(record[field.Name])),
        byte[] bytes => Convert.ToBase64String(bytes),
        IDictionary map => map.Cast<DictionaryEntry>().ToDictionary(
            entry => Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            entry => ToPlain(entry.Value)),
        IEnumerable sequence when value is not string => sequence.Cast<object?>().Select(ToPlain).ToArray(),
        _ => value,
    };
}
