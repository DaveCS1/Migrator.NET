#region License

//The contents of this file are subject to the Mozilla Public License
//Version 1.1 (the "License"); you may not use this file except in
//compliance with the License. You may obtain a copy of the License at
//http://www.mozilla.org/MPL/
//Software distributed under the License is distributed on an "AS IS"
//basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See the
//License for the specific language governing rights and limitations
//under the License.

#endregion

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

using Migrator.Framework;

namespace Migrator.Providers.PostgreSQL
{
	/// <summary>
	/// Migration transformations provider for PostgreSql (using NPGSql .Net driver)
	/// </summary>
	public class PostgreSQLTransformationProvider : TransformationProvider
	{
		public PostgreSQLTransformationProvider(Dialect dialect, string connectionString, string defaultSchema, string scope, string providerName)
			: base(dialect, connectionString, defaultSchema, scope)
		{
			if (string.IsNullOrEmpty(providerName)) providerName = "Npgsql";
			var fac = DbProviderFactoriesHelper.GetFactory(providerName, "Npgsql", "Npgsql.NpgsqlFactory");
			_connection = fac.CreateConnection(); //new NpgsqlConnection();
			_connection.ConnectionString = _connectionString;
			_connection.Open();
		}

		public PostgreSQLTransformationProvider(Dialect dialect, IDbConnection connection, string defaultSchema, string scope, string providerName)
		   : base(dialect, connection, defaultSchema, scope)
		{
		}

		public override Index[] GetIndexes(string table)
		{
			var retVal = new List<Index>();

			var sql = @"
SELECT * FROM (
SELECT i.relname as indname,
       idx.indisprimary,
       idx.indisunique,
       i.relowner as indowner,
       cast(idx.indrelid::regclass as varchar) as tablenm,
       am.amname as indam,
       idx.indkey,
       ARRAY(
       SELECT pg_get_indexdef(idx.indexrelid, k + 1, true)
       FROM generate_subscripts(idx.indkey, 1) as k
       ORDER BY k
       ) as indkey_names,
       idx.indexprs IS NOT NULL as indexprs,
       idx.indpred IS NOT NULL as indpred
FROM   pg_index as idx
JOIN   pg_class as i
ON     i.oid = idx.indexrelid
JOIN   pg_am as am
ON     i.relam = am.oid
JOIN   pg_namespace as ns
ON     ns.oid = i.relnamespace
AND    ns.nspname = ANY(current_schemas(false))) AS t
WHERE  lower(tablenm) = lower('{0}')
;";



			using (var reader = ExecuteQuery(string.Format(sql, table)))
			{
				while (reader.Read())
				{
					if (!reader.IsDBNull(1))
					{
						var idx = new Index
						{
							Name = reader.GetString(0),
							PrimaryKey = reader.GetBoolean(1),
							Unique = reader.GetBoolean(2),
						};
						//var cols = reader.GetString(7);
						//cols = cols.Substring(1, cols.Length - 2);
						//idx.KeyColumns = cols.Split(',');                        
						retVal.Add(idx);
					}
				}
			}

			return retVal.ToArray();
		}

		public override void RemoveTable(string name)
		{
			if (!TableExists(name))
			{
				throw new MigrationException(String.Format("Table with name '{0}' does not exist to rename", name));
			}

			ExecuteNonQuery(String.Format("DROP TABLE IF EXISTS {0} CASCADE", name));
		}

		public override bool ConstraintExists(string table, string name)
		{
			using (IDataReader reader =
				ExecuteQuery(string.Format("SELECT constraint_name FROM information_schema.table_constraints WHERE table_schema = 'public' AND constraint_name = lower('{0}')", name)))
			{
				return reader.Read();
			}
		}

		public override bool ColumnExists(string table, string column)
		{
			if (!TableExists(table))
				return false;

			using (IDataReader reader =
				ExecuteQuery(String.Format("SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = lower('{0}') AND (column_name = lower('{1}') OR column_name = '{1}')", table, column)))
			{
				return reader.Read();
			}
		}

		public override bool TableExists(string table)
		{
			using (IDataReader reader =
				ExecuteQuery(String.Format("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name = lower('{0}')", table)))
			{
				return reader.Read();
			}
		}

		public override List<string> GetDatabases()
		{
			return ExecuteStringQuery("SELECT datname FROM pg_database WHERE datistemplate = false");
		}

		public override void ChangeColumn(string table, Column column)
		{
			var oldColumn = GetColumnByName(table, column.Name);

			var isUniqueSet = column.ColumnProperty.IsSet(ColumnProperty.Unique);

			column.ColumnProperty = column.ColumnProperty.Clear(ColumnProperty.Unique);

			if (!ColumnExists(table, column.Name))
			{
				Logger.Warn("Column {0}.{1} does not exist", table, column.Name);
				return;
			}

			ColumnPropertiesMapper mapper = _dialect.GetAndMapColumnProperties(column);

			string change1 = string.Format("{0} TYPE {1}", QuoteColumnNameIfRequired(mapper.Name), mapper.type);

			#region Field Type Converters...
			if ((oldColumn.Type == DbType.Int16 || oldColumn.Type == DbType.Int32 || oldColumn.Type == DbType.Int64 || oldColumn.Type == DbType.Decimal) && column.Type == DbType.Boolean)
			{
				change1 += string.Format(" USING CASE {0} WHEN 1 THEN true ELSE false END", QuoteColumnNameIfRequired(mapper.Name));
			}
			else if (column.Type == DbType.Boolean)
			{
				change1 += string.Format(" USING CASE {0} WHEN '1' THEN true ELSE false END", QuoteColumnNameIfRequired(mapper.Name));
			}
			#endregion

			ChangeColumn(table, change1);

			if (mapper.Default != null)
			{
				string change2 = string.Format("{0} SET {1}", QuoteColumnNameIfRequired(mapper.Name), _dialect.Default(mapper.Default));
				ChangeColumn(table, change2);
			}
			else
			{
				string change2 = string.Format("{0} DROP DEFAULT", QuoteColumnNameIfRequired(mapper.Name));
				ChangeColumn(table, change2);
			}

			if (column.ColumnProperty.HasFlag(ColumnProperty.NotNull))
			{
				string change3 = string.Format("{0} SET NOT NULL", QuoteColumnNameIfRequired(mapper.Name));
				ChangeColumn(table, change3);
			}
			else
			{
				string change3 = string.Format("{0} DROP NOT NULL", QuoteColumnNameIfRequired(mapper.Name));
				ChangeColumn(table, change3);
			}

			if (isUniqueSet)
			{
				AddUniqueConstraint(string.Format("UX_{0}_{1}", table, column.Name), table, new string[] { column.Name });
			}
		}

		public override void CreateDatabases(string databaseName)
		{
			ExecuteNonQuery(string.Format("CREATE DATABASE {0}", _dialect.Quote(databaseName)));
		}

		public override void SwitchDatabase(string databaseName)
		{
			_connection.ChangeDatabase(_dialect.Quote(databaseName));
		}

		public override void DropDatabases(string databaseName)
		{
			ExecuteNonQuery(string.Format("DROP DATABASE {0}", _dialect.Quote(databaseName)));
		}

		public override string[] GetTables()
		{
			var tables = new List<string>();
			using (IDataReader reader = ExecuteQuery("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"))
			{
				while (reader.Read())
				{
					tables.Add((string)reader[0]);
				}
			}
			return tables.ToArray();
		}

		public override Column[] GetColumns(string table)
		{
			var columns = new List<Column>();
			using (
				IDataReader reader =
					ExecuteQuery(
						String.Format("select COLUMN_NAME, IS_NULLABLE from information_schema.columns where table_schema = 'public' AND table_name = lower('{0}');", table)))
			{
				// FIXME: Mostly duplicated code from the Transformation provider just to support stupid case-insensitivty of Postgre
				while (reader.Read())
				{
					var column = new Column(reader[0].ToString(), DbType.String);
					bool isNullable = reader.GetString(1) == "YES";
					column.ColumnProperty |= isNullable ? ColumnProperty.Null : ColumnProperty.NotNull;

					columns.Add(column);
				}
			}

			return columns.ToArray();
		}

		public override Column GetColumnByName(string table, string columnName)
		{
			// Duplicate because of the lower case issue
			return Array.Find(GetColumns(table), column => column.Name == columnName.ToLower() || column.Name == columnName);
		}

		public override bool IndexExists(string table, string name)
		{
			using (IDataReader reader =
				ExecuteQuery(string.Format("SELECT indexname FROM pg_catalog.pg_indexes WHERE indexname = lower('{0}')", name)))
			{
				return reader.Read();
			}
		}

		protected override void ConfigureParameterWithValue(IDbDataParameter parameter, int index, object value)
		{
			if (value is UInt16)
			{
				parameter.DbType = DbType.Int32;
				parameter.Value = Convert.ToInt32(value);
			}
			else if (value is UInt32)
			{
				parameter.DbType = DbType.Int64;
				parameter.Value = Convert.ToInt64(value);
			}
			else
			{
				base.ConfigureParameterWithValue(parameter, index, value);
			}
		}
	}
}
