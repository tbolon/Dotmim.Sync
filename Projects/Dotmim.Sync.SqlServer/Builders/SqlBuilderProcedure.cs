﻿using Dotmim.Sync.Core.Builders;
using System;
using System.Collections.Generic;
using System.Text;
using Dotmim.Sync.Data;
using System.Data.Common;
using Dotmim.Sync.Core.Common;
using System.Data.SqlClient;
using System.Data;
using System.Globalization;
using Dotmim.Sync.Core.Log;
using System.Linq;

namespace Dotmim.Sync.SqlServer.Builders
{
    public class SqlBuilderProcedure : IDbBuilderProcedureHelper
    {
        private ObjectNameParser tableName;
        private ObjectNameParser trackingName;
        private SqlConnection connection;
        private SqlTransaction transaction;
        private DmTable tableDescription;
        private SqlObjectNames sqlObjectNames;

        public List<DmColumn> FilterColumns { get; set; }
        public List<DmColumn> FilterParameters { get; set; }

        public SqlBuilderProcedure(DmTable tableDescription, DbConnection connection, DbTransaction transaction = null)
        {
            this.connection = connection as SqlConnection;
            this.transaction = transaction as SqlTransaction;

            this.tableDescription = tableDescription;
            (this.tableName, this.trackingName) = SqlBuilder.GetParsers(tableDescription);
            this.sqlObjectNames = new SqlObjectNames(this.tableDescription);
        }

        private void AddPkColumnParametersToCommand(SqlCommand sqlCommand)
        {
            foreach (DmColumn pkColumn in this.tableDescription.PrimaryKey.Columns)
                sqlCommand.Parameters.Add(pkColumn.GetSqlParameter());
        }
        private void AddColumnParametersToCommand(SqlCommand sqlCommand)
        {
            foreach (DmColumn column in this.tableDescription.Columns.Where(c => !c.ReadOnly))
                sqlCommand.Parameters.Add(column.GetSqlParameter());
        }

        /// <summary>
        /// From a SqlParameter, create the declaration
        /// </summary>
        internal static string CreateParameterDeclaration(SqlParameter param)
        {
            StringBuilder stringBuilder3 = new StringBuilder();
            SqlDbType sqlDbType = param.SqlDbType;

            string empty = string.Empty;
            switch (sqlDbType)
            {
                case SqlDbType.Binary:
                case SqlDbType.Char:
                case SqlDbType.NChar:
                case SqlDbType.NVarChar:
                case SqlDbType.VarBinary:
                case SqlDbType.VarChar:
                    {
                        if (param.Size != -1)
                            empty = string.Concat("(", param.Size, ")");
                        else
                            empty = "(max)";
                        break;
                    }
                case SqlDbType.Decimal:
                    {
                        empty = string.Concat("(", param.Precision, ",", param.Scale, ")");
                        break;
                    }

            }

            var sqlDbTypeString = sqlDbType == SqlDbType.Variant ? "sql_variant" : sqlDbType.ToString();

            if (sqlDbType != SqlDbType.Structured)
            {
                stringBuilder3.Append(string.Concat(param.ParameterName, " ", sqlDbTypeString, empty));

                if (param.Direction == ParameterDirection.Output || param.Direction == ParameterDirection.InputOutput)
                    stringBuilder3.Append(" OUTPUT");
            }
            else
            {
                stringBuilder3.Append(string.Concat(param.ParameterName, " ", param.TypeName, " READONLY"));
            }

            return stringBuilder3.ToString();

        }

        /// <summary>
        /// From a SqlCommand, create a stored procedure string
        /// </summary>
        private static string CreateProcedureCommandText(SqlCommand cmd, string procName)
        {
            StringBuilder stringBuilder = new StringBuilder(string.Concat("CREATE PROCEDURE ", procName));
            string str = "\n\t";
            foreach (SqlParameter parameter in cmd.Parameters)
            {
                stringBuilder.Append(string.Concat(str, CreateParameterDeclaration(parameter)));
                str = ",\n\t";
            }
            stringBuilder.Append("\nAS\nBEGIN\n");
            stringBuilder.Append(cmd.CommandText);
            stringBuilder.Append("\nEND");
            return stringBuilder.ToString();
        }

        /// <summary>
        /// Create a stored procedure
        /// </summary>
        private void CreateProcedureCommand(Func<SqlCommand> BuildCommand, string procName)
        {

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    connection.Open();

                var str = CreateProcedureCommandText(BuildCommand(), procName);
                using (var command = new SqlCommand(str, connection))
                {
                    if (transaction != null)
                        command.Transaction = transaction;

                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Logger.Current.Error($"Error during CreateProcedureCommand : {ex}");
                throw;
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }

        }

        private string CreateProcedureCommandScriptText(Func<SqlCommand> BuildCommand, string procName)
        {

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    connection.Open();

                var str1 = $"Command {procName} for table {tableName.QuotedString}";
                var str = CreateProcedureCommandText(BuildCommand(), procName);
                return SqlBuilder.WrapScriptTextWithComments(str, str1);


            }
            catch (Exception ex)
            {
                Logger.Current.Error($"Error during CreateProcedureCommand : {ex}");
                throw;
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }
        }

        /// <summary>
        /// Check if we need to create the stored procedure
        /// </summary>
        public bool NeedToCreateProcedure(DbCommandType commandType, DbBuilderOption option)
        {
            if (connection.State != ConnectionState.Open)
                throw new ArgumentException("Here, we need an opened connection please");

            var commandName = this.sqlObjectNames.GetCommandName(commandType);

            if (option.HasFlag(DbBuilderOption.CreateOrUseExistingSchema))
                return !SqlManagementUtils.ProcedureExists(connection, transaction, commandName);

            return false;
        }

        /// <summary>
        /// Check if we need to create the TVP Type
        /// </summary>
        public bool NeedToCreateType(DbCommandType commandType, DbBuilderOption option)
        {
            if (connection.State != ConnectionState.Open)
                throw new ArgumentException("Here, we need an opened connection please");

            var commandName = this.sqlObjectNames.GetCommandName(commandType);

            if (option.HasFlag(DbBuilderOption.CreateOrUseExistingSchema))
                return !SqlManagementUtils.TypeExists(connection, transaction, commandName);

            return false;
        }

        private string BulkSelectUnsuccessfulRows()
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("--Select all ids not deleted for conflict");
            stringBuilder.Append("SELECT ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append(", ");
                else
                    stringBuilder.AppendLine();
            }

            stringBuilder.AppendLine($"FROM @changeTable t");
            stringBuilder.AppendLine("WHERE NOT EXISTS (");
            stringBuilder.Append("\t SELECT ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append(", ");
                else
                    stringBuilder.AppendLine();
            }
            stringBuilder.AppendLine("\t FROM @changed i");
            stringBuilder.Append("\t WHERE ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"t.{cc.QuotedString} = i.{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append("AND ");
                else
                    stringBuilder.AppendLine();
            }
            stringBuilder.AppendLine("\t)");
            return stringBuilder.ToString();
        }

        //------------------------------------------------------------------
        // Bulk Delete command
        //------------------------------------------------------------------
        private SqlCommand BuildBulkDeleteCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();

            SqlParameter sqlParameter = new SqlParameter("@sync_min_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter);
            SqlParameter sqlParameter1 = new SqlParameter("@sync_scope_id", SqlDbType.UniqueIdentifier);
            sqlCommand.Parameters.Add(sqlParameter1);
            SqlParameter sqlParameter2 = new SqlParameter("@changeTable", SqlDbType.Structured);
            sqlParameter2.TypeName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkTableType);
            sqlCommand.Parameters.Add(sqlParameter2);


            string str4 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "p", "t");
            string str5 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "changes", "base");
            string str6 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "changes", "side");
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("-- use a temp table to store the list of PKs that successfully got updated/inserted");
            stringBuilder.Append("declare @changed TABLE (");
            foreach (var c in this.tableDescription.PrimaryKey.Columns)
            {
                var cc = new ObjectNameParser(c.ColumnName);
                var quotedColumnType = new ObjectNameParser(c.GetSqlDbTypeString(), "[", "]").QuotedString;
                quotedColumnType += c.GetSqlTypePrecisionString();

                stringBuilder.Append($"{cc.QuotedString} {quotedColumnType}, ");
            }
            stringBuilder.Append(" PRIMARY KEY (");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append(", ");
            }
            stringBuilder.AppendLine("));");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"-- delete all items where timestamp <= @sync_min_timestamp or update_scope_id = @sync_scope_id");

            stringBuilder.AppendLine($"DELETE {tableName.QuotedString}");
            stringBuilder.Append($"OUTPUT ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"DELETED.{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append(", ");
                else
                    stringBuilder.AppendLine();
            }
            stringBuilder.AppendLine($"INTO @changed ");
            stringBuilder.AppendLine($"FROM {tableName.QuotedString} [base]");
            stringBuilder.AppendLine("JOIN (");


            stringBuilder.AppendLine($"\tSELECT ");
            string str = "";
            stringBuilder.Append("\t");
            foreach (var c in this.tableDescription.Columns.Where(col => !col.ReadOnly))
            {
                var columnName = new ObjectNameParser(c.ColumnName);
                stringBuilder.Append($"{str}[p].{columnName.QuotedString}");
                str = ", ";
            }
            stringBuilder.AppendLine("\t, [p].[create_timestamp], [p].[update_timestamp]");
            stringBuilder.AppendLine("\t, [t].[update_scope_id], [t].[timestamp]");

            stringBuilder.AppendLine($"\tFROM @changeTable p ");


            //stringBuilder.AppendLine($"\tSELECT p.*, t.update_scope_id, t.timestamp");
            //stringBuilder.AppendLine($"\tFROM @changeTable p ");
            stringBuilder.AppendLine($"\tJOIN {trackingName.QuotedString} t ON ");
            stringBuilder.AppendLine($"\t{str4}");
            stringBuilder.AppendLine("\t)");
            stringBuilder.AppendLine("\tAS changes ON ");
            stringBuilder.AppendLine(str5);
            stringBuilder.AppendLine("WHERE");
            stringBuilder.AppendLine("-- Last changes was from the current scope, so we can delete it since we are sure no one else edit it ");
            stringBuilder.AppendLine("changes.update_scope_id = @sync_scope_id");
            stringBuilder.AppendLine("-- no change since the last time the current scope has sync (so no one has update the row)");
            stringBuilder.AppendLine("OR [changes].[timestamp] <= @sync_min_timestamp;");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("-- Since the delete trigger is passed, we update the tracking table to reflect the real scope deleter");
            stringBuilder.AppendLine("UPDATE [side] SET");
            stringBuilder.AppendLine("\tsync_row_is_tombstone = 1, ");
            stringBuilder.AppendLine("\tupdate_scope_id = @sync_scope_id,");
            stringBuilder.AppendLine("\tupdate_timestamp = [changes].update_timestamp");
            stringBuilder.AppendLine($"FROM {trackingName.QuotedString} side");
            stringBuilder.AppendLine("JOIN (");
            stringBuilder.Append("\tSELECT ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"p.{cc.QuotedString}, ");
            }
            stringBuilder.AppendLine(" p.update_timestamp, p.create_timestamp ");
            stringBuilder.AppendLine("\tFROM @changed t JOIN @changeTable p ON ");
            stringBuilder.AppendLine($"\t{str4}");
            stringBuilder.Append("\t) AS changes ON ");
            stringBuilder.Append(str6);
            stringBuilder.AppendLine();
            stringBuilder.AppendLine();
            stringBuilder.Append(BulkSelectUnsuccessfulRows());
            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateBulkDelete()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkDeleteRows);
            CreateProcedureCommand(this.BuildBulkDeleteCommand, commandName);
        }
        public string CreateBulkDeleteScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkDeleteRows);
            return CreateProcedureCommandScriptText(this.BuildBulkDeleteCommand, commandName);
        }

        //------------------------------------------------------------------
        // Bulk Insert command
        //------------------------------------------------------------------
        private SqlCommand BuildBulkInsertCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();

            SqlParameter sqlParameter = new SqlParameter("@sync_min_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter);
            SqlParameter sqlParameter1 = new SqlParameter("@sync_scope_id", SqlDbType.UniqueIdentifier);
            sqlCommand.Parameters.Add(sqlParameter1);
            SqlParameter sqlParameter2 = new SqlParameter("@changeTable", SqlDbType.Structured);
            sqlParameter2.TypeName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkTableType);
            sqlCommand.Parameters.Add(sqlParameter2);


            string str4 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "[p]", "[t]");
            string str5 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "[changes]", "[base]");
            string str6 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "[changes]", "[side]");

            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("-- use a temp table to store the list of PKs that successfully got updated/inserted");
            stringBuilder.Append("declare @changed TABLE (");
            foreach (var c in this.tableDescription.PrimaryKey.Columns)
            {
                var cc = new ObjectNameParser(c.ColumnName);
                var quotedColumnType = new ObjectNameParser(c.GetSqlDbTypeString(), "[", "]").QuotedString;
                quotedColumnType += c.GetSqlTypePrecisionString();

                stringBuilder.Append($"{cc.QuotedString} {quotedColumnType}, ");
            }
            stringBuilder.Append(" PRIMARY KEY (");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append(", ");
            }
            stringBuilder.AppendLine("));");
            stringBuilder.AppendLine();

            // Check if we have auto inc column
            bool flag = false;
            foreach (var mutableColumn in this.tableDescription.Columns.Where(c => !c.ReadOnly))
            {
                if (!mutableColumn.AutoIncrement)
                    continue;
                flag = true;
            }

            if (flag)
            {
                stringBuilder.AppendLine();
                stringBuilder.AppendLine($"SET IDENTITY_INSERT {tableName.QuotedString} ON;");
                stringBuilder.AppendLine();
            }

            stringBuilder.AppendLine("-- update/insert into the base table");
            stringBuilder.AppendLine($"MERGE {tableName.QuotedString} AS base USING");
            stringBuilder.AppendLine("\t-- join done here against the side table to get the local timestamp for concurrency check\n");


            //   stringBuilder.AppendLine("\t(SELECT p.*, t.timestamp FROM @changeTable p ");

            stringBuilder.AppendLine($"\t(SELECT ");
            string str = "";
            stringBuilder.Append("\t");
            foreach (var c in this.tableDescription.Columns.Where(col => !col.ReadOnly))
            {
                var columnName = new ObjectNameParser(c.ColumnName);
                stringBuilder.Append($"{str}[p].{columnName.QuotedString}");
                str = ", ";
            }
            stringBuilder.AppendLine("\t, [p].[create_timestamp], [p].[update_timestamp]");
            stringBuilder.AppendLine("\t, [t].[update_scope_id], [t].[timestamp]");

            stringBuilder.AppendLine($"\tFROM @changeTable p ");


            stringBuilder.Append($"\tLEFT JOIN {trackingName.QuotedString} t ON ");
            stringBuilder.AppendLine($"\t{str4}");
            stringBuilder.AppendLine($"\t) AS changes ON {str5}");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("-- Si la ligne n'existe pas en local et qu'elle a été créé avant le timestamp de référence");
            stringBuilder.Append("WHEN NOT MATCHED BY TARGET AND (changes.[timestamp] <= @sync_min_timestamp OR changes.[timestamp] IS NULL) THEN");

            StringBuilder stringBuilderArguments = new StringBuilder();
            StringBuilder stringBuilderParameters = new StringBuilder();

            string empty = string.Empty;
            foreach (var mutableColumn in this.tableDescription.Columns.Where(c => !c.ReadOnly))
            {
                ObjectNameParser columnName = new ObjectNameParser(mutableColumn.ColumnName);
                stringBuilderArguments.Append(string.Concat(empty, columnName.QuotedString));
                stringBuilderParameters.Append(string.Concat(empty, $"changes.{columnName.UnquotedString}"));
                empty = ", ";
            }
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"\tINSERT");
            stringBuilder.AppendLine($"\t({stringBuilderArguments.ToString()})");
            stringBuilder.AppendLine($"\tVALUES ({stringBuilderParameters.ToString()})");
            stringBuilder.Append($"\tOUTPUT ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"INSERTED.{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append(", ");
                else
                    stringBuilder.AppendLine();
            }
            stringBuilder.AppendLine($"\tINTO @changed; -- populates the temp table with successful PKs");
            stringBuilder.AppendLine();

            if (flag)
            {
                stringBuilder.AppendLine();
                stringBuilder.AppendLine($"SET IDENTITY_INSERT {tableName.QuotedString} OFF;");
                stringBuilder.AppendLine();
            }

            stringBuilder.AppendLine("-- Since the insert trigger is passed, we update the tracking table to reflect the real scope inserter");
            stringBuilder.AppendLine("UPDATE side SET");
            stringBuilder.AppendLine("\tupdate_scope_id = @sync_scope_id,");
            stringBuilder.AppendLine("\tcreate_scope_id = @sync_scope_id,");
            stringBuilder.AppendLine("\tupdate_timestamp = changes.update_timestamp,");
            stringBuilder.AppendLine("\tcreate_timestamp = changes.create_timestamp");
            stringBuilder.AppendLine($"FROM {trackingName.QuotedString} side");
            stringBuilder.AppendLine("JOIN (");
            stringBuilder.Append("\tSELECT ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"p.{cc.QuotedString}, ");
            }

            stringBuilder.AppendLine(" p.update_timestamp, p.create_timestamp ");
            stringBuilder.AppendLine("\tFROM @changed t");
            stringBuilder.AppendLine("\tJOIN @changeTable p ON ");
            stringBuilder.Append(str4);
            stringBuilder.AppendLine(") AS [changes] ON ");
            stringBuilder.AppendLine(str6);
            stringBuilder.Append(BulkSelectUnsuccessfulRows());


            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateBulkInsert()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkInsertRows);
            CreateProcedureCommand(BuildBulkInsertCommand, commandName);
        }
        public string CreateBulkInsertScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkInsertRows);
            return CreateProcedureCommandScriptText(BuildBulkInsertCommand, commandName);
        }

        //------------------------------------------------------------------
        // Bulk Update command
        //------------------------------------------------------------------
        private SqlCommand BuildBulkUpdateCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();

            SqlParameter sqlParameter = new SqlParameter("@sync_min_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter);
            SqlParameter sqlParameter1 = new SqlParameter("@sync_scope_id", SqlDbType.UniqueIdentifier);
            sqlCommand.Parameters.Add(sqlParameter1);
            SqlParameter sqlParameter2 = new SqlParameter("@changeTable", SqlDbType.Structured);
            sqlParameter2.TypeName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkTableType);
            sqlCommand.Parameters.Add(sqlParameter2);


            string str4 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "[p]", "[t]");
            string str5 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "[changes]", "[base]");
            string str6 = SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "[changes]", "[side]");

            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("-- use a temp table to store the list of PKs that successfully got updated/inserted");
            stringBuilder.Append("declare @changed TABLE (");
            foreach (var c in this.tableDescription.PrimaryKey.Columns)
            {
                var cc = new ObjectNameParser(c.ColumnName);
                var quotedColumnType = new ObjectNameParser(c.GetSqlDbTypeString(), "[", "]").QuotedString;
                quotedColumnType += c.GetSqlTypePrecisionString();

                stringBuilder.Append($"{cc.QuotedString} {quotedColumnType}, ");
            }
            stringBuilder.Append(" PRIMARY KEY (");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append(", ");
            }
            stringBuilder.AppendLine("));");
            stringBuilder.AppendLine();

            // Check if we have auto inc column
            bool flag = false;
            foreach (var mutableColumn in this.tableDescription.Columns.Where(c => !c.ReadOnly))
            {
                if (!mutableColumn.AutoIncrement)
                    continue;
                flag = true;
            }

            if (flag)
            {
                stringBuilder.AppendLine();
                stringBuilder.AppendLine($"SET IDENTITY_INSERT {tableName.QuotedString} ON;");
                stringBuilder.AppendLine();
            }

            stringBuilder.AppendLine("-- update the base table");
            stringBuilder.AppendLine($"MERGE {tableName.QuotedString} AS base USING");
            stringBuilder.AppendLine("\t-- join done here against the side table to get the local timestamp for concurrency check\n");

            //stringBuilder.AppendLine("\t(SELECT p.*, t.update_scope_id, t.timestamp FROM @changeTable p ");

            stringBuilder.AppendLine($"\t(SELECT ");
            string str = "";
            stringBuilder.Append("\t");
            foreach (var c in this.tableDescription.Columns.Where(col => !col.ReadOnly))
            {
                var columnName = new ObjectNameParser(c.ColumnName);
                stringBuilder.Append($"{str}[p].{columnName.QuotedString}");
                str = ", ";
            }
            stringBuilder.AppendLine("\t, [p].[create_timestamp], [p].[update_timestamp]");
            stringBuilder.AppendLine("\t, [t].[update_scope_id], [t].[timestamp]");

            stringBuilder.AppendLine($"\tFROM @changeTable p ");
            stringBuilder.Append($"\tLEFT JOIN {trackingName.QuotedString} t ON ");
            stringBuilder.AppendLine($" {str4}");
            stringBuilder.AppendLine($"\t) AS changes ON {str5}");
            stringBuilder.AppendLine();

            stringBuilder.AppendLine("WHEN MATCHED AND ([changes].[update_scope_id] = @sync_scope_id OR [changes].[timestamp] <= @sync_min_timestamp) THEN");

            StringBuilder stringBuilderArguments = new StringBuilder();
            StringBuilder stringBuilderParameters = new StringBuilder();

            string empty = string.Empty;
            foreach (var mutableColumn in this.tableDescription.Columns.Where(c => !c.ReadOnly))
            {
                ObjectNameParser columnName = new ObjectNameParser(mutableColumn.ColumnName);
                stringBuilderArguments.Append(string.Concat(empty, columnName.QuotedString));
                stringBuilderParameters.Append(string.Concat(empty, $"changes.{columnName.UnquotedString}"));
                empty = ", ";
            }
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"\tUPDATE SET");

            string strSeparator = "";
            foreach (DmColumn column in this.tableDescription.NonPkColumns.Where(col => !col.ReadOnly))
            {
                ObjectNameParser quotedColumn = new ObjectNameParser(column.ColumnName);
                stringBuilder.AppendLine($"\t{strSeparator}{quotedColumn.QuotedString} = [changes].{quotedColumn.UnquotedString}");
                strSeparator = ", ";
            }
            stringBuilder.Append($"\tOUTPUT ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"INSERTED.{cc.QuotedString}");
                if (i < this.tableDescription.PrimaryKey.Columns.Length - 1)
                    stringBuilder.Append(", ");
                else
                    stringBuilder.AppendLine();
            }
            stringBuilder.AppendLine($"\tINTO @changed; -- populates the temp table with successful PKs");
            stringBuilder.AppendLine();

            if (flag)
            {
                stringBuilder.AppendLine();
                stringBuilder.AppendLine($"SET IDENTITY_INSERT {tableName.QuotedString} OFF;");
                stringBuilder.AppendLine();
            }

            stringBuilder.AppendLine("-- Since the update trigger is passed, we update the tracking table to reflect the real scope updater");
            stringBuilder.AppendLine("UPDATE side SET");
            stringBuilder.AppendLine("\tupdate_scope_id = @sync_scope_id,");
            stringBuilder.AppendLine("\tupdate_timestamp = changes.update_timestamp");
            stringBuilder.AppendLine($"FROM {trackingName.QuotedString} side");
            stringBuilder.AppendLine("JOIN (");
            stringBuilder.Append("\tSELECT ");
            for (int i = 0; i < this.tableDescription.PrimaryKey.Columns.Length; i++)
            {
                var cc = new ObjectNameParser(this.tableDescription.PrimaryKey.Columns[i].ColumnName);
                stringBuilder.Append($"p.{cc.QuotedString}, ");
            }

            stringBuilder.AppendLine(" p.update_timestamp, p.create_timestamp ");
            stringBuilder.AppendLine("\tFROM @changed t");
            stringBuilder.AppendLine("\tJOIN @changeTable p ON ");
            stringBuilder.AppendLine($"\t{str4}");
            stringBuilder.AppendLine(") AS [changes] ON ");
            stringBuilder.AppendLine($"\t{str6}");
            stringBuilder.AppendLine();
            stringBuilder.Append(BulkSelectUnsuccessfulRows());

            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateBulkUpdate()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkUpdateRows);
            CreateProcedureCommand(BuildBulkUpdateCommand, commandName);
        }
        public string CreateBulkUpdateScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkUpdateRows);
            return CreateProcedureCommandScriptText(BuildBulkUpdateCommand, commandName);
        }


        //------------------------------------------------------------------
        // Delete command
        //------------------------------------------------------------------
        private SqlCommand BuildDeleteCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();
            this.AddPkColumnParametersToCommand(sqlCommand);
            SqlParameter sqlParameter = new SqlParameter("@sync_force_write", SqlDbType.Int);
            sqlCommand.Parameters.Add(sqlParameter);
            SqlParameter sqlParameter1 = new SqlParameter("@sync_min_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter1);
            SqlParameter sqlParameter2 = new SqlParameter("@sync_row_count", SqlDbType.Int);
            sqlParameter2.Direction = ParameterDirection.Output;
            sqlCommand.Parameters.Add(sqlParameter2);

            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine($"SET {sqlParameter2.ParameterName} = 0;");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"DELETE {tableName.QuotedString}");
            stringBuilder.AppendLine($"FROM {tableName.QuotedString} [base]");
            stringBuilder.AppendLine($"JOIN {trackingName.QuotedString} [side] ON ");

            stringBuilder.AppendLine(SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "[base]", "[side]"));

            stringBuilder.AppendLine("WHERE ([side].[timestamp] <= @sync_min_timestamp  OR @sync_force_write = 1)");
            stringBuilder.Append("AND ");
            stringBuilder.AppendLine(string.Concat("(", SqlManagementUtils.WhereColumnAndParameters(this.tableDescription.PrimaryKey.Columns, "[base]"), ");"));
            stringBuilder.AppendLine();
            stringBuilder.AppendLine(string.Concat("SET ", sqlParameter2.ParameterName, " = @@ROWCOUNT;"));
            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateDelete()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.DeleteRow);
            CreateProcedureCommand(BuildDeleteCommand, commandName);
        }
        public string CreateDeleteScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.DeleteRow);
            return CreateProcedureCommandScriptText(BuildDeleteCommand, commandName);
        }


        //------------------------------------------------------------------
        // Delete Metadata command
        //------------------------------------------------------------------
        private SqlCommand BuildDeleteMetadataCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();
            this.AddPkColumnParametersToCommand(sqlCommand);
            SqlParameter sqlParameter = new SqlParameter("@sync_check_concurrency", SqlDbType.Int);
            sqlCommand.Parameters.Add(sqlParameter);
            SqlParameter sqlParameter1 = new SqlParameter("@sync_row_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter1);
            SqlParameter sqlParameter2 = new SqlParameter("@sync_row_count", SqlDbType.Int);
            sqlParameter2.Direction = ParameterDirection.Output;
            sqlCommand.Parameters.Add(sqlParameter2);
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine($"SET {sqlParameter2.ParameterName} = 0;");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"DELETE [side] FROM {trackingName.QuotedString} [side]");
            stringBuilder.Append($"WHERE ");
            stringBuilder.AppendLine(SqlManagementUtils.WhereColumnAndParameters(this.tableDescription.PrimaryKey.Columns, ""));
            stringBuilder.AppendLine();
            stringBuilder.AppendLine(string.Concat("SET ", sqlParameter2.ParameterName, " = @@ROWCOUNT;"));
            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateDeleteMetadata()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.DeleteMetadata);
            CreateProcedureCommand(BuildDeleteMetadataCommand, commandName);
        }
        public string CreateDeleteMetadataScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.DeleteMetadata);
            return CreateProcedureCommandScriptText(BuildDeleteMetadataCommand, commandName);
        }


        //------------------------------------------------------------------
        // Insert command
        //------------------------------------------------------------------
        private SqlCommand BuildInsertCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();
            StringBuilder stringBuilder = new StringBuilder();
            StringBuilder stringBuilderArguments = new StringBuilder();
            StringBuilder stringBuilderParameters = new StringBuilder();

            this.AddColumnParametersToCommand(sqlCommand);
            SqlParameter sqlParameter = new SqlParameter("@sync_row_count", SqlDbType.Int);
            sqlParameter.Direction = ParameterDirection.Output;
            sqlCommand.Parameters.Add(sqlParameter);

            //Check auto increment column
            bool hasAutoIncColumn = false;
            foreach (var column in this.tableDescription.Columns)
            {
                if (!column.AutoIncrement)
                    continue;
                hasAutoIncColumn = true;
                break;
            }

            stringBuilder.AppendLine($"SET {sqlParameter.ParameterName} = 0;");

            stringBuilder.Append(string.Concat("IF NOT EXISTS (SELECT * FROM ", trackingName.QuotedString, " WHERE "));
            stringBuilder.Append(SqlManagementUtils.WhereColumnAndParameters(this.tableDescription.PrimaryKey.Columns, string.Empty));
            stringBuilder.AppendLine(") ");
            stringBuilder.AppendLine("BEGIN ");
            if (hasAutoIncColumn)
            {
                stringBuilder.AppendLine($"\tSET IDENTITY_INSERT {tableName.QuotedString} ON;");
                stringBuilder.AppendLine();
            }

            string empty = string.Empty;
            foreach (var mutableColumn in this.tableDescription.Columns.Where(c => !c.ReadOnly))
            {
                ObjectNameParser columnName = new ObjectNameParser(mutableColumn.ColumnName);
                stringBuilderArguments.Append(string.Concat(empty, columnName.QuotedString));
                stringBuilderParameters.Append(string.Concat(empty, $"@{columnName.UnquotedString}"));
                empty = ", ";
            }
            stringBuilder.AppendLine($"\tINSERT INTO {tableName.QuotedString}");
            stringBuilder.AppendLine($"\t({stringBuilderArguments.ToString()})");
            stringBuilder.AppendLine($"\tVALUES ({stringBuilderParameters.ToString()});");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine(string.Concat("\tSET ", sqlParameter.ParameterName, " = @@rowcount; "));

            if (hasAutoIncColumn)
            {
                stringBuilder.AppendLine();
                stringBuilder.AppendLine($"\tSET IDENTITY_INSERT {tableName.QuotedString} OFF;");
            }

            stringBuilder.AppendLine("END ");
            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateInsert()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.InsertRow);
            CreateProcedureCommand(BuildInsertCommand, commandName);
        }
        public string CreateInsertScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.InsertRow);
            return CreateProcedureCommandScriptText(BuildInsertCommand, commandName);
        }

        //------------------------------------------------------------------
        // Insert Metadata command
        //------------------------------------------------------------------
        private SqlCommand BuildInsertMetadataCommand()
        {
            StringBuilder stringBuilderArguments = new StringBuilder();
            StringBuilder stringBuilderParameters = new StringBuilder();
            SqlCommand sqlCommand = new SqlCommand();

            StringBuilder stringBuilder = new StringBuilder();
            this.AddPkColumnParametersToCommand(sqlCommand);
            SqlParameter sqlParameter = new SqlParameter("@sync_scope_id", SqlDbType.UniqueIdentifier);
            sqlCommand.Parameters.Add(sqlParameter);
            SqlParameter sqlParameter1 = new SqlParameter("@sync_row_is_tombstone", SqlDbType.Int);
            sqlCommand.Parameters.Add(sqlParameter1);
            SqlParameter sqlParameter3 = new SqlParameter("@create_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter3);
            SqlParameter sqlParameter4 = new SqlParameter("@update_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter4);
            SqlParameter sqlParameter8 = new SqlParameter("@sync_row_count", SqlDbType.Int);
            sqlParameter8.Direction = ParameterDirection.Output;
            sqlCommand.Parameters.Add(sqlParameter8);

            stringBuilder.AppendLine($"SET {sqlParameter8.ParameterName} = 0;");
            stringBuilder.AppendLine();

            stringBuilder.AppendLine($"UPDATE {trackingName.QuotedString} SET ");
            stringBuilder.AppendLine("\t[create_scope_id] = @sync_scope_id, ");
            stringBuilder.AppendLine("\t[create_timestamp] = @create_timestamp, ");
            stringBuilder.AppendLine("\t[update_scope_id] = @sync_scope_id, ");
            stringBuilder.AppendLine("\t[update_timestamp] = @update_timestamp, ");
            stringBuilder.AppendLine("\t[sync_row_is_tombstone] = @sync_row_is_tombstone ");
            stringBuilder.AppendLine($"WHERE ({SqlManagementUtils.WhereColumnAndParameters(this.tableDescription.PrimaryKey.Columns, "")})");
            stringBuilder.AppendLine();

            stringBuilder.AppendLine($"SET {sqlParameter8.ParameterName} = @@rowcount; ");
            stringBuilder.AppendLine();

            stringBuilder.AppendLine($"IF ({sqlParameter8.ParameterName} = 0)");
            stringBuilder.AppendLine("BEGIN ");
            stringBuilder.AppendLine($"\tINSERT INTO {trackingName.QuotedString}");

            string empty = string.Empty;
            foreach (var pkColumn in this.tableDescription.PrimaryKey.Columns)
            {
                ObjectNameParser columnName = new ObjectNameParser(pkColumn.ColumnName);
                stringBuilderArguments.Append(string.Concat(empty, columnName.QuotedString));
                stringBuilderParameters.Append(string.Concat(empty, $"@{columnName.UnquotedString}"));
                empty = ", ";
            }
            stringBuilder.AppendLine($"\t({stringBuilderArguments.ToString()}, ");
            stringBuilder.AppendLine($"\t[create_scope_id], [create_timestamp], [update_scope_id], [update_timestamp],");
            stringBuilder.AppendLine($"\t[sync_row_is_tombstone], [last_change_datetime])");
            stringBuilder.AppendLine($"\tVALUES ({stringBuilderParameters.ToString()}, ");
            stringBuilder.AppendLine($"\t@sync_scope_id, @create_timestamp, @sync_scope_id, @update_timestamp, ");
            stringBuilder.AppendLine($"\t@sync_row_is_tombstone, GetDate());");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"\tSET {sqlParameter8.ParameterName} = @@rowcount; ");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("END");

            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateInsertMetadata()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.InsertMetadata);
            CreateProcedureCommand(BuildInsertMetadataCommand, commandName);
        }
        public string CreateInsertMetadataScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.InsertMetadata);
            return CreateProcedureCommandScriptText(BuildInsertMetadataCommand, commandName);
        }


        //------------------------------------------------------------------
        // Select Row command
        //------------------------------------------------------------------
        private SqlCommand BuildSelectRowCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();
            this.AddPkColumnParametersToCommand(sqlCommand);
            SqlParameter sqlParameter = new SqlParameter("@sync_scope_id", SqlDbType.UniqueIdentifier);
            sqlCommand.Parameters.Add(sqlParameter);

            StringBuilder stringBuilder = new StringBuilder("SELECT ");
            stringBuilder.AppendLine();
            StringBuilder stringBuilder1 = new StringBuilder();
            string empty = string.Empty;
            foreach (var pkColumn in this.tableDescription.PrimaryKey.Columns)
            {
                ObjectNameParser pkColumnName = new ObjectNameParser(pkColumn.ColumnName);
                stringBuilder.AppendLine($"\t[side].{pkColumnName.QuotedString}, ");
                stringBuilder1.Append($"{empty}[side].{pkColumnName.QuotedString} = @{pkColumnName.UnquotedString}");
                empty = " AND ";
            }
            foreach (DmColumn nonPkMutableColumn in this.tableDescription.NonPkColumns.Where(c => !c.ReadOnly))
            {
                ObjectNameParser nonPkColumnName = new ObjectNameParser(nonPkMutableColumn.ColumnName);
                stringBuilder.AppendLine($"\t[base].{nonPkColumnName.QuotedString}, ");
            }
            stringBuilder.AppendLine("\t[side].[sync_row_is_tombstone],");
            stringBuilder.AppendLine("\t[side].[create_scope_id],");
            stringBuilder.AppendLine("\t[side].[create_timestamp],");
            stringBuilder.AppendLine("\t[side].[update_scope_id],");
            stringBuilder.AppendLine("\t[side].[update_timestamp]");

            stringBuilder.AppendLine($"FROM {tableName.QuotedString} [base]");
            stringBuilder.AppendLine($"RIGHT JOIN {trackingName.QuotedString} [side] ON");

            string str = string.Empty;
            foreach (var pkColumn in this.tableDescription.PrimaryKey.Columns)
            {
                ObjectNameParser pkColumnName = new ObjectNameParser(pkColumn.ColumnName);
                stringBuilder.Append($"{str}[base].{pkColumnName.QuotedString} = [side].{pkColumnName.QuotedString}");
                str = " AND ";
            }
            stringBuilder.AppendLine();
            stringBuilder.Append(string.Concat("WHERE ", stringBuilder1.ToString()));
            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateSelectRow()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.SelectRow);
            CreateProcedureCommand(BuildSelectRowCommand, commandName);
        }
        public string CreateSelectRowScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.SelectRow);
            return CreateProcedureCommandScriptText(BuildSelectRowCommand, commandName);
        }

        //------------------------------------------------------------------
        // Create TVP command
        //------------------------------------------------------------------
        private string CreateTVPTypeCommandText()
        {
            StringBuilder stringBuilder = new StringBuilder();
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.BulkTableType);
            stringBuilder.AppendLine($"CREATE TYPE {commandName} AS TABLE (");
            string str = "";
            foreach (var c in this.tableDescription.Columns.Where(col => !col.ReadOnly))
            {
                var isPrimaryKey = this.tableDescription.PrimaryKey.Columns.Any(cc => this.tableDescription.IsEqual(cc.ColumnName, c.ColumnName));
                var columnName = new ObjectNameParser(c.ColumnName);
                var nullString = isPrimaryKey ? "NOT NULL" : "NULL";
                var quotedColumnType = new ObjectNameParser(c.GetSqlDbTypeString(), "[", "]").QuotedString;
                quotedColumnType += c.GetSqlTypePrecisionString();

                stringBuilder.AppendLine($"{str}{columnName.QuotedString} {quotedColumnType} {nullString}");
                str = ", ";
            }
            stringBuilder.AppendLine(", [create_scope_id] [uniqueidentifier] NULL");
            stringBuilder.AppendLine(", [create_timestamp] [bigint] NULL");
            stringBuilder.AppendLine(", [update_scope_id] [uniqueidentifier] NULL");
            stringBuilder.AppendLine(", [update_timestamp] [bigint] NULL");
            stringBuilder.Append(string.Concat(str, "PRIMARY KEY ("));
            str = "";
            foreach (var c in this.tableDescription.PrimaryKey.Columns)
            {
                var columnName = new ObjectNameParser(c.ColumnName);
                stringBuilder.Append($"{str}{columnName.QuotedString} ASC");
                str = ", ";
            }

            stringBuilder.Append("))");
            return stringBuilder.ToString();
        }
        public void CreateTVPType()
        {
            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    connection.Open();

                using (SqlCommand sqlCommand = new SqlCommand(this.CreateTVPTypeCommandText(),
                    connection))
                {
                    if (transaction != null)
                        sqlCommand.Transaction = transaction;

                    sqlCommand.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Logger.Current.Error($"Error during CreateTVPType : {ex}");
                throw;
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }

        }
        public string CreateTVPTypeScriptText()
        {
            string str = string.Concat("Create TVP Type on table ", tableName.QuotedString);
            return SqlBuilder.WrapScriptTextWithComments(this.CreateTVPTypeCommandText(), str);
        }

        //------------------------------------------------------------------
        // Update command
        //------------------------------------------------------------------
        private SqlCommand BuildUpdateCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();

            StringBuilder stringBuilder = new StringBuilder();
            this.AddColumnParametersToCommand(sqlCommand);

            SqlParameter sqlParameter = new SqlParameter("@sync_force_write", SqlDbType.Int);
            sqlCommand.Parameters.Add(sqlParameter);
            SqlParameter sqlParameter1 = new SqlParameter("@sync_min_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter1);
            SqlParameter sqlParameter2 = new SqlParameter("@sync_row_count", SqlDbType.Int);
            sqlParameter2.Direction = ParameterDirection.Output;
            sqlCommand.Parameters.Add(sqlParameter2);

            stringBuilder.AppendLine($"SET {sqlParameter2.ParameterName} = 0;");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"UPDATE {tableName.QuotedString}");
            stringBuilder.Append($"SET {SqlManagementUtils.CommaSeparatedUpdateFromParameters(this.tableDescription)}");
            stringBuilder.AppendLine($"FROM {tableName.QuotedString} [base]");
            stringBuilder.AppendLine($"JOIN {trackingName.QuotedString} [side]");
            stringBuilder.Append($"ON ");
            stringBuilder.AppendLine(SqlManagementUtils.JoinTwoTablesOnClause(this.tableDescription.PrimaryKey.Columns, "[base]", "[side]"));
            stringBuilder.AppendLine("WHERE ([side].[timestamp] <= @sync_min_timestamp OR @sync_force_write = 1)");
            stringBuilder.Append("AND (");
            stringBuilder.Append(SqlManagementUtils.WhereColumnAndParameters(this.tableDescription.PrimaryKey.Columns, "[base]"));
            stringBuilder.AppendLine(");");
            stringBuilder.AppendLine();
            stringBuilder.Append(string.Concat("SET ", sqlParameter2.ParameterName, " = @@ROWCOUNT;"));
            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateUpdate()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.UpdateRow);
            this.CreateProcedureCommand(BuildUpdateCommand, commandName);
        }
        public string CreateUpdateScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.UpdateRow);
            return CreateProcedureCommandScriptText(BuildUpdateCommand, commandName);
        }

        //------------------------------------------------------------------
        // Update Metadata command
        //------------------------------------------------------------------
        private SqlCommand BuildUpdateMetadataCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();
            StringBuilder stringBuilder = new StringBuilder();
            this.AddPkColumnParametersToCommand(sqlCommand);
            SqlParameter sqlParameter = new SqlParameter("@sync_scope_id", SqlDbType.UniqueIdentifier);
            sqlCommand.Parameters.Add(sqlParameter);
            SqlParameter sqlParameter1 = new SqlParameter("@sync_row_is_tombstone", SqlDbType.Int);
            sqlCommand.Parameters.Add(sqlParameter1);
            SqlParameter sqlParameter3 = new SqlParameter("@create_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter3);
            SqlParameter sqlParameter5 = new SqlParameter("@update_timestamp", SqlDbType.BigInt);
            sqlCommand.Parameters.Add(sqlParameter5);
            SqlParameter sqlParameter8 = new SqlParameter("@sync_row_count", SqlDbType.Int);
            sqlParameter8.Direction = ParameterDirection.Output;
            sqlCommand.Parameters.Add(sqlParameter8);

            string str1 = SqlManagementUtils.WhereColumnAndParameters(this.tableDescription.PrimaryKey.Columns, "");

            stringBuilder.AppendLine($"SET {sqlParameter8.ParameterName} = 0;");
            stringBuilder.AppendLine();

            stringBuilder.AppendLine("DECLARE @was_tombstone int; ");

            stringBuilder.AppendLine($"SELECT @was_tombstone = [sync_row_is_tombstone]");
            stringBuilder.AppendLine($"FROM {trackingName.QuotedString}");
            stringBuilder.AppendLine($"WHERE ({str1})");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("IF (@was_tombstone IS NOT NULL AND @was_tombstone = 1 AND @sync_row_is_tombstone = 0)");
            stringBuilder.AppendLine("BEGIN ");
            stringBuilder.AppendLine($"UPDATE {trackingName.QuotedString} SET ");
            stringBuilder.AppendLine("\t [create_scope_id] = @sync_scope_id, ");
            stringBuilder.AppendLine("\t [update_scope_id] = @sync_scope_id, ");
            stringBuilder.AppendLine("\t [create_timestamp] = @create_timestamp, ");
            stringBuilder.AppendLine("\t [update_timestamp] = @update_timestamp, ");
            stringBuilder.AppendLine("\t [sync_row_is_tombstone] = @sync_row_is_tombstone ");
            stringBuilder.AppendLine($"WHERE ({str1})");
            stringBuilder.AppendLine("END");
            stringBuilder.AppendLine("ELSE");
            stringBuilder.AppendLine("BEGIN ");
            stringBuilder.AppendLine($"UPDATE {trackingName.QuotedString} SET ");
            stringBuilder.AppendLine("\t [update_scope_id] = @sync_scope_id, ");
            stringBuilder.AppendLine("\t [update_timestamp] = @update_timestamp, ");
            stringBuilder.AppendLine("\t [sync_row_is_tombstone] = @sync_row_is_tombstone ");
            stringBuilder.AppendLine($"WHERE ({str1})");
            stringBuilder.AppendLine("END;");
            stringBuilder.AppendLine();
            stringBuilder.Append($"SET {sqlParameter8.ParameterName} = @@ROWCOUNT;");
            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public void CreateUpdateMetadata()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.UpdateMetadata);
            CreateProcedureCommand(BuildUpdateMetadataCommand, commandName);
        }
        public string CreateUpdateMetadataScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.UpdateMetadata);
            return CreateProcedureCommandScriptText(BuildUpdateMetadataCommand, commandName);
        }


        //------------------------------------------------------------------
        // Select changes command
        //------------------------------------------------------------------
        private SqlCommand BuildSelectIncrementalChangesCommand()
        {
            SqlCommand sqlCommand = new SqlCommand();
            SqlParameter sqlParameter1 = new SqlParameter("@sync_min_timestamp", SqlDbType.BigInt);
            SqlParameter sqlParameter3 = new SqlParameter("@sync_scope_id", SqlDbType.UniqueIdentifier);
            SqlParameter sqlParameter4 = new SqlParameter("@sync_scope_is_new", SqlDbType.Bit);
            sqlCommand.Parameters.Add(sqlParameter1);
            sqlCommand.Parameters.Add(sqlParameter3);
            sqlCommand.Parameters.Add(sqlParameter4);

            StringBuilder stringBuilder = new StringBuilder("SELECT ");
            foreach (var pkColumn in this.tableDescription.PrimaryKey.Columns)
            {
                var pkColumnName = new ObjectNameParser(pkColumn.ColumnName);
                stringBuilder.AppendLine($"\t[side].{pkColumnName.QuotedString}, ");
            }
            foreach (var column in this.tableDescription.NonPkColumns.Where(col => !col.ReadOnly))
            {
                var columnName = new ObjectNameParser(column.ColumnName);
                stringBuilder.AppendLine($"\t[base].{columnName.QuotedString}, ");
            }
            stringBuilder.AppendLine($"\t[side].[sync_row_is_tombstone], ");
            stringBuilder.AppendLine($"\t[side].[create_scope_id], ");
            stringBuilder.AppendLine($"\t[side].[create_timestamp], ");
            stringBuilder.AppendLine($"\t[side].[update_scope_id], ");
            stringBuilder.AppendLine($"\t[side].[update_timestamp] ");
            stringBuilder.AppendLine($"FROM {tableName.QuotedString} [base]");
            stringBuilder.AppendLine($"RIGHT JOIN {trackingName.QuotedString} [side]");
            stringBuilder.Append($"ON ");

            string empty = "";
            foreach (var pkColumn in this.tableDescription.PrimaryKey.Columns)
            {
                var pkColumnName = new ObjectNameParser(pkColumn.ColumnName);
                stringBuilder.Append($"{empty}[base].{pkColumnName.QuotedString} = [side].{pkColumnName.QuotedString}");
                empty = " AND ";
            }
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("WHERE (");
            string str = string.Empty;
            //if (!SqlManagementUtils.IsStringNullOrWhitespace(this._filterClause))
            //{
            //    StringBuilder stringBuilder1 = new StringBuilder();
            //    stringBuilder1.Append("((").Append(this._filterClause).Append(") OR (");
            //    stringBuilder1.Append(SqlSyncProcedureHelper.TrackingTableAlias).Append(".").Append(this._trackingColNames.SyncRowIsTombstone).Append(" = 1 AND ");
            //    stringBuilder1.Append("(");
            //    stringBuilder1.Append(SqlSyncProcedureHelper.TrackingTableAlias).Append(".").Append(this._trackingColNames.UpdateScopeLocalId).Append(" = ").Append(sqlParameter.ParameterName);
            //    stringBuilder1.Append(" OR ");
            //    stringBuilder1.Append(SqlSyncProcedureHelper.TrackingTableAlias).Append(".").Append(this._trackingColNames.UpdateScopeLocalId).Append(" IS NULL");
            //    stringBuilder1.Append(") AND ");
            //    string empty1 = string.Empty;
            //    foreach (DbSyncColumnDescription _filterColumn in this._filterColumns)
            //    {
            //        stringBuilder1.Append(empty1).Append(SqlSyncProcedureHelper.TrackingTableAlias).Append(".").Append(_filterColumn.QuotedName).Append(" IS NULL");
            //        empty1 = " AND ";
            //    }
            //    stringBuilder1.Append("))");
            //    stringBuilder.Append(stringBuilder1.ToString());
            //    str = " AND ";
            //}

            stringBuilder.AppendLine("\t-- Update made by the local instance");
            stringBuilder.AppendLine("\t[side].[update_scope_id] IS NULL");
            stringBuilder.AppendLine("\t-- Or Update different from remote");
            stringBuilder.AppendLine("\tOR [side].[update_scope_id] <> @sync_scope_id");
            stringBuilder.AppendLine("    )");
            stringBuilder.AppendLine("AND (");
            stringBuilder.AppendLine("\t-- And Timestamp is > from remote timestamp");
            stringBuilder.AppendLine("\t[side].[timestamp] > @sync_min_timestamp");
            stringBuilder.AppendLine("\tOR");
            stringBuilder.AppendLine("\t-- remote instance is new, so we don't take the last timestamp");
            stringBuilder.AppendLine("\t@sync_scope_is_new = 1");
            stringBuilder.AppendLine("\t)");


            sqlCommand.CommandText = stringBuilder.ToString();
            //if (this._filterParameters != null)
            //{
            //    foreach (SqlParameter _filterParameter in this._filterParameters)
            //    {
            //        sqlCommand.Parameters.Add(((ICloneable)_filterParameter).Clone());
            //    }
            //}
            return sqlCommand;
        }
        public void CreateSelectIncrementalChanges()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.SelectChanges);
            CreateProcedureCommand(BuildSelectIncrementalChangesCommand, commandName);
        }
        public string CreateSelectIncrementalChangesScriptText()
        {
            var commandName = this.sqlObjectNames.GetCommandName(DbCommandType.SelectChanges);
            return CreateProcedureCommandScriptText(BuildSelectIncrementalChangesCommand, commandName);
        }



    }
}
