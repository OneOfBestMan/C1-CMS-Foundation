﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using Composite.Core;
using Composite.Core.Extensions;
using Composite.Core.Logging;
using Composite.Core.Sql;
using Composite.Core.Types;
using Composite.Data;
using Composite.Data.DynamicTypes;

namespace Composite.Plugins.Data.DataProviders.MSSqlServerDataProvider.Foundation
{
	internal sealed class SqlDataProviderStoreManipulator
	{
		private static object _lock = new object();

		private readonly string _connectionString;
		private readonly List<InterfaceConfigurationElement> _generatedInterfaces;

		internal SqlDataProviderStoreManipulator(string connectionString, List<InterfaceConfigurationElement> generatedInterfaces)
		{
			Verify.ArgumentNotNullOrEmpty(connectionString, "connectionString");
			Verify.ArgumentNotNull(generatedInterfaces, "generatedInterfaces");

			_connectionString = connectionString;
			_generatedInterfaces = generatedInterfaces;
		}

		internal void CreateStoresForType(DataTypeDescriptor typeDescriptor)
		{
			lock (_lock)
			{
				foreach (DataScopeIdentifier dataScope in typeDescriptor.DataScopes)
				{
					foreach (var culture in GetCultures(typeDescriptor))
					{
						CreateStore(typeDescriptor, dataScope, culture);
					}
				}
			}
		}

		internal void AddLocale(DataTypeDescriptor typeDescriptor, CultureInfo cultureInfo)
		{
			foreach (DataScopeIdentifier dataScope in typeDescriptor.DataScopes)
			{
				CreateStore(typeDescriptor, dataScope, cultureInfo);
			}
		}

		internal void RemoveLocale(string providerName, DataTypeDescriptor typeDescriptor, CultureInfo cultureInfo)
		{
			foreach (DataScopeIdentifier dataScope in typeDescriptor.DataScopes)
			{
				DropStore(typeDescriptor, dataScope, cultureInfo);
			}
		}

		internal static IEnumerable<CultureInfo> GetCultures(DataTypeDescriptor typeDescriptor)
		{
			if (typeDescriptor.Localizeable)
			{
				foreach (var culture in DataLocalizationFacade.ActiveLocalizationCultures)
				{
					yield return culture;
				}
			}
			else
			{
				yield return CultureInfo.InvariantCulture;
			}
		}

		private void CreateScopeData(DataTypeDescriptor typeDescriptor, DataScopeIdentifier dataScope)
		{
			foreach (var cultureInfo in GetCultures(typeDescriptor))
			{
				CreateStore(typeDescriptor, dataScope, cultureInfo);
			}
		}

		internal void CreateStore(DataTypeDescriptor typeDescriptor, DataScopeIdentifier dataScope, CultureInfo cultureInfo)
		{
			string tableName = DynamicTypesCommon.GenerateTableName(typeDescriptor, dataScope, cultureInfo);
			var tables = GetTablesList();

			if (tables.Contains(tableName))
			{
				throw new InvalidOperationException(
					"Database already contains a table named {0}".FormatWith(tableName));
			}

			var sql = new StringBuilder();
			var sqlColumns = typeDescriptor.Fields.Select(fieldDescriptor => GetColumnInfo(tableName, fieldDescriptor.Name, fieldDescriptor, true)).ToList();

			sql.AppendFormat("CREATE TABLE {0}({1});", tableName, string.Join(",", sqlColumns));
			sql.Append(SetPrimaryKey(tableName, typeDescriptor.KeyPropertyNames, (typeDescriptor.HasCustomPhysicalSortOrder == false)));

			try
			{
				ExecuteNonQuery(sql.ToString());
			}
			catch (Exception ex)
			{
				throw MakeVerboseException(ex);
			}
		}

		internal List<string> GetTablesList()
		{
			string sql = @"
				SELECT t.Name FROM sysobjects s
				INNER JOIN sysobjects t ON s.parent_obj = t.id
				WHERE t.xtype = 'U'";
			DataTable dt = ExecuteReader(sql);
			List<string> tables = (from DataRow dr in dt.Rows select dr["Name"].ToString()).ToList();

			return tables;
		}

		#region Db helpers
		public DataTable ExecuteReader(string commandText)
		{
			var conn = SqlConnectionManager.GetConnection(_connectionString);

			using (var cmd = new SqlCommand(commandText, conn))
			{
				using (var dt = new DataTable())
				{
					using (var rdr = cmd.ExecuteReader())
					{
						if (rdr != null) dt.Load(rdr);
						return dt;
					}
				}
			}
		}

		public void ExecuteNonQuery(string commandText)
		{
		    if (string.IsNullOrEmpty(commandText))
		    {
		        return;
		    }

		    Log.LogInformation("ExecuteNonQuery", commandText);

		    var conn = SqlConnectionManager.GetConnection(_connectionString);
		    using (var cmd = new SqlCommand(commandText, conn))
		    {
		        cmd.ExecuteNonQuery();
		    }
		}

		public void ExecuteNonQuery(SqlCommand cmd)
		{
			var conn = SqlConnectionManager.GetConnection(_connectionString);
			cmd.Connection = conn;
			cmd.ExecuteNonQuery();
		}

		public void ExecuteStoredProcedure(string spName, string[] spParams)
		{
			string sql = string.Format("{0} {1}", spName, string.Join(",", spParams));

			ExecuteNonQuery(sql);
		}

		#endregion


		private string GetConfiguredTableName(string dataTypeName, DataScopeIdentifier dataScope, string cultureName)
		{
			string normalizedTypeName = TryNormalizeTypeFullName(dataTypeName);

			var stores =
				(from dataInterface in _generatedInterfaces
				 where dataInterface.InterfaceType == normalizedTypeName
				 select dataInterface.Stores).FirstOrDefault();

			if (stores == null)
			{
				return null;
			}

			var tableName = (from store in stores
							 where store.CultureName == cultureName && store.DataScope == dataScope.Name
							 select store.TableName).FirstOrDefault();
			return tableName;
		}


		internal string TryNormalizeTypeFullName(string typeName)
		{
			Type type = TypeManager.TryGetType(typeName);

			if (type != null)
			{
				return TypeManager.TrySerializeType(type);
			}
			return typeName;
		}


		internal void AlterStoresForType(string providerName, DataTypeChangeDescriptor changeDescriptor)
		{
			lock (_lock)
			{
				foreach (DataScopeIdentifier dataScope in changeDescriptor.ExistingDataScopes)
				{
					AlterScopeData(changeDescriptor, dataScope);
				}

				foreach (DataScopeIdentifier dataScope in changeDescriptor.AddedDataScopes)
				{
					CreateScopeData(changeDescriptor.AlteredType, dataScope);
				}

				foreach (DataScopeIdentifier dataScope in changeDescriptor.DeletedDataScopes)
				{
					DropScopeData(changeDescriptor.AlteredType, dataScope);
				}
			}
		}

		private void AlterScopeData(DataTypeChangeDescriptor changeDescriptor, DataScopeIdentifier dataScope)
		{
			var culturesToDelete = new List<CultureInfo>();
			var culturesToChange = new List<CultureInfo>();

			var oldCultures = GetCultures(changeDescriptor.OriginalType);
			var newCultures = GetCultures(changeDescriptor.AlteredType);

			foreach (var culture in oldCultures)
			{
				if (newCultures.Contains(culture))
				{
					culturesToChange.Add(culture);
				}
				else
				{
					culturesToDelete.Add(culture);
				}
			}

			var culturesToAdd = newCultures.Where(culture => !oldCultures.Contains(culture)).ToList();

			culturesToChange.ForEach(culture => AlterStore(changeDescriptor, dataScope, culture));
			culturesToAdd.ForEach(culture => CreateStore(changeDescriptor.AlteredType, dataScope, culture));
			culturesToDelete.ForEach(culture => DropStore(changeDescriptor.OriginalType, dataScope, culture));
		}

		private void AlterStore(DataTypeChangeDescriptor changeDescriptor, DataScopeIdentifier dataScope, CultureInfo culture)
		{
			try
			{
				string originalTableName = GetConfiguredTableName(changeDescriptor.OriginalType.TypeManagerTypeName, dataScope, culture.Name);
				string alteredTableName = DynamicTypesCommon.GenerateTableName(changeDescriptor.AlteredType, dataScope, culture);
				var tables = GetTablesList();

				if (!tables.Contains(originalTableName))
				{
					throw new InvalidOperationException(
						string.Format(
							"Unable to alter data type store. The database does not contain expected table {0}",
							originalTableName));
				}

				DropAllConstraintsExceptPrimaryKey(originalTableName);

				if (originalTableName != alteredTableName)
				{
					if (tables.Contains(alteredTableName))
						throw new InvalidOperationException(
							string.Format("Can not rename table to {0}. A table with that name already exists",
										  alteredTableName));
					RenameTable(originalTableName, alteredTableName);
				}
				DropFields(alteredTableName, changeDescriptor.DeletedFields, changeDescriptor.OriginalType.Fields);
				ImplementFieldChanges(alteredTableName, changeDescriptor.ExistingFields);
				AppendFields(alteredTableName, changeDescriptor.OriginalType.Fields, changeDescriptor.AddedFields);

				//string sql = SetPrimaryKey(alteredTableName, changeDescriptor.AlteredType.KeyPropertyNames, (changeDescriptor.AlteredType.HasCustomPhysicalSortOrder == false));
				//ExecuteNonQuery(sql);
			}
			catch (Exception ex)
			{
				throw MakeVerboseException(ex);
			}

		}

		internal void RenameTable(string oldTableName, string newTableName)
		{
			ExecuteStoredProcedure("sp_rename", new[] { oldTableName, newTableName });
		}

		internal void DropStoresForType(string providerName, DataTypeDescriptor typeDescriptor)
		{
			lock (_lock)
			{
				foreach (DataScopeIdentifier dataScope in typeDescriptor.DataScopes)
				{
					DropScopeData(typeDescriptor, dataScope);
				}
			}
		}

		private void DropScopeData(DataTypeDescriptor typeDescriptor, DataScopeIdentifier dataScope)
		{
			foreach (var culture in GetCultures(typeDescriptor))
			{
				DropStore(typeDescriptor, dataScope, culture);
			}
		}

		private void DropStore(DataTypeDescriptor typeDescriptor, DataScopeIdentifier dataScope, CultureInfo cultureInfo)
		{
			string tableName = GetConfiguredTableName(typeDescriptor.TypeManagerTypeName, dataScope, cultureInfo.Name);

			try
			{
				if (!string.IsNullOrEmpty(tableName))
				{
					var tables = GetTablesList();

					if (tables.Contains(tableName))
					{
						ExecuteNonQuery(string.Format("DROP TABLE {0};", tableName));
					}
				}
			}
			catch (Exception ex)
			{
				throw MakeVerboseException(ex);
			}
		}

		private void ImplementFieldChanges(string tableName, IEnumerable<DataTypeChangeDescriptor.ExistingFieldInfo> existingFieldDescription)
		{
            foreach (var changedFieldDescriptor in existingFieldDescription)
			{
                // Recreating deleted contraints, if necessary - renaming the column/changing its type
			    bool changes = changedFieldDescriptor.AlteredFieldHasChanges;
				var columnName = changedFieldDescriptor.OriginalField.Name;

				ConfigureColumn(tableName, columnName, changedFieldDescriptor.AlteredField, changes);
			}
		}


		internal void RenameColumn(string tableName, string oldColumnName, string newColumnName)
		{
			string oldName = string.Format("'{0}.{1}'", tableName, oldColumnName);
			ExecuteStoredProcedure("sp_rename", new[] { oldName, newColumnName, "'COLUMN'" });
		}

		private void DropFields(string tableName, IEnumerable<DataFieldDescriptor> fieldsToDrop, IEnumerable<DataFieldDescriptor> fields)
		{
			var sql = new StringBuilder();

			foreach (var deletedFieldDescriptor in fieldsToDrop)
			{
				var column = fields.Where(f => f.Name.Equals(deletedFieldDescriptor.Name)).Any();

				if (column)
				{
					sql.AppendFormat("ALTER TABLE {0} DROP COLUMN {1};", tableName, deletedFieldDescriptor.Name);
				}
				else
				{
					LoggingService.LogWarning(typeof(SqlDataProvider).FullName,
						"Column '{0}' on table '{1}' has already been dropped".FormatWith(deletedFieldDescriptor.Name, tableName));
				}
			}

			ExecuteNonQuery(sql.ToString());
		}

		private void AppendFields(string tableName, IEnumerable<DataFieldDescriptor> originalFieldDescriptions, IEnumerable<DataFieldDescriptor> addedFieldDescriptions)
		{
			foreach (var addedFieldDescriptor in addedFieldDescriptions)
			{
				var originalColumn = originalFieldDescriptions.Where(f => f.Name.Equals(addedFieldDescriptor.Name)).SingleOrDefault();
				if (originalColumn != null)
				{
					ConfigureColumn(tableName, originalColumn.Name, addedFieldDescriptor, true);
				}
				else
				{
					CreateColumn(tableName, addedFieldDescriptor);
				}
			}
		}

		private IEnumerable<string> GetConstraints(string tableName, string constraintType = null)
		{
			/*
				This is the list of all possible values for this column (xtype):
				C = CHECK constraint 
				D = Default or DEFAULT constraint 
				F = FOREIGN KEY constraint 
				L = Log 
				P = Stored procedure 
				PK = PRIMARY KEY constraint (type is K) 
				RF = Replication filter stored procedure 
				S = System table 
				TR = Trigger 
				U = User table 
				UQ = UNIQUE constraint (type is K) 
				V = View 
				X = Extended stored procedure
			*/
			string type = string.IsNullOrEmpty(constraintType) ? string.Empty : string.Format(" AND s.xtype = '{0}'", constraintType);

			string commandText = string.Format(@"
				SELECT * FROM sysobjects s
				INNER JOIN sysobjects t ON s.parent_obj = t.id
				WHERE t.name = '{0}'{1}", tableName, type);

			var dt = ExecuteReader(commandText);
			var constraints = (from DataRow dr in dt.Rows select dr["Name"].ToString()).ToList();

			return constraints;
		}

        private void DropAllConstraintsExceptPrimaryKey(string tableName)
		{
			var sql = new StringBuilder();
			var constraints = GetConstraints(tableName);

			foreach (var constraint in constraints)
			{
				if(!IsPrimaryKeyContraint(constraint))
				{
                    sql.AppendFormat("ALTER TABLE {0} DROP CONSTRAINT {1};", tableName, constraint);
				}
			}

			ExecuteNonQuery(sql.ToString());
		}

        internal string SetPrimaryKey(string tableName, IEnumerable<string> fieldNames, bool createAsClustered)
        {
            if (!fieldNames.Any())
            {
                return string.Empty;
            }

            string primaryKeyIndexName = GeneratePrimaryKeyContraintName(tableName);

            return string.Format("ALTER TABLE {0} ADD CONSTRAINT {1} PRIMARY KEY{2}({3});", tableName, primaryKeyIndexName,
                                    createAsClustered ? " CLUSTERED " : string.Empty, string.Join(",", fieldNames.Distinct()));
        }

	    private Exception MakeVerboseException(Exception ex)
		{
			var message = new StringBuilder();
			Exception nested = ex;
			while (nested != null)
			{
				message.Append(nested.Message);
				message.Append(" ");
				nested = nested.InnerException;
			}
			return new InvalidOperationException(message.ToString(), ex);
		}

		private void CreateColumn(string tableName, DataFieldDescriptor fieldDescriptor)
		{
			var sql = new StringBuilder();
			string columnInfo = GetColumnInfo(tableName, fieldDescriptor.Name, fieldDescriptor, true);

			sql.AppendFormat("ALTER TABLE {0} ADD {1};", tableName, columnInfo);
			ExecuteNonQuery(sql.ToString());
		}

		private void ConfigureColumn(string tableName, string columnName, DataFieldDescriptor fieldDescriptor, bool changes)
		{
			if (columnName != fieldDescriptor.Name)
			{
				RenameColumn(tableName, columnName, fieldDescriptor.Name);
			}

            if (changes)
			{
				ExecuteNonQuery(string.Format("ALTER TABLE {0} ALTER COLUMN {1};", tableName, GetColumnInfo(tableName, fieldDescriptor.Name, fieldDescriptor, false)));
			}

            ExecuteNonQuery(SetDefaultValue(tableName, fieldDescriptor.Name, fieldDescriptor.DefaultValue));
		}

		internal string GetColumnInfo(string tableName, string columnName, DataFieldDescriptor fieldDescriptor, bool includeDefault)
		{
			string defaultInfo = string.Empty;

			if (fieldDescriptor.DefaultValue != null)
			{
				if (includeDefault)
				{
					defaultInfo = string.Format("CONSTRAINT {0} DEFAULT {1}", SqlSafeName("DF", tableName, columnName), GetDefaultValueText(fieldDescriptor.DefaultValue));
				}
			}

			return string.Format(
				"[{0}] {1} {2} {3}",
				fieldDescriptor.Name,
				DynamicTypesCommon.MapStoreTypeToSqlDataType(fieldDescriptor.StoreType),
				fieldDescriptor.IsNullable ? "NULL" : "NOT NULL",
				defaultInfo);
		}

		private string SetDefaultValue(string tableName, string columnName, DefaultValue defaultValue)
		{
			if (defaultValue == null)
				return string.Empty;

			string constraintName = SqlSafeName("DF", tableName, columnName);
			return string.Format("ALTER TABLE {0} ADD CONSTRAINT {1} DEFAULT {2} FOR {3};", tableName, constraintName, GetDefaultValueText(defaultValue), columnName);
		}

		private string GetDefaultValueText(DefaultValue defaultValue)
		{
			switch (defaultValue.ValueType)
			{
				case DefaultValueType.DateTimeNow:
					return "getdate()";
				case DefaultValueType.String:
				case DefaultValueType.Guid:
					return "N" + SqlQuoted(defaultValue.Value);
				case DefaultValueType.NewGuid:
					return "newid()";
				case DefaultValueType.Integer:
					return defaultValue.Value.ToString();
				case DefaultValueType.Boolean:
					return ((bool)defaultValue.Value ? "1" : "0");
				case DefaultValueType.DateTime:
					return SqlQuoted(((DateTime)defaultValue.Value).ToString("yyyy-MM-dd HH:mm:ss"));
				case DefaultValueType.Decimal:
					return ((decimal)defaultValue.Value).ToString("F", CultureInfo.InvariantCulture);
			}

			throw new NotImplementedException("Supplied DefaultValue contains an unsupported DefaultValueType.");
		}

		private string SqlQuoted(object obj)
		{
			return SqlQuoted(obj.ToString());
		}

		private static string SqlQuoted(string theString)
		{
			return string.Format("'{0}'", theString.Replace("'", "''"));
		}

        private static bool IsPrimaryKeyContraint(string contraintName)
        {
            return contraintName.StartsWith("PK_");
        }

        private static string GeneratePrimaryKeyContraintName(string tableName)
        {
            return SqlSafeName("PK", tableName);
        }

		private static string SqlSafeName(string prefix, string elementName)
		{
			string name = string.Format("{0}_{1}", prefix, elementName);

			if (name.Length > 128)
			{

				string random = System.IO.Path.GetRandomFileName();
				name = name.Substring(0, 128 - random.Length) + random;
			}

			return name;
		}

		private static string SqlSafeName(string prefix, string parentName, string subName)
		{
			string stem = string.Format("{0}_{1}_", prefix, parentName);

			if (stem.Length + subName.Length > 128)
			{
				stem = stem.Substring(0, 127 - subName.Length) + "_";
			}

			return stem + subName;
		}
	}
}