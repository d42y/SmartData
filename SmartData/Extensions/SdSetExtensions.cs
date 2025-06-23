using SmartData.Configurations;

namespace SmartData.Extensions
{
    public static class SdSetExtensions
    {
        public static async Task<List<TimeseriesResult>> GetTimeseriesAsync<T>(
            this SdSet<T> dataSet,
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
