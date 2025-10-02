using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SmartData.Core.Helpers
{
    internal static class TableHelper
    {
        public static string ResolveTableName(IModel model, Type entityClrType)
        {
            var et = model.FindEntityType(entityClrType)
                     ?? throw new InvalidOperationException($"Unknown entity type {entityClrType.Name} in model.");
            return et.GetTableName()
                ?? throw new InvalidOperationException($"No table name for entity type {et.Name}.");
        }
    }
}
