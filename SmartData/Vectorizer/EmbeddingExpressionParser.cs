using SmartData.Data;
using System.Text.RegularExpressions;

namespace SmartData.Vectorizer
{
    public class EmbeddingExpressionParser
    {
        private readonly DataContext _dbContext;

        public EmbeddingExpressionParser(DataContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public string EvaluateExpression(object entity, Type entityType, string expression)
        {
            return Regex.Replace(expression, @"\{([^}]+)\}", match =>
            {
                var expr = match.Groups[1].Value.Trim();
                return EvaluateSingleExpression(entity, entityType, expr);
            });
        }

        private string EvaluateSingleExpression(object entity, Type entityType, string expression)
        {
            var navMatch = Regex.Match(expression, @"^\$\.(\w+)\[(\w+)\s*@\.\s*(\w+)\]$");
            if (navMatch.Success)
            {
                var collectionName = navMatch.Groups[1].Value;
                var alias = navMatch.Groups[2].Value;
                var propertyName = navMatch.Groups[3].Value;

                var collectionProp = entityType.GetProperty(collectionName);
                if (collectionProp == null || !typeof(IEnumerable<object>).IsAssignableFrom(collectionProp.PropertyType))
                    throw new InvalidOperationException($"Property {collectionName} is not a valid collection on {entityType.Name}.");

                var collection = collectionProp.GetValue(entity) as IEnumerable<object>;
                if (collection == null)
                {
                    var entry = _dbContext.Entry(entity);
                    var nav = entry.Collection(collectionName);
                    if (!nav.IsLoaded)
                    {
                        nav.Load();
                        collection = nav.CurrentValue as IEnumerable<object>;
                    }
                }

                if (collection == null) return string.Empty;

                var elementType = collectionProp.PropertyType.GetGenericArguments().FirstOrDefault();
                if (elementType == null) return string.Empty;

                var prop = elementType.GetProperty(propertyName);
                if (prop == null)
                    throw new InvalidOperationException($"Property {propertyName} not found on {elementType.Name}.");

                var values = collection
                    .Select(item => prop.GetValue(item)?.ToString() ?? string.Empty)
                    .Where(val => !string.IsNullOrEmpty(val));
                return string.Join(" ", values);
            }

            var propMatch = Regex.Match(expression, @"^\$\.(\w+)$");
            if (propMatch.Success)
            {
                var propName = propMatch.Groups[1].Value;
                var prop = entityType.GetProperty(propName);
                if (prop == null)
                    throw new InvalidOperationException($"Property {propName} not found on {entityType.Name}.");
                return prop.GetValue(entity)?.ToString() ?? string.Empty;
            }

            return expression;
        }
    }
}