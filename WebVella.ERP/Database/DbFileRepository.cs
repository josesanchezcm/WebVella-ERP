﻿using System;
using System.Collections.Generic;
using Npgsql;
using System.Data;

namespace WebVella.ERP.Database
{
	public class DbFileRepository
	{
		private const string FOLDER_SEPARATOR = "/";
		private const string TMP_FOLDER_NAME = "tmp";

		public DbFile Find(string filepath)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			using (var connection = DbContext.Current.CreateConnection())
			{
				var command = connection.CreateCommand("SELECT * FROM files WHERE filepath = @filepath ");
				command.Parameters.Add(new NpgsqlParameter("@filepath", filepath));
				DataTable dataTable = new DataTable();
				new NpgsqlDataAdapter(command).Fill(dataTable);

				if (dataTable.Rows.Count == 1)
					return new DbFile(dataTable.Rows[0]);
			}

			return null;
		}

		public List<DbFile> FindAll(string startsWithPath = null, bool includeTempFiles = false, int? skip = null, int? limit = null)
		{
			//all filepaths are lowercase and all starts with folder separator
			if (!string.IsNullOrWhiteSpace(startsWithPath))
			{
				startsWithPath = startsWithPath.ToLowerInvariant();

				if (!startsWithPath.StartsWith(FOLDER_SEPARATOR))
					startsWithPath = FOLDER_SEPARATOR + startsWithPath;
			}

			string pagingSql = string.Empty;
			if (limit != null || skip != null)
			{
				pagingSql = " LIMIT ";
				if (limit.HasValue)
					pagingSql = pagingSql + limit + " ";
				else
					pagingSql = pagingSql + "ALL ";

				if (skip.HasValue)
					pagingSql = pagingSql + " OFFSET " + skip;
			}

			DataTable table = new DataTable();
			using (var connection = DbContext.Current.CreateConnection())
			{
				var command = connection.CreateCommand(string.Empty);
				if (!includeTempFiles && !string.IsNullOrWhiteSpace(startsWithPath))
				{
					command.CommandText = "SELECT * FROM files WHERE filepath NOT ILIKE @tmp_path AND filepath ILIKE @startswith" + pagingSql;
					command.Parameters.Add(new NpgsqlParameter("@tmp_path", "%" + FOLDER_SEPARATOR + TMP_FOLDER_NAME));
					command.Parameters.Add(new NpgsqlParameter("@startswith", "%" + startsWithPath));
					new NpgsqlDataAdapter(command).Fill(table);
				}
				else if (!string.IsNullOrWhiteSpace(startsWithPath))
				{
					command.CommandText = "SELECT * FROM files WHERE filepath ILIKE @startswith" + pagingSql;
					command.Parameters.Add(new NpgsqlParameter("@startswith", "%" + startsWithPath));
					new NpgsqlDataAdapter(command).Fill(table);
				}
				else if (!includeTempFiles)
				{
					command.CommandText = "SELECT * FROM files WHERE filepath NOT ILIKE @tmp_path " + pagingSql;
					command.Parameters.Add(new NpgsqlParameter("@tmp_path", "%" + FOLDER_SEPARATOR + TMP_FOLDER_NAME));
					new NpgsqlDataAdapter(command).Fill(table);
				}
				else
				{
					command.CommandText = "SELECT * FROM files " + pagingSql;
					new NpgsqlDataAdapter(command).Fill(table);
				}
			}

			List<DbFile> files = new List<DbFile>();
			foreach (DataRow row in table.Rows)
				files.Add(new DbFile(row));

			return files;
		}

		public DbFile Create(string filepath, byte[] buffer, DateTime? createdOn, Guid? createdBy)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			if (Find(filepath) != null)
				throw new ArgumentException(filepath + ": file already exists");

			using (var connection = DbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();

					var manager = new NpgsqlLargeObjectManager(connection.connection);
					uint objectId = manager.Create();

					using (var stream = manager.OpenReadWrite(objectId))
					{
						stream.Write(buffer, 0, buffer.Length);
						stream.Close();
					}


					var command = connection.CreateCommand(@"INSERT INTO files(id,object_id,filepath,created_on,modified_on,created_by,modified_by) 
															 VALUES (@id,@object_id,@filepath,@created_on,@modified_on,@created_by,@modified_by)");

					command.Parameters.Add(new NpgsqlParameter("@id", Guid.NewGuid()));
					command.Parameters.Add(new NpgsqlParameter("@object_id", (decimal)objectId));
					command.Parameters.Add(new NpgsqlParameter("@filepath", Guid.NewGuid()));
					var date = createdOn ?? DateTime.UtcNow;
					command.Parameters.Add(new NpgsqlParameter("@created_on", date));
					command.Parameters.Add(new NpgsqlParameter("@modified_on", date));
					command.Parameters.Add(new NpgsqlParameter("@created_by", createdBy));
					command.Parameters.Add(new NpgsqlParameter("@modified_by", createdBy));

					command.ExecuteNonQuery();

					var result = Find(filepath);

					connection.CommitTransaction();
				}
				catch
				{
					connection.RollbackTransaction();
				}
			}

			return Find(filepath);
		}

		public DbFile UpdateModificationDate(string filepath, DateTime modificationDate)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			using (var connection = DbContext.Current.CreateConnection())
			{
				var file = Find(filepath);
				if (file == null)
					throw new ArgumentException("file does not exist");

				var command = connection.CreateCommand(@"UPDATE files SET modified_on = @modified_on WHERE id = @id");
				command.Parameters.Add(new NpgsqlParameter("@id", Guid.NewGuid()));
				command.Parameters.Add(new NpgsqlParameter("@modified_on", modificationDate));
				command.ExecuteNonQuery();

				return Find(filepath);
			}
		}

		/// <summary>
		/// copy file from source to destination location
		/// </summary>
		/// <param name="sourceFilepath"></param>
		/// <param name="destinationFilepath"></param>
		/// <param name="overwrite"></param>
		/// <returns></returns>
		public DbFile Copy(string sourceFilepath, string destinationFilepath, bool overwrite = false)
		{
			if (string.IsNullOrWhiteSpace(sourceFilepath))
				throw new ArgumentException("sourceFilepath cannot be null or empty");

			if (string.IsNullOrWhiteSpace(destinationFilepath))
				throw new ArgumentException("destinationFilepath cannot be null or empty");

			sourceFilepath = sourceFilepath.ToLowerInvariant();
			destinationFilepath = destinationFilepath.ToLowerInvariant();

			if (!sourceFilepath.StartsWith(FOLDER_SEPARATOR))
				sourceFilepath = FOLDER_SEPARATOR + sourceFilepath;

			if (!destinationFilepath.StartsWith(FOLDER_SEPARATOR))
				destinationFilepath = FOLDER_SEPARATOR + destinationFilepath;

			var srcFile = Find(sourceFilepath);
			var destFile = Find(destinationFilepath);

			if (srcFile == null)
				throw new Exception("Source file cannot be found.");

			if (destFile != null && overwrite == false)
				throw new Exception("Destination file already exists and no overwrite specified.");

			using (var connection = DbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();

					if (destFile != null && overwrite)
						Delete(destFile.FilePath);

					var bytes = srcFile.GetBytes(connection);
					var newFile = Create(destinationFilepath, bytes, srcFile.CreatedOn, srcFile.CreatedBy);

					connection.CommitTransaction();
					return newFile;
				}
				catch
				{
					connection.RollbackTransaction();
					throw;
				}
			}
		}

		/// <summary>
		/// moves file from source to destination location
		/// </summary>
		/// <param name="sourceFilepath"></param>
		/// <param name="destinationFilepath"></param>
		/// <param name="overwrite"></param>
		/// <returns></returns>
		public DbFile Move(string sourceFilepath, string destinationFilepath, bool overwrite = false)
		{
			if (string.IsNullOrWhiteSpace(sourceFilepath))
				throw new ArgumentException("sourceFilepath cannot be null or empty");

			if (string.IsNullOrWhiteSpace(destinationFilepath))
				throw new ArgumentException("destinationFilepath cannot be null or empty");

			sourceFilepath = sourceFilepath.ToLowerInvariant();
			destinationFilepath = destinationFilepath.ToLowerInvariant();

			if (!sourceFilepath.StartsWith(FOLDER_SEPARATOR))
				sourceFilepath = FOLDER_SEPARATOR + sourceFilepath;

			if (!destinationFilepath.StartsWith(FOLDER_SEPARATOR))
				destinationFilepath = FOLDER_SEPARATOR + destinationFilepath;

			var srcFile = Find(sourceFilepath);
			var destFile = Find(destinationFilepath);

			if (srcFile == null)
				throw new Exception("Source file cannot be found.");

			if (destFile != null && overwrite == false)
				throw new Exception("Destination file already exists and no overwrite specified.");

			using (var connection = DbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();

					if (destFile != null && overwrite)
						Delete(destFile.FilePath);

					var command = connection.CreateCommand(@"UPDATE files SET filepath = @filepath WHERE id = @id");
					command.Parameters.Add(new NpgsqlParameter("@id", srcFile.Id));
					command.Parameters.Add(new NpgsqlParameter("@filepath", destinationFilepath));
					command.ExecuteNonQuery();

					connection.CommitTransaction();
					return Find(destinationFilepath);
				}
				catch
				{
					connection.RollbackTransaction();
					throw;
				}
			}
		}


		/// <summary>
		/// deletes file
		/// </summary>
		/// <param name="filepath"></param>
		public void Delete(string filepath)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			var file = Find(filepath);

			if (file == null)
				return;

			using (var connection = DbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();

					new NpgsqlLargeObjectManager(connection.connection).Unlink(file.ObjectId);

					var command = connection.CreateCommand(@"DELETE FROM files WHERE id = @id");
					command.Parameters.Add(new NpgsqlParameter("@id", file.Id));
					command.ExecuteNonQuery();

					connection.CommitTransaction();
				}
				catch
				{
					connection.RollbackTransaction();
					throw;
				}
			}
		}

		/// <summary>
		/// create temp file
		/// </summary>
		/// <param name="buffer"></param>
		/// <param name="extension"></param>
		/// <returns></returns>
		public DbFile CreateTempFile(string filename, byte[] buffer, string extension = null)
		{
			if (!string.IsNullOrWhiteSpace(extension))
			{
				extension = extension.Trim().ToLowerInvariant();
				if (!extension.StartsWith("."))
					extension = "." + extension;
			}

			string section = Guid.NewGuid().ToString().Replace("-", "").ToLowerInvariant();
			var tmpFilePath = FOLDER_SEPARATOR + TMP_FOLDER_NAME + FOLDER_SEPARATOR + section + FOLDER_SEPARATOR + filename + extension ?? string.Empty;
			return Find(tmpFilePath);
		}

		/// <summary>
		/// cleanup expired temp files 
		/// </summary>
		/// <param name="expiration"></param>
		public void CleanupExpiredTempFiles(TimeSpan expiration)
		{

			DataTable table = new DataTable();
			using (var connection = DbContext.Current.CreateConnection())
			{
				var command = connection.CreateCommand(string.Empty);
				command.CommandText = "SELECT filepath FROM files WHERE filepath ILIKE @tmp_path";
				command.Parameters.Add(new NpgsqlParameter("@tmp_path", "%" + FOLDER_SEPARATOR + TMP_FOLDER_NAME));
				new NpgsqlDataAdapter(command).Fill(table);
			}

			foreach (DataRow row in table.Rows)
				Delete((string)row["filepath"]);
		}

	}
}
