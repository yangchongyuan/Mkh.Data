﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mkh.Data.Core.Repository
{
    /// <summary>
    /// 是否存在
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    public abstract partial class RepositoryAbstract<TEntity>
    {
        protected async Task<bool> Exists(dynamic id, string tableName)
        {
            //没有主键的表无法使用Exists方法
            if (EntityDescriptor.PrimaryKey.IsNo)
                throw new ArgumentException("该实体没有主键，无法使用Exists方法~");

            var dynParams = GetIdParameter(id);
            var sql = _sql.GetExists(tableName);

            _logger?.LogDebug("ExistsAsync:{@sql}", sql);
            return await QuerySingleOrDefault<int>(sql, dynParams) > 0;
        }

        public Task<bool> Exists(dynamic id)
        {
            return Exists(id, null);
        }
    }
}
