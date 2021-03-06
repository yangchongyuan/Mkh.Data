﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
#if DEBUG
using System.Runtime.CompilerServices;
#endif
using Mkh.Data.Abstractions.Adapter;
using Mkh.Data.Abstractions.Descriptors;
using Mkh.Data.Abstractions.Pagination;
using Mkh.Data.Abstractions.Queryable;
using Mkh.Data.Core.Extensions;
using Mkh.Data.Core.Internal;
using Mkh.Data.Core.Queryable.Internal;

#if DEBUG
[assembly: InternalsVisibleTo("Data.Adapter.MySql.Test")]
#endif
namespace Mkh.Data.Core.SqlBuilder
{
    /// <summary>
    /// 查询SQL生成器
    /// </summary>
    internal class QueryableSqlBuilder
    {
        public QueryBody QueryBody => _queryBody;

        public bool IsSingleEntity => _queryBody.Joins.Count < 2;

        private readonly QueryBody _queryBody;
        private readonly IDbAdapter _dbAdapter;

        public QueryableSqlBuilder(QueryBody queryBody)
        {
            _queryBody = queryBody;
            _dbAdapter = queryBody.Repository.DbContext.Adapter;
        }

        /// <summary>
        /// 生成列表语句
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public string BuildListSql(out IQueryParameters parameters)
        {
            parameters = new QueryParameters();

            var sqlBuilder = new StringBuilder();
            sqlBuilder.Append("SELECT ");
            ResolveSelect(sqlBuilder);
            sqlBuilder.Append(" FORM ");
            ResolveFrom(sqlBuilder, parameters);
            sqlBuilder.Append(" ");
            ResolveWhere(sqlBuilder, parameters);
            ResolveSort(sqlBuilder);

            return sqlBuilder.ToString();
        }

        #region ==解析查询列==

        public string ResolveSelect()
        {
            var sqlBuilder = new StringBuilder();
            ResolveSelect(sqlBuilder);
            return sqlBuilder.ToString();
        }

        /// <summary>
        /// 解析查询列
        /// </summary>
        /// <returns></returns>
        public void ResolveSelect(StringBuilder sqlBuilder)
        {
            //先解析出要排除的列
            var excludeColumns = ResolveSelectExcludeColumns();

            var select = _queryBody.Select;
            if (select.Mode == QuerySelectMode.UnKnown)
            {
                //解析所有实体
                ResolveSelectForEntity(sqlBuilder, 0, excludeColumns);
            }
            else if (select.Mode == QuerySelectMode.Sql)
            {
                //SQL语句
                sqlBuilder.Append(select.Sql);
            }
            else if (select.Mode == QuerySelectMode.Lambda)
            {
                //表达式
                var exp = select.Include.Body;
                switch (exp.NodeType)
                {
                    //返回的整个实体
                    case ExpressionType.Parameter:
                        ResolveSelectForEntity(sqlBuilder, 0, excludeColumns);
                        break;
                    //返回的某个列
                    case ExpressionType.MemberAccess:
                        ResolveSelectForMember(sqlBuilder, exp as MemberExpression, select.Include, excludeColumns);
                        if (sqlBuilder.Length > 0 && sqlBuilder[^1] == ',')
                        {
                            sqlBuilder.Remove(sqlBuilder.Length - 1, 1);
                        }
                        break;
                    //自定义的返回对象
                    case ExpressionType.New:
                        ResolveSelectForNew(sqlBuilder, exp as NewExpression, select.Include, excludeColumns);
                        break;
                }
            }

            //移除末尾的逗号
            if (sqlBuilder.Length > 0 && sqlBuilder[^1] == ',')
            {
                sqlBuilder.Remove(sqlBuilder.Length - 1, 1);
            }
        }

        /// <summary>
        /// 解析排除列的名称列表
        /// </summary>
        /// <returns></returns>
        public List<IColumnDescriptor> ResolveSelectExcludeColumns()
        {
            if (_queryBody.Select.Exclude != null)
            {
                var lambda = _queryBody.Select.Exclude;
                var body = lambda.Body;
                //整个实体
                if (body.NodeType == ExpressionType.Parameter)
                {
                    throw new ArgumentException("不能排除整个实体的列");
                }

                var list = new List<IColumnDescriptor>();

                //返回的单个列
                if (body.NodeType == ExpressionType.MemberAccess)
                {
                    var col = _queryBody.GetColumnDescriptor(body as MemberExpression, lambda);
                    if (col != null)
                        list.Add(col);

                    return list;
                }

                //自定义的返回对象
                if (body.NodeType == ExpressionType.New)
                {
                    var newExp = body as NewExpression;
                    for (var i = 0; i < newExp!.Arguments.Count; i++)
                    {
                        var arg = newExp.Arguments[i];
                        //实体
                        if (arg.NodeType == ExpressionType.Parameter)
                        {
                            throw new ArgumentException("不能排除整个实体");
                        }

                        //成员
                        if (arg.NodeType == ExpressionType.MemberAccess)
                        {
                            var col = _queryBody.GetColumnDescriptor(arg as MemberExpression, lambda);
                            if (col != null)
                                list.Add(col);
                        }
                    }
                }

                return list;
            }

            return null;
        }

        /// <summary>
        /// 解析查询列中的整个实体
        /// </summary>
        /// <param name="sqlBuilder"></param>
        /// <param name="index">多表连接时实体的下标</param>
        /// <param name="excludeColumns">排除列</param>
        public void ResolveSelectForEntity(StringBuilder sqlBuilder, int index = 0, List<IColumnDescriptor> excludeColumns = null)
        {
            var join = _queryBody.Joins[index];

            foreach (var col in join.EntityDescriptor.Columns)
            {
                if (excludeColumns != null && excludeColumns.Any(m => m == col))
                    continue;

                //单个实体时不需要别名
                sqlBuilder.Append(IsSingleEntity ? $"{_dbAdapter.AppendQuote(col.Name)}" : $"{join.Alias}.{_dbAdapter.AppendQuote(col.Name)}");

                sqlBuilder.AppendFormat(" AS {0},", _dbAdapter.AppendQuote(col.PropertyInfo.Name));
            }
        }

        /// <summary>
        /// 解析查询列中的自定义类型
        /// </summary>
        /// <param name="sqlBuilder"></param>
        /// <param name="newExp"></param>
        /// <param name="fullLambda"></param>
        /// <param name="excludeColumns"></param>
        public void ResolveSelectForNew(StringBuilder sqlBuilder, NewExpression newExp, LambdaExpression fullLambda, List<IColumnDescriptor> excludeColumns = null)
        {
            for (var i = 0; i < newExp.Arguments.Count; i++)
            {
                var arg = newExp.Arguments[i];
                var alias = newExp.Members![i].Name;
                //成员
                if (arg.NodeType == ExpressionType.MemberAccess)
                {
                    ResolveSelectForMember(sqlBuilder, arg as MemberExpression, fullLambda, excludeColumns, alias);
                    continue;
                }
                //实体
                if (arg.NodeType == ExpressionType.Parameter && arg is ParameterExpression parameterExp)
                {
                    ResolveSelectForEntity(sqlBuilder, fullLambda.Parameters.IndexOf(parameterExp), excludeColumns);
                    continue;
                }
                //方法
                if (arg.NodeType == ExpressionType.Call && arg is MethodCallExpression methodCallExp)
                {
                    var memberExp = methodCallExp!.Object as MemberExpression;
                    var columnName = _queryBody.GetColumnName(memberExp, fullLambda);
                    sqlBuilder.AppendFormat("{0} AS {1},", _dbAdapter.Method2Func(methodCallExp, columnName), _dbAdapter.AppendQuote(alias));
                }
            }
        }

        public void ResolveSelectForMember(StringBuilder sqlBuilder, MemberExpression memberExp, LambdaExpression fullLambda, List<IColumnDescriptor> excludeCols, string alias = null)
        {
            alias ??= memberExp.Member.Name;
            if (memberExp.Expression!.NodeType == ExpressionType.MemberAccess)
            {
                //分组查询
                if (_queryBody.IsGroupBy)
                {
                    //GroupByJoinDescriptor descriptor = _queryBody.GroupByPropertyList.FirstOrDefault(m =>
                    //    _sqlAdapter.AppendQuote(m.Alias) == alias || m.Name == memberExp.Member.Name);

                    //if (descriptor != null)
                    //{
                    //    var colName = _queryBody.GetColumnName(descriptor.Name, descriptor.JoinDescriptor);
                    //    sqlBuilder.AppendFormat("{0} AS {1},", colName, alias);
                    //}
                }
                else if (memberExp.Expression.Type.IsString())
                {
                    var columnName = _queryBody.GetColumnName(memberExp.Expression as MemberExpression, fullLambda);
                    sqlBuilder.AppendFormat("{0} AS {1},", _dbAdapter.Property2Func(memberExp.Member.Name, columnName), _dbAdapter.AppendQuote(alias));
                }
            }
            else
            {
                var column = _queryBody.GetColumnDescriptor(memberExp, fullLambda);
                if (excludeCols != null && excludeCols.Any(m => m == column))
                    return;

                var columnName = _queryBody.GetColumnName(memberExp, fullLambda);
                sqlBuilder.AppendFormat("{0} AS {1},", columnName, _dbAdapter.AppendQuote(alias));
            }
        }

        #endregion

        #region ==解析表==

        public string ResolveForm(IQueryParameters parameters)
        {
            var sqlBuilder = new StringBuilder();
            ResolveFrom(sqlBuilder, parameters);
            return sqlBuilder.ToString();
        }

        public void ResolveFrom(StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            var first = _queryBody.Joins.First();

            //
            if (_queryBody.Joins.Count == 1)
            {
                sqlBuilder.AppendFormat("{0}", _dbAdapter.AppendQuote(first.TableName));

                //附加SqlServer的NOLOCK特性
                if (_dbAdapter.Provider == DbProvider.SqlServer && first.NoLock)
                {
                    sqlBuilder.Append(" WITH (NOLOCK)");
                }

                return;
            }

            sqlBuilder.AppendFormat("{0} AS {1}", _dbAdapter.AppendQuote(first.TableName), first.Alias);
            //附加NOLOCK特性
            if (_dbAdapter.Provider == DbProvider.SqlServer && first.NoLock)
            {
                sqlBuilder.Append(" WITH (NOLOCK)");
            }

            for (var i = 1; i < _queryBody.Joins.Count; i++)
            {
                var join = _queryBody.Joins[i];
                switch (join.Type)
                {
                    case JoinType.Inner:
                        sqlBuilder.Append(" INNER");
                        break;
                    case JoinType.Right:
                        sqlBuilder.Append(" RIGHT");
                        break;
                    default:
                        sqlBuilder.Append(" LEFT");
                        break;
                }

                sqlBuilder.AppendFormat(" JOIN {0} AS {1}", _dbAdapter.AppendQuote(join.TableName), join.Alias);
                //附加SqlServer的NOLOCK特性
                if (_dbAdapter.Provider == DbProvider.SqlServer && first.NoLock)
                {
                    sqlBuilder.Append(" WITH (NOLOCK)");
                }

                sqlBuilder.Append(" ON ");
                sqlBuilder.Append(ResolveExpression(join.On, parameters));

                if (join.Type == JoinType.Inner)
                {
                    //过滤软删除
                    if (_queryBody.FilterDeleted && join.EntityDescriptor.IsSoftDelete)
                    {
                        sqlBuilder.AppendFormat(" AND {0}.{1} = {2}", join.Alias, _dbAdapter.AppendQuote(join.EntityDescriptor.GetDeletedColumnName()), _dbAdapter.BooleanFalseValue);
                    }

                    //添加租户过滤
                    if (_queryBody.FilterTenant && join.EntityDescriptor.IsTenant)
                    {
                        var x1 = _dbAdapter.AppendQuote(DbConstants.TENANT_COLUMN_NAME);
                        var tenantId = _queryBody.Repository.DbContext.AccountResolver.TenantId;
                        if (tenantId == null)
                        {
                            sqlBuilder.AppendFormat(" AND {0}.{1} IS NULL", join.Alias, x1);
                        }
                        else
                        {
                            sqlBuilder.AppendFormat(" AND {0}.{1} = '{2}'", join.Alias, x1, tenantId);
                        }
                    }
                }
            }
        }

        #endregion

        #region ==解析排序==

        /// <summary>
        /// 解析排序
        /// </summary>
        /// <returns></returns>
        public string ResolveSort()
        {
            if (!_queryBody.Sorts.Any())
                return string.Empty;

            var sqlBuilder = new StringBuilder();
            ResolveSort(sqlBuilder);
            return sqlBuilder.ToString();
        }

        public void ResolveSort(StringBuilder sqlBuilder)
        {
            var startLength = sqlBuilder.Length;
            foreach (var sort in _queryBody.Sorts)
            {
                if (sort.Mode == QuerySortMode.Lambda)
                {
                    ResolveSort(sqlBuilder, sort.Lambda.Body, sort.Lambda, sort.Type);
                }
                else
                {
                    sqlBuilder.AppendFormat(" {0} {1},", sort.Sql, sort.Type == SortType.Asc ? "ASC" : "DESC");
                }
            }

            if (startLength < sqlBuilder.Length)
            {
                sqlBuilder.Remove(sqlBuilder.Length - 1, 1);
            }
        }

        /// <summary>
        /// 解析排序
        /// </summary>
        private void ResolveSort(StringBuilder sqlBuilder, Expression exp, LambdaExpression fullExp, SortType sortType)
        {
            var nodeType = exp.NodeType;

            //OrderBy(m=>m.Id)
            if (nodeType == ExpressionType.MemberAccess)
            {
                ResolveSort(sqlBuilder, exp as MemberExpression, fullExp, sortType);
                return;
            }

            //m => m.Title.Length
            if (nodeType == ExpressionType.Convert && exp is UnaryExpression unaryExpression)
            {
                ResolveSort(sqlBuilder, unaryExpression.Operand as MemberExpression, fullExp, sortType);
                return;
            }

            //m => m.Title.Substring(1)
            if (nodeType == ExpressionType.Call)
            {
                var memberExp = (exp as MethodCallExpression)!.Object as MemberExpression;
                var columnName = _queryBody.GetColumnName(memberExp, fullExp);
                sqlBuilder.AppendFormat(" {0} {1},", _dbAdapter.Method2Func(exp as MethodCallExpression, columnName), sortType == SortType.Asc ? "ASC" : "DESC");
                return;
            }

            //OrderBy(m=>new {m.Title.Substring(2)})
            if (nodeType == ExpressionType.New && exp is NewExpression newExp)
            {
                foreach (var arg in newExp.Arguments)
                {
                    ResolveSort(sqlBuilder, arg, fullExp, sortType);
                }
            }
        }

        /// <summary>
        /// 解析成员表达式中的排序信息
        /// </summary>
        private void ResolveSort(StringBuilder sqlBuilder, MemberExpression memberExp, LambdaExpression fullLambda, SortType sortType)
        {
            switch (memberExp.Expression!.NodeType)
            {
                case ExpressionType.Parameter:
                    //OrderBy(m=>m.id)
                    var columnName = _queryBody.GetColumnName(memberExp, fullLambda);
                    sqlBuilder.AppendFormat(" {0} {1},", columnName, sortType == SortType.Asc ? "ASC" : "DESC");
                    break;
                case ExpressionType.MemberAccess:
                    //分组查询
                    if (_queryBody.IsGroupBy)
                    {
                        var groupBy = _queryBody.GroupBys.FirstOrDefault(m => m.Alias == memberExp.Member.Name);
                        if (groupBy != null)
                        {
                            columnName = _queryBody.GetColumnName(groupBy.Field, groupBy.Join);
                            sqlBuilder.AppendFormat(" {0} {1},", columnName, sortType == SortType.Asc ? "ASC" : "DESC");
                        }
                    }
                    else if (memberExp.Expression.Type.IsString())
                    {
                        columnName = _queryBody.GetColumnName(memberExp.Expression as MemberExpression, fullLambda);
                        sqlBuilder.AppendFormat(" {0} {1},", _dbAdapter.Property2Func(memberExp.Member.Name, columnName), sortType == SortType.Asc ? "ASC" : "DESC");
                    }

                    break;
            }
        }

        #endregion

        #region ==解析过滤条件==

        public string ResolveWhere(IQueryParameters parameters)
        {
            var sqlBuilder = new StringBuilder();
            ResolveWhere(sqlBuilder, parameters);
            return sqlBuilder.ToString();
        }

        public void ResolveWhere(StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            sqlBuilder.Append("WHERE ");
            //记录下当前sqlBuilder的长度，用于解析完成后比对
            var length = sqlBuilder.Length;

            //解析where条件
            if (_queryBody.Wheres.Any())
            {
                foreach (var w in _queryBody.Wheres)
                {
                    switch (w.Mode)
                    {
                        case QueryWhereMode.Lambda:
                            ResolveExpression(w.Lambda.Body, w.Lambda, sqlBuilder, parameters);
                            break;
                        case QueryWhereMode.SubQuery:
                            ResolveExpression(w.SubQueryColumn.Body, w.SubQueryColumn, sqlBuilder, parameters);
                            var subSql = w.SubQueryable.Sql(parameters);
                            sqlBuilder.AppendFormat("{0} ({1})", w.SubQueryOperator, subSql);
                            break;
                        case QueryWhereMode.Sql:
                            sqlBuilder.AppendFormat("({0})", w.Sql);
                            break;
                    }

                    sqlBuilder.Append(" AND ");
                }
            }

            //解析软删除
            ResolveWhereForSoftDelete(sqlBuilder);

            //解析租户
            ResolveWhereForTenant(sqlBuilder);

            /*
             * 1、当没有过滤条件是，需要移除WHERE关键字，此时sqlBuilder是以"WHERE "结尾，只需删除最后面的6位即可
             * 2、当有过滤条件时，需要移除最后面的AND关键字，此时sqlBuilder是以" AND "结尾，也是只需删除最后面的5位即可
             */
            var removeLength = length == sqlBuilder.Length ? 6 : 5;
            sqlBuilder.Remove(sqlBuilder.Length - removeLength, removeLength);
        }

        /// <summary>
        /// 解析软删除过滤条件
        /// </summary>
        /// <param name="sqlBuilder"></param>
        private void ResolveWhereForSoftDelete(StringBuilder sqlBuilder)
        {
            //未开启软删除过滤
            if (!_queryBody.FilterDeleted)
                return;

            //单表
            if (_queryBody.Joins.Count == 1)
            {
                var first = _queryBody.Joins.First();
                if (!first.EntityDescriptor.IsSoftDelete)
                    return;

                sqlBuilder.AppendFormat("{0} = {1} AND ", _dbAdapter.AppendQuote(first.EntityDescriptor.GetDeletedColumnName()), _dbAdapter.BooleanFalseValue);

                return;
            }

            //多表
            foreach (var join in _queryBody.Joins)
            {
                if (!join.EntityDescriptor.IsSoftDelete || join.Type == JoinType.Inner)
                    return;

                sqlBuilder.AppendFormat("{0}.{1} = {2} AND ", _dbAdapter.AppendQuote(join.Alias), _dbAdapter.AppendQuote(join.EntityDescriptor.GetDeletedColumnName()), _dbAdapter.BooleanFalseValue);
            }
        }

        /// <summary>
        /// 解析租户过滤条件
        /// </summary>
        /// <param name="sqlBuilder"></param>
        private void ResolveWhereForTenant(StringBuilder sqlBuilder)
        {
            //未开启过滤租户
            if (!_queryBody.FilterTenant)
                return;

            var tenantId = _queryBody.Repository.DbContext.AccountResolver.TenantId;
            //单表
            if (_queryBody.Joins.Count == 1)
            {
                var first = _queryBody.Joins.First();
                if (!first.EntityDescriptor.IsTenant)
                    return;

                //单表
                var x0 = _dbAdapter.AppendQuote(DbConstants.TENANT_COLUMN_NAME);
                if (tenantId == null)
                {
                    sqlBuilder.AppendFormat("{0} IS NULL AND ", x0);
                }
                else
                {
                    sqlBuilder.AppendFormat("{0} = '{1}' AND ", x0, tenantId);
                }

                return;
            }

            //多表
            foreach (var join in _queryBody.Joins)
            {
                if (!join.EntityDescriptor.IsTenant || join.Type == JoinType.Inner)
                    return;

                //多表时附加别名
                var x0 = _dbAdapter.AppendQuote(join.Alias);
                var x1 = _dbAdapter.AppendQuote(DbConstants.TENANT_COLUMN_NAME);
                if (tenantId == null)
                {
                    sqlBuilder.AppendFormat("{0}.{1} IS NULL AND ", x0, x1);
                }
                else
                {
                    sqlBuilder.AppendFormat("{0}.{1} = '{2}' AND", x0, x1, tenantId);
                }
            }
        }

        #endregion

        #region ==解析表达式==

        public string ResolveExpression(LambdaExpression expression, IQueryParameters parameters)
        {
            if (expression == null)
                return string.Empty;

            var sqlBuilder = new StringBuilder();

            ResolveExpression(expression, expression, sqlBuilder, parameters);

            return sqlBuilder.ToString();
        }

        public void ResolveExpression(Expression expression, LambdaExpression fullLambda, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            switch (expression.NodeType)
            {
                case ExpressionType.Lambda:
                    ResolveExpression((expression as LambdaExpression)!.Body, fullLambda, sqlBuilder, parameters);
                    break;
                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                    ResolveExpression((expression as UnaryExpression)!.Operand, fullLambda, sqlBuilder, parameters);
                    break;
                case ExpressionType.Add:
                case ExpressionType.AddChecked:
                case ExpressionType.Subtract:
                case ExpressionType.SubtractChecked:
                case ExpressionType.Multiply:
                case ExpressionType.MultiplyChecked:
                case ExpressionType.Divide:
                case ExpressionType.Modulo:
                case ExpressionType.And:
                case ExpressionType.AndAlso:
                case ExpressionType.Or:
                case ExpressionType.OrElse:
                case ExpressionType.LessThan:
                case ExpressionType.LessThanOrEqual:
                case ExpressionType.GreaterThan:
                case ExpressionType.GreaterThanOrEqual:
                case ExpressionType.Equal:
                case ExpressionType.NotEqual:
                case ExpressionType.Coalesce:
                case ExpressionType.ArrayIndex:
                case ExpressionType.RightShift:
                case ExpressionType.LeftShift:
                case ExpressionType.ExclusiveOr:
                    ResolveBinaryExpression(expression as BinaryExpression, fullLambda, sqlBuilder, parameters);
                    break;
                case ExpressionType.Constant:
                    AppendValue((expression as ConstantExpression)!.Value, sqlBuilder, parameters);
                    break;
                case ExpressionType.MemberAccess:
                    ResolveMemberExpression(expression as MemberExpression, fullLambda, sqlBuilder, parameters);
                    break;
                case ExpressionType.Call:
                    ResolveCallExpression(expression as MethodCallExpression, fullLambda, sqlBuilder, parameters);
                    break;
                case ExpressionType.MemberInit:
                    ResolveMemberInitExpression(expression, sqlBuilder);
                    break;
            }
        }

        /// <summary>
        /// 解析二元运算符表达式
        /// </summary>
        /// <param name="exp"></param>
        /// <param name="fullLambda"></param>
        /// <param name="sqlBuilder"></param>
        /// <param name="parameters"></param>
        private void ResolveBinaryExpression(BinaryExpression exp, LambdaExpression fullLambda, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            //针对简写方式的布尔类型解析m=>m.Deleted
            if (exp.Left.NodeType == ExpressionType.MemberAccess && exp.Left.Type == typeof(bool) && exp.NodeType != ExpressionType.Equal && exp.NodeType != ExpressionType.NotEqual)
            {
                ResolveMemberExpression(exp.Left as MemberExpression, fullLambda, sqlBuilder, parameters);
                sqlBuilder.Append(" = ");
                AppendValue(_dbAdapter.BooleanTrueValue, sqlBuilder, parameters);
            }
            //针对简写方式的布尔类型解析m=>!m.Deleted
            else if (exp.Left.NodeType == ExpressionType.Not)
            {
                ResolveMemberExpression((exp.Left as UnaryExpression)!.Operand as MemberExpression, fullLambda, sqlBuilder, parameters);
                sqlBuilder.Append(" = ");
                AppendValue(_dbAdapter.BooleanFalseValue, sqlBuilder, parameters);
            }
            else
            {
                ResolveExpression(exp.Left, fullLambda, sqlBuilder, parameters);
            }

            switch (exp.NodeType)
            {
                case ExpressionType.And:
                case ExpressionType.AndAlso:
                    sqlBuilder.Append(" AND ");
                    break;
                case ExpressionType.GreaterThan:
                    sqlBuilder.Append(" > ");
                    break;
                case ExpressionType.GreaterThanOrEqual:
                    sqlBuilder.Append(" >= ");
                    break;
                case ExpressionType.LessThan:
                    sqlBuilder.Append(" < ");
                    break;
                case ExpressionType.LessThanOrEqual:
                    sqlBuilder.Append(" <= ");
                    break;
                case ExpressionType.Equal:
                    sqlBuilder.Append(" = ");
                    break;
                case ExpressionType.OrElse:
                case ExpressionType.Or:
                    sqlBuilder.Append(" OR ");
                    break;
                case ExpressionType.NotEqual:
                    sqlBuilder.Append(" <> ");
                    break;
                case ExpressionType.Add:
                    sqlBuilder.Append(" + ");
                    break;
                case ExpressionType.Subtract:
                    sqlBuilder.Append(" - ");
                    break;
                case ExpressionType.Multiply:
                    sqlBuilder.Append(" * ");
                    break;
                case ExpressionType.Divide:
                    sqlBuilder.Append(" / ");
                    break;
            }

            ResolveExpression(exp.Right, fullLambda, sqlBuilder, parameters);
        }

        /// <summary>
        /// 解析成员表达式
        /// </summary>
        /// <param name="exp"></param>
        /// <param name="fullLambda"></param>
        /// <param name="sqlBuilder"></param>
        /// <param name="parameters"></param>
        private void ResolveMemberExpression(MemberExpression exp, LambdaExpression fullLambda, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            if (exp.Expression != null)
            {
                switch (exp.Expression.NodeType)
                {
                    case ExpressionType.Parameter:
                        sqlBuilder.Append(_queryBody.GetColumnName(exp, fullLambda));
                        break;
                    case ExpressionType.Constant:
                        var val = ResolveDynamicInvoke(exp);
                        AppendValue(val, sqlBuilder, parameters);
                        break;
                    case ExpressionType.MemberAccess:
                        if (exp.Expression is MemberExpression subMemberExp && subMemberExp.Expression!.NodeType == ExpressionType.Constant)
                        {
                            val = ResolveDynamicInvoke(exp);
                            AppendValue(val, sqlBuilder, parameters);
                            return;
                        }

                        //分组查询
                        if (_queryBody.IsGroupBy)
                        {
                            //var descriptor = _queryBody.GroupByPropertyList.FirstOrDefault(m => m.Alias == memberExp.Member.Name);
                            //if (descriptor != null)
                            //{
                            //    var colName = _queryBody.GetColumnName(descriptor.Name, descriptor.JoinDescriptor);
                            //    _sqlBuilder.AppendFormat("{0}", colName);
                            //    return;
                            //}
                        }
                        else if (exp.Expression.Type.IsString())
                        {
                            var columnName = _queryBody.GetColumnName(exp.Expression as MemberExpression, fullLambda);
                            sqlBuilder.AppendFormat("{0}", _dbAdapter.Property2Func(exp.Member.Name, columnName));
                        }
                        break;
                }

                //针对简写方式的布尔类型解析m=>m.Deleted
                if (exp == fullLambda.Body && exp.NodeType == ExpressionType.MemberAccess && exp.Type == typeof(bool))
                {
                    sqlBuilder.Append(" = ");
                    AppendValue(_dbAdapter.BooleanTrueValue, sqlBuilder, parameters);
                }
            }
        }

        /// <summary>
        /// 解析方法调用表达式
        /// </summary>
        /// <param name="exp"></param>
        /// <param name="fullLambda"></param>
        /// <param name="sqlBuilder"></param>
        /// <param name="parameters"></param>
        private void ResolveCallExpression(MethodCallExpression exp, LambdaExpression fullLambda, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            switch (exp.Method.Name)
            {
                case "Contains":
                    ResolveMethodForContains(exp, fullLambda, sqlBuilder, parameters);
                    break;
                case "StartsWith":
                    ResolveMethodForStartsWith(exp, fullLambda, sqlBuilder, parameters);
                    break;
                case "EndsWith":
                    ResolveMethodForEndsWith(exp, fullLambda, sqlBuilder, parameters);
                    break;
                case "Equals":
                    ResolveMethodForEquals(exp, fullLambda, sqlBuilder, parameters);
                    break;
                default:
                    if (exp.Object != null)
                    {
                        switch (exp.Object.NodeType)
                        {
                            case ExpressionType.Constant:
                                var val = ResolveDynamicInvoke(exp);
                                AppendValue(val, sqlBuilder, parameters);
                                break;
                            case ExpressionType.MemberAccess:
                                var memberExp = exp!.Object as MemberExpression;
                                var columnName = _queryBody.GetColumnName(memberExp, fullLambda);
                                sqlBuilder.AppendFormat("{0}", _dbAdapter.Method2Func(exp, columnName));
                                break;
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// 解析成员初始化表达式
        /// </summary>
        /// <param name="exp"></param>
        /// <param name="sqlBuilder"></param>
        private void ResolveMemberInitExpression(Expression exp, StringBuilder sqlBuilder)
        {

        }

        /// <summary>
        /// 解析Contains方法
        /// </summary>
        /// <param name="exp"></param>
        /// <param name="fullLambda"></param>
        /// <param name="sqlBuilder"></param>
        /// <param name="parameters"></param>
        private void ResolveMethodForContains(MethodCallExpression exp, LambdaExpression fullLambda, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            if (exp.Object is MemberExpression objExp)
            {
                if (objExp.Expression.NodeType == ExpressionType.Parameter)
                {
                    sqlBuilder.Append(_queryBody.GetColumnName(objExp, fullLambda));

                    string value;
                    if (exp.Arguments[0] is ConstantExpression c)
                    {
                        value = c.Value.ToString();
                    }
                    else
                    {
                        value = ResolveDynamicInvoke(exp.Arguments[0]).ToString();
                    }

                    sqlBuilder.Append(" LIKE ");

                    AppendValue($"%{value}%", sqlBuilder, parameters);
                }
                else if (objExp.Type.IsGenericType && exp.Arguments[0] is MemberExpression argExp && argExp.Expression!.NodeType == ExpressionType.Parameter)
                {
                    sqlBuilder.Append(_queryBody.GetColumnName(argExp, fullLambda));

                    sqlBuilder.Append(" IN (");

                    #region ==解析泛型集合==

                    var value = ResolveDynamicInvoke(objExp);
                    var valueType = objExp.Type.GetGenericArguments()[0];
                    var isValueType = false;
                    var list = new List<string>();
                    if (valueType.IsEnum)
                    {
                        isValueType = true;
                        var valueList = (IEnumerable)value;
                        if (valueList != null)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(Enum.Parse(valueType, c.ToString()).ToInt().ToString());
                            }
                        }
                    }
                    else if (valueType.IsString())
                    {
                        list = value as List<string>;
                    }
                    else if (valueType.IsGuid())
                    {
                        if (value is List<Guid> valueList)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(c.ToString());
                            }
                        }
                    }
                    else if (valueType.IsChar())
                    {
                        if (value is List<char> valueList)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(c.ToString());
                            }
                        }
                    }
                    else if (valueType.IsDateTime())
                    {
                        if (value is List<DateTime> valueList)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(c.ToString("yyyy-MM-dd HH:mm:ss"));
                            }
                        }
                    }
                    else if (valueType.IsInt())
                    {
                        isValueType = true;
                        if (value is List<int> valueList)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(c.ToString());
                            }
                        }
                    }
                    else if (valueType.IsLong())
                    {
                        isValueType = true;
                        if (value is List<long> valueList)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(c.ToString());
                            }
                        }
                    }
                    else if (valueType.IsDouble())
                    {
                        isValueType = true;
                        if (value is List<double> valueList)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(c.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                    }
                    else if (valueType.IsFloat())
                    {
                        isValueType = true;
                        if (value is List<float> valueList)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(c.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                    }
                    else if (valueType.IsDecimal())
                    {
                        isValueType = true;
                        if (value is List<decimal> valueList)
                        {
                            foreach (var c in valueList)
                            {
                                list.Add(c.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                    }

                    if (list == null)
                        return;

                    //值类型不带引号
                    if (isValueType)
                    {
                        for (var i = 0; i < list.Count; i++)
                        {
                            sqlBuilder.AppendFormat("{0}", list[i]);
                            if (i != list.Count - 1)
                            {
                                sqlBuilder.Append(",");
                            }
                        }
                    }
                    else
                    {
                        for (var i = 0; i < list.Count; i++)
                        {
                            sqlBuilder.AppendFormat("'{0}'", list[i].Replace("'", "''"));
                            if (i != list.Count - 1)
                            {
                                sqlBuilder.Append(",");
                            }
                        }
                    }

                    #endregion

                    sqlBuilder.Append(")");
                }
            }
            else if (exp.Arguments[0].Type.IsArray && exp.Arguments[1] is MemberExpression argExp && argExp.Expression!.NodeType == ExpressionType.Parameter)
            {
                sqlBuilder.Append(_queryBody.GetColumnName(argExp, fullLambda));
                sqlBuilder.Append(" IN (");

                #region ==解析数组==

                if (exp.Arguments[0] is MemberExpression member)
                {
                    var valueType = member.Type.GetElementType();
                    if (valueType != null)
                    {
                        var value = ResolveDynamicInvoke(member);
                        //是否是值类型
                        var isValueType = false;
                        var list = new List<string>();
                        if (valueType.IsEnum)
                        {
                            isValueType = true;
                            var valueList = (IEnumerable)value;
                            if (valueList != null)
                            {
                                foreach (var c in valueList)
                                {
                                    list.Add(Enum.Parse(valueType, c.ToString()).ToInt().ToString());
                                }
                            }
                        }
                        else if (valueType.IsString())
                        {
                            if (value is string[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val);
                                }
                            }
                        }
                        else if (valueType.IsGuid())
                        {
                            if (value is Guid[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString());
                                }
                            }
                        }
                        else if (valueType.IsChar())
                        {
                            if (value is char[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString());
                                }
                            }
                        }
                        else if (valueType.IsDateTime())
                        {
                            if (value is DateTime[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString("yyyy-MM-dd HH:mm:ss"));
                                }
                            }
                        }
                        else if (valueType.IsByte())
                        {
                            isValueType = true;
                            if (value is byte[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString());
                                }
                            }
                        }
                        else if (valueType.IsInt())
                        {
                            isValueType = true;
                            if (value is int[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString());
                                }
                            }
                        }
                        else if (valueType.IsLong())
                        {
                            isValueType = true;
                            if (value is long[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString());
                                }
                            }
                        }
                        else if (valueType.IsDouble())
                        {
                            isValueType = true;
                            if (value is double[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString(CultureInfo.InvariantCulture));
                                }
                            }
                        }
                        else if (valueType.IsShort())
                        {
                            isValueType = true;
                            if (value is short[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString());
                                }
                            }
                        }
                        else if (valueType.IsFloat())
                        {
                            isValueType = true;
                            if (value is float[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString(CultureInfo.InvariantCulture));
                                }
                            }
                        }
                        else if (valueType.IsDecimal())
                        {
                            isValueType = true;
                            if (value is decimal[] valueList)
                            {
                                foreach (var val in valueList)
                                {
                                    list.Add(val.ToString(CultureInfo.InvariantCulture));
                                }
                            }
                        }

                        //值类型不带引号
                        if (isValueType)
                        {
                            for (var i = 0; i < list.Count; i++)
                            {
                                sqlBuilder.AppendFormat("{0}", list[i]);
                                if (i != list.Count - 1)
                                {
                                    sqlBuilder.Append(",");
                                }
                            }
                        }
                        else
                        {
                            for (var i = 0; i < list.Count; i++)
                            {
                                sqlBuilder.AppendFormat("'{0}'", list[i].Replace("'", "''"));
                                if (i != list.Count - 1)
                                {
                                    sqlBuilder.Append(",");
                                }
                            }
                        }
                    }
                }

                #endregion

                sqlBuilder.Append(")");
            }
        }

        /// <summary>
        /// 解析StartsWith方法
        /// </summary>
        private void ResolveMethodForStartsWith(MethodCallExpression exp, LambdaExpression fullLambda, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            if (exp.Object is MemberExpression objExp && objExp.Expression.NodeType == ExpressionType.Parameter)
            {
                sqlBuilder.Append(_queryBody.GetColumnName(objExp, fullLambda));

                string value;
                if (exp.Arguments[0] is ConstantExpression c)
                {
                    value = c.Value.ToString();
                }
                else
                {
                    value = ResolveDynamicInvoke(exp.Arguments[0]).ToString();
                }

                sqlBuilder.Append(" LIKE ");

                AppendValue($"{value}%", sqlBuilder, parameters);
            }
        }

        /// <summary>
        /// 解析EndsWith方法
        /// </summary>
        private void ResolveMethodForEndsWith(MethodCallExpression exp, LambdaExpression fullLambda, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            if (exp.Object is MemberExpression objExp && objExp.Expression!.NodeType == ExpressionType.Parameter)
            {
                sqlBuilder.Append(_queryBody.GetColumnName(objExp, fullLambda));

                string value;
                if (exp.Arguments[0] is ConstantExpression c)
                {
                    value = c.Value!.ToString();
                }
                else
                {
                    value = ResolveDynamicInvoke(exp.Arguments[0]).ToString();
                }

                sqlBuilder.Append(" LIKE ");

                AppendValue($"%{value}", sqlBuilder, parameters);
            }
        }

        /// <summary>
        /// 解析Equals方法
        /// </summary>
        private void ResolveMethodForEquals(MethodCallExpression exp, LambdaExpression fullLambda, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            if (exp.Object is MemberExpression objExp && objExp.Expression!.NodeType == ExpressionType.Parameter)
            {
                sqlBuilder.Append(_queryBody.GetColumnName(objExp, fullLambda));

                sqlBuilder.Append(" = ");

                var arg = exp.Arguments[0];
                if (arg is ConstantExpression c)
                {
                    AppendValue(c.Value!.ToString(), sqlBuilder, parameters);
                }
                else if (arg.NodeType == ExpressionType.MemberAccess)
                {
                    ResolveMemberExpression(arg as MemberExpression, fullLambda, sqlBuilder, parameters);
                }
                else if (arg.NodeType == ExpressionType.Convert)
                {
                    ResolveExpression((arg as UnaryExpression)!.Operand, fullLambda, sqlBuilder, parameters);
                }
                else
                {
                    AppendValue(ResolveDynamicInvoke(arg).ToString(), sqlBuilder, parameters);
                }
            }
        }

        /// <summary>
        /// 解析动态代码
        /// </summary>
        /// <param name="exp"></param>
        private object ResolveDynamicInvoke(Expression exp)
        {
            var value = Expression.Lambda(exp).Compile().DynamicInvoke();
            if (exp.Type.IsEnum)
                value = value.ToInt();

            return value;
        }

        #endregion

        /// <summary>
        /// 附加值
        /// </summary>
        private void AppendValue(object value, StringBuilder sqlBuilder, IQueryParameters parameters)
        {
            if (value == null)
            {
                var len = sqlBuilder.Length;
                if (sqlBuilder[len - 1] == ' ' && sqlBuilder[len - 2] == '>' && sqlBuilder[len - 3] == '<')
                {
                    sqlBuilder.Remove(len - 3, 3);
                    sqlBuilder.Append("IS NOT NULL");
                    return;
                }

                if (sqlBuilder[len - 1] == ' ' && sqlBuilder[len - 2] == '=')
                {
                    sqlBuilder.Remove(len - 2, 2);
                    sqlBuilder.Append("IS NULL");
                }

                return;
            }

            var pName = parameters.Add(value);
            sqlBuilder.Append(_dbAdapter.AppendParameter(pName));
        }
    }
}
