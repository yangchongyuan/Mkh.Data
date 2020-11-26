﻿using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mkh.Data.Abstractions.Adapter;

namespace Mkh.Data.Core.Repository
{
    public abstract partial class RepositoryAbstract<TEntity>
    {
        public Task<TEntity> Get(dynamic id)
        {
            return Get(id, null);
        }

        /// <summary>
        /// 查询单个实体
        /// </summary>
        /// <param name="id">实体ID</param>
        /// <param name="tableName">自定义表名称</param>
        /// <param name="rowLock">行锁</param>
        /// <param name="noLock">无锁(SqlServer有效)</param>
        /// <returns></returns>
        protected Task<TEntity> Get(dynamic id, string tableName, bool rowLock = false, bool noLock = false)
        {
            var dynParams = GetIdParameter(id);
            string sql;
            if (rowLock)
                sql = _sql.GetGetAndRowLock(tableName);
            else if (_adapter.Provider == DbProvider.SqlServer && noLock)
                sql = _sql.GetGetAndNoLock(tableName);
            else
                sql = _sql.GetGet(tableName);

            _logger?.LogDebug("Get:{@sql}", sql);
            return QuerySingleOrDefault<TEntity>(sql, dynParams);
        }
    }
}
