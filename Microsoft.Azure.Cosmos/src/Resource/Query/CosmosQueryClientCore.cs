﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Json;
    using Microsoft.Azure.Cosmos.Query;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;

    internal class CosmosQueryClientCore : CosmosQueryClient
    {
        private readonly CosmosClientContext clientContext;
        private readonly CosmosContainerCore cosmosContainerCore;
        internal readonly IDocumentQueryClient DocumentQueryClient;

        internal CosmosQueryClientCore(
            CosmosClientContext clientContext,
            CosmosContainerCore cosmosContainerCore)
        {
            this.clientContext = clientContext ?? throw new ArgumentException(nameof(clientContext));
            this.cosmosContainerCore = cosmosContainerCore ?? throw new ArgumentException(nameof(cosmosContainerCore));
            this.DocumentQueryClient = clientContext.DocumentQueryClient ?? throw new ArgumentException(nameof(clientContext));
        }

        internal override IDocumentClientRetryPolicy GetRetryPolicy()
        {
            return this.DocumentQueryClient.ResetSessionTokenRetryPolicy.GetRequestPolicy();
        }

        internal override Task<CollectionCache> GetCollectionCacheAsync()
        {
            return this.DocumentQueryClient.GetCollectionCacheAsync();
        }

        internal override Task<IRoutingMapProvider> GetRoutingMapProviderAsync()
        {
            return this.DocumentQueryClient.GetRoutingMapProviderAsync();
        }

        internal override Task<QueryPartitionProvider> GetQueryPartitionProviderAsync(CancellationToken cancellationToken)
        {
            return this.DocumentQueryClient.GetQueryPartitionProviderAsync(cancellationToken);
        }

        internal override async Task<CosmosQueryResponse> ExecuteItemQueryAsync(
            Uri resourceUri,
            ResourceType resourceType,
            OperationType operationType,
            string containerResourceId,
            CosmosQueryRequestOptions requestOptions,
            SqlQuerySpec sqlQuerySpec,
            Action<CosmosRequestMessage> requestEnricher,
            CancellationToken cancellationToken)
        {
            CosmosResponseMessage message = await this.clientContext.ProcessResourceOperationStreamAsync(
                resourceUri: resourceUri,
                resourceType: resourceType,
                operationType: operationType,
                requestOptions: requestOptions,
                partitionKey: requestOptions.PartitionKey,
                cosmosContainerCore: this.cosmosContainerCore,
                streamPayload: this.clientContext.JsonSerializer.ToStream<SqlQuerySpec>(sqlQuerySpec),
                requestEnricher: requestEnricher,
                cancellationToken: cancellationToken);

            return this.GetCosmosElementResponse(
                requestOptions, 
                resourceType, 
                containerResourceId, 
                message);
        }

        internal override Task<Documents.ConsistencyLevel> GetDefaultConsistencyLevelAsync()
        {
            return this.DocumentQueryClient.GetDefaultConsistencyLevelAsync();
        }

        internal override Task<Documents.ConsistencyLevel?> GetDesiredConsistencyLevelAsync()
        {
            return this.DocumentQueryClient.GetDesiredConsistencyLevelAsync();
        }

        internal override Task EnsureValidOverwrite(Documents.ConsistencyLevel desiredConsistencyLevel)
        {
            return this.DocumentQueryClient.EnsureValidOverwrite(desiredConsistencyLevel);
        }

        internal override Task<PartitionKeyRangeCache> GetPartitionKeyRangeCache()
        {
            return this.DocumentQueryClient.GetPartitionKeyRangeCache();
        }

        internal override Task<List<PartitionKeyRange>> GetTargetPartitionKeyRangesByEpkString(
            string resourceLink,
            string collectionResourceId,
            string effectivePartitionKeyString)
        {
            return this.GetTargetPartitionKeyRanges(
                resourceLink,
                collectionResourceId,
                new List<Range<string>>
                {
                    Range<string>.GetPointRange(effectivePartitionKeyString)
                });
        }

        internal override async Task<List<PartitionKeyRange>> GetTargetPartitionKeyRanges(
            string resourceLink,
            string collectionResourceId,
            List<Range<string>> providedRanges)
        {
            if (string.IsNullOrEmpty(collectionResourceId))
            {
                throw new ArgumentNullException(nameof(collectionResourceId));
            }

            if (providedRanges == null ||
                !providedRanges.Any() ||
                providedRanges.Any(x => x == null))
            {
                throw new ArgumentNullException(nameof(providedRanges));
            }

            IRoutingMapProvider routingMapProvider = await this.GetRoutingMapProviderAsync();

            List<PartitionKeyRange> ranges = await routingMapProvider.TryGetOverlappingRangesAsync(collectionResourceId, providedRanges);
            if (ranges == null && PathsHelper.IsNameBased(resourceLink))
            {
                // Refresh the cache and don't try to re-resolve collection as it is not clear what already
                // happened based on previously resolved collection rid.
                // Return NotFoundException this time. Next query will succeed.
                // This can only happen if collection is deleted/created with same name and client was not restarted
                // in between.
                CollectionCache collectionCache = await this.GetCollectionCacheAsync();
                collectionCache.Refresh(resourceLink);
            }

            if (ranges == null)
            {
                throw new NotFoundException($"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}: GetTargetPartitionKeyRanges(collectionResourceId:{collectionResourceId}, providedRanges: {string.Join(",", providedRanges)} failed due to stale cache");
            }

            return ranges;
        }

        internal override bool ByPassQueryParsing()
        {
            return CustomTypeExtensions.ByPassQueryParsing();
        }

        private CosmosQueryResponse GetCosmosElementResponse(
            CosmosQueryRequestOptions requestOptions,
            ResourceType resourceType,
            string containerResourceId,
            CosmosResponseMessage cosmosResponseMessage)
        {
            using (cosmosResponseMessage)
            {
                if (!cosmosResponseMessage.IsSuccessStatusCode)
                {
                    return CosmosQueryResponse.CreateFailure(
                        CosmosQueryResponseMessageHeaders.ConvertToQueryHeaders(cosmosResponseMessage.Headers, resourceType, containerResourceId),
                        cosmosResponseMessage.StatusCode,
                        cosmosResponseMessage.RequestMessage,
                        cosmosResponseMessage.ErrorMessage,
                        cosmosResponseMessage.Error);
                }

                MemoryStream memoryStream;
                if (cosmosResponseMessage.Content is MemoryStream)
                {
                    memoryStream = (MemoryStream)cosmosResponseMessage.Content;
                }
                else
                {
                    memoryStream = new MemoryStream();
                    cosmosResponseMessage.Content.CopyTo(memoryStream);
                }

                long responseLengthBytes = memoryStream.Length;
                CosmosArray cosmosArray = CosmosElementSerializer.ToCosmosElements(
                    memoryStream, 
                    resourceType, 
                    requestOptions.CosmosSerializationOptions);

                int itemCount = cosmosArray.Count;
                return CosmosQueryResponse.CreateSuccess(
                    result: cosmosArray,
                    count: itemCount,
                    responseHeaders: CosmosQueryResponseMessageHeaders.ConvertToQueryHeaders(cosmosResponseMessage.Headers, resourceType, containerResourceId),
                    responseLengthBytes: responseLengthBytes);
            }
        }
    }
}
