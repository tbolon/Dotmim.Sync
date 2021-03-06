﻿using Dotmim.Sync.Builders;
using Dotmim.Sync.Cache;
using Dotmim.Sync.Data;
using Dotmim.Sync.Manager;
using System.Data.Common;
using MySql.Data.MySqlClient;
using Dotmim.Sync.MySql.Builders;
using System;

namespace Dotmim.Sync.MySql
{

    public class MySqlSyncProvider : CoreProvider
    {
        ICache cacheManager;
        DbMetadata dbMetadata;
        static string providerType;

        public override string ProviderTypeName
        {
            get
            {
                return ProviderType;
            }
        }

        public static string ProviderType
        {
            get
            {
                if (!string.IsNullOrEmpty(providerType))
                    return providerType;

                Type type = typeof(MySqlSyncProvider);
                providerType = $"{type.Name}, {type.ToString()}";

                return providerType;
            }

        }

        public override ICache CacheManager
        {
            get
            {
                if (cacheManager == null)
                    cacheManager = new InMemoryCache();

                return cacheManager;
            }
            set
            {
                cacheManager = value;

            }
        }

        /// <summary>
        /// MySql does not support Bulk operations
        /// </summary>
        public override bool SupportBulkOperations => false;

        /// <summary>
        /// MySql can be a server side provider
        /// </summary>
        public override bool CanBeServerProvider => true;


        /// <summary>
        /// Gets or Sets the MySql Metadata object, provided to validate the MySql Columns issued from MySql
        /// </summary>
        /// <summary>
        /// Gets or sets the Metadata object which parse Sql server types
        /// </summary>
        public override DbMetadata Metadata
        {
            get
            {
                if (dbMetadata == null)
                    dbMetadata = new MySqlDbMetadata();

                return dbMetadata;
            }
            set
            {
                dbMetadata = value;

            }
        }

        public MySqlSyncProvider() : base()
        {
        }
        public MySqlSyncProvider(string connectionString) : base()
        {
            this.ConnectionString = connectionString;
        }

        public override DbConnection CreateConnection() => new MySqlConnection(this.ConnectionString);

        public override DbBuilder GetDatabaseBuilder(DmTable tableDescription, DbBuilderOption options = DbBuilderOption.UseExistingSchema) => new MySqlBuilder(tableDescription, options);

        public override DbManager GetDbManager(string tableName) => new MySqlManager(tableName);

        public override DbScopeBuilder GetScopeBuilder() => new MySqlScopeBuilder();
    }
}
