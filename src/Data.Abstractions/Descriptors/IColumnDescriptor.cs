﻿using System.Reflection;

namespace Mkh.Data.Abstractions.Descriptors
{
    /// <summary>
    /// 列信息描述符
    /// </summary>
    public interface IColumnDescriptor
    {
        /// <summary>
        /// 列名
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 属性名称
        /// </summary>
        string PropertyName { get; }

        /// <summary>
        /// 列类型名称
        /// </summary>
        string TypeName { get; set; }

        /// <summary>
        /// 默认值
        /// </summary>
        string DefaultValue { get; set; }

        /// <summary>
        /// 属性信息
        /// </summary>
        PropertyInfo PropertyInfo { get; }

        /// <summary>
        /// 是否主键
        /// </summary>
        bool IsPrimaryKey { get; }

        /// <summary>
        /// 长度(为0表示使用最大长度)
        /// </summary>
        int Length { get; }

        /// <summary>
        /// 可空
        /// </summary>
        bool Nullable { get; }

        /// <summary>
        /// 精度位数
        /// </summary>
        public int PrecisionM { get; }
        /// <summary>
        /// 精度小数
        /// </summary>
        int PrecisionD { get; }
    }
}
