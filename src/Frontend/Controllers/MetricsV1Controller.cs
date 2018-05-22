namespace Microsoft.HpcAcm.Frontend.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using T = System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using Microsoft.HpcAcm.Common.Dto;
    using Microsoft.HpcAcm.Common.Utilities;
    using Microsoft.WindowsAzure.Storage.Table;
    using Newtonsoft.Json;

    [Route("v1/metrics")]
    public class MetricsV1Controller : Controller
    {
        private readonly DataProvider provider;
        private readonly CloudUtilities utilities;

        public MetricsV1Controller(DataProvider provider, CloudUtilities utilities)
        {
            this.provider = provider;
            this.utilities = utilities;
        }

        // GET v1/metrics/cpu?lastnodeid=node1&count=50
        [HttpGet("{category}")]
        public async T.Task<IActionResult> GetMetricsAsync(
            string category = "cpu",
            [FromQuery] string lastNodeId = null,
            [FromQuery] int count = 1000,
            CancellationToken token = default(CancellationToken))
        {
            var partitionQuery = this.utilities.GetPartitionQueryString(this.utilities.MetricsValuesPartitionKey);
            var registrationEnd = this.utilities.MaxString;
            var registrationRangeQuery = this.utilities.GetRowKeyRangeString(lastNodeId, registrationEnd);

            var q = new TableQuery<DynamicTableEntity>()
                .Where(TableQuery.CombineFilters(
                    partitionQuery,
                    TableOperators.And,
                    registrationRangeQuery))
                .Select(new List<string>() { category });

            var metricsTable = this.utilities.GetMetricsTable();
            var entities = await metricsTable.QueryAsync(q, count, token);

            var list = entities.Select(r =>
            {
                if (r.Timestamp > DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5.0)
                    && r.Properties.TryGetValue(category, out EntityProperty values))
                {
                    try
                    {
                        return (r.RowKey, JsonConvert.DeserializeObject<Dictionary<string, double?>>(values.StringValue));
                    }
                    catch (Exception)
                    {
                        return (r.RowKey, new Dictionary<string, double?>());
                    }
                }
                else
                {
                    return (r.RowKey, new Dictionary<string, double?>());
                }
            });

            return new OkObjectResult(new Heatmap()
            {
                Category = category,
                Values = list.ToDictionary(l => l.Item1, l => l.Item2),
            });
        }

        // GET v1/metrics/categories
        [HttpGet("categories", Order = 0)]
        public async T.Task<IActionResult> GetCategoriesAsync(CancellationToken token)
        {
            var partitionQuery = this.utilities.GetPartitionQueryString(this.utilities.MetricsCategoriesPartitionKey);

            var q = new TableQuery<DynamicTableEntity>()
                .Where(partitionQuery)
                .Select(new List<string>());

            var metricsTable = this.utilities.GetMetricsTable();

            return new OkObjectResult((await metricsTable.QueryAsync(q, null, token)).Select(r => r.RowKey));
        }

    }
}
