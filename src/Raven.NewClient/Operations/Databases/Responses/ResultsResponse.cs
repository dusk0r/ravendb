﻿using Raven.NewClient.Abstractions.Indexing;
using Raven.NewClient.Client.Data;
using Raven.NewClient.Client.Indexing;
using Raven.NewClient.Data.Indexes;

namespace Raven.NewClient.Operations.Databases.Responses
{
    public abstract class ResultsResponse<T>
    {
        public T[] Results { get; set; }
    }

    public class GetIndexNamesResponse : ResultsResponse<string>
    {
    }

    public class GetTransformerNamesResponse : ResultsResponse<string>
    {
    }

    public class GetIndexesResponse : ResultsResponse<IndexDefinition>
    {
    }

    public class GetTransformersResponse : ResultsResponse<TransformerDefinition>
    {
    }

    public class GetIndexStatisticsResponse : ResultsResponse<IndexStats>
    {
    }

    public class GetApiKeysResponse : ResultsResponse<NamedApiKeyDefinition>
    {
    }
}