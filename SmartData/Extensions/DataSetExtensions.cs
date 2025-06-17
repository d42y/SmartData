using SmartData.Configurations;
using SmartData.Tables;

namespace SmartData.Extensions
{
    public static class DataSetExtensions
    {
        public static async Task<List<TimeseriesData>> GetTimeseriesAsync<T>(
            this DataSet<T> dataSet,
            string entityId,
            string propertyName,
            DateTime startTime,
            DateTime endTime)
            where T : class
        {
            if (dataSet == null) throw new ArgumentNullException(nameof(dataSet));
            return await dataSet.GetTimeseriesAsync(entityId, propertyName, startTime, endTime);
        }
    }
}
